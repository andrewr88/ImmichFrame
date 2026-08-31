#!/bin/sh
set -e

DB="audit/audit.db"
SCHEMA="audit/schema.sql"
Q="sqlite3 -header -column"

# Language adapter: the core is language-agnostic; every language-specific
# decision (source discovery, priority scoring, verify commands, scan tools)
# lives in audit/adapters/<lang>.sh. Selected by AUDIT_ADAPTER (default "go").
AUDIT_ADAPTER="${AUDIT_ADAPTER:-go}"
ADAPTER_FILE="$(dirname "$0")/adapters/${AUDIT_ADAPTER}.sh"
if [ ! -f "$ADAPTER_FILE" ]; then
    echo "Audit adapter not found: $ADAPTER_FILE (set AUDIT_ADAPTER to a valid adapter)." >&2
    exit 1
fi
. "$ADAPTER_FILE"

usage() {
    cat <<'EOF'
Usage: audit/audit.sh <command>

Sweep & Fix:
  sweep [N]       Generate prompt to sweep next N files (default 5)
  deep-sweep [pkg] Generate prompt for package-level cross-cutting sweep
  arch-sweep [pkg] Generate architecture report + prompt (whole repo if omitted)
  fix N           Generate prompt to fix finding #N
  fix-all [file]  Generate prompt to fix all findings in a file

Status:
  status          Dashboard overview
  findings        All open findings
  packages        Package coverage + sweep status
  next            Next files to audit
  history         Recent runs and sweeps

Manage:
  finding N       Details for finding #N
  triage          Generate prompt to triage every open finding interactively
  resolve N       Mark finding #N as fixed
  wontfix N       Mark finding #N as wontfix
  sync            Refresh file list from repo
  scan            Run static analyzers and seed findings
  init [--force]  Create/rebuild database (--force or -y skips the rebuild prompt)

Examples:
  audit/audit.sh status
  audit/audit.sh sweep
  audit/audit.sh deep-sweep internal/web
  audit/audit.sh arch-sweep
  audit/audit.sh arch-sweep internal/web
  audit/audit.sh fix 3
EOF
}

check_db() {
    if [ ! -f "$DB" ]; then
        echo "No audit database found. Run: audit/audit.sh init"
        exit 1
    fi
}

# ensure_meta: create the meta table if it's missing. Safe to call
# repeatedly; the CREATE TABLE IF NOT EXISTS is a cheap no-op after the
# first call. Lets existing DBs absorb the schema change transparently.
ensure_meta() {
    sqlite3 "$DB" "CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL DEFAULT '');"
}

# current_head: git short SHA, empty if not in a git repo or git is
# unavailable. Used to detect when the tree has moved since the last
# sync or scan.
current_head() {
    git rev-parse --short HEAD 2>/dev/null || echo ""
}

# meta_get <key>: print the value or empty string.
meta_get() {
    sqlite3 "$DB" "SELECT value FROM meta WHERE key='$1';"
}

# meta_set <key> <value>: upsert a row. Values are short SHAs and small
# integers; no user-controlled input flows here, so plain interpolation
# is acceptable.
meta_set() {
    sqlite3 "$DB" "PRAGMA foreign_keys=ON; INSERT INTO meta (key, value) VALUES ('$1', '$2') ON CONFLICT(key) DO UPDATE SET value=excluded.value;"
}

# require_int <value> <usage>: CLI args interpolated into numeric SQL
# positions must be plain non-negative integers; anything else (or empty)
# prints the command's usage line and exits 1.
require_int() {
    case "$1" in
        ''|*[!0-9]*) echo "$2"; exit 1 ;;
    esac
}

# sql_escape <value>: double single quotes for safe SQL interpolation
# (same helper as scan.sh). For CLI args that flow into string literals.
sql_escape() {
    printf '%s' "$1" | sed "s/'/''/g"
}

# auto_sync: run cmd_sync if HEAD has moved since the last recorded
# sync. Silent on no-op and silent on sync itself — the next command's
# output will reflect the refreshed state. Skips when git is unavailable
# so we don't sync in an unknown state.
auto_sync() {
    check_db
    ensure_meta
    cur_head="$(current_head)"
    last_head="$(meta_get last_sync_head)"
    if [ -z "$cur_head" ] || [ "$cur_head" = "$last_head" ]; then
        return
    fi
    cmd_sync >/dev/null
    meta_set last_sync_head "$cur_head"
}

# auto_scan: increment the sweeps-since-scan counter; if it hits the
# configured interval, run scan and reset. First-ever call (no
# last_scan_head recorded) always runs a scan so the DB doesn't sit
# with zero static-tool findings. AUDIT_SCAN_INTERVAL=0 disables;
# AUDIT_SCAN_INTERVAL=1 scans every sweep; default is 10.
auto_scan() {
    check_db
    ensure_meta
    interval="${AUDIT_SCAN_INTERVAL:-10}"
    case "$interval" in
        ''|*[!0-9]*) interval=10 ;;
    esac
    if [ "$interval" = "0" ]; then
        return
    fi

    # scan_count, not "count": POSIX sh has no `local`, so a generic name
    # here would clobber the caller's variable (cmd_sweep's batch size was
    # bitten by exactly that). Same rationale for cmd_sync's `synced`.
    last_head="$(meta_get last_scan_head)"
    scan_count="$(meta_get sweeps_since_scan)"
    [ -z "$scan_count" ] && scan_count=0
    scan_count=$((scan_count + 1))

    if [ -z "$last_head" ] || [ "$scan_count" -ge "$interval" ]; then
        echo "Scan cadence reached (${interval} sweeps), running analyzers..." >&2
        if ! sh "$(dirname "$0")/scan.sh" >&2; then
            echo "auto_scan: scan.sh failed (continuing without fresh findings)" >&2
        fi
        meta_set last_scan_head "$(current_head)"
        meta_set sweeps_since_scan 0
        return
    fi

    meta_set sweeps_since_scan "$scan_count"
}

cmd_init() {
    # Fresh install: applies audit/schema.sql from scratch.
    # To upgrade an EXISTING database in-place without losing data, run
    # audit/migrate.sh instead — it applies new schema changes idempotently.
    if [ -f "$DB" ]; then
        case "${1:-}" in
            --force|-y)
                rm -f "$DB" "$DB-wal" "$DB-shm" ;;
            *)
                printf "Database exists. Rebuild from scratch? [y/N] "
                # EOF on stdin (scripted runs) would otherwise trip set -e;
                # treat it as the default N so we fall through to Aborted.
                read -r ans || ans=""
                case "$ans" in y|Y) rm -f "$DB" "$DB-wal" "$DB-shm" ;; *) echo "Aborted."; exit 0 ;; esac
                ;;
        esac
    fi
    sqlite3 "$DB" < "$SCHEMA"
    echo "Database created. Syncing files..."
    cmd_sync
}

cmd_sync() {
    check_db
    ensure_meta
    synced=0
    keep_values=""
    for path in $(audit_discover_sources | sort); do
        path="${path#./}"
        f="./$path"
        lines=$(wc -l < "$f")
        # git log exits 0 with EMPTY output for a path with no history (e.g.
        # staged but never committed), so the || fallback only covers git
        # failing outright; apply the sentinel to empty output explicitly.
        commit=$(git log -1 --format='%h' -- "$f" 2>/dev/null || echo 'unknown')
        [ -n "$commit" ] || commit='unknown'
        priority=$(audit_priority_score "$path")
        sqlite3 "$DB" "PRAGMA foreign_keys=ON; INSERT INTO files (path, line_count, last_commit, priority_score) VALUES ('$(sql_escape "$path")', $lines, '$commit', $priority) ON CONFLICT(path) DO UPDATE SET line_count=excluded.line_count, last_commit=excluded.last_commit, priority_score=excluded.priority_score, updated_at=strftime('%Y-%m-%dT%H:%M:%SZ','now');"
        keep_values="${keep_values}${keep_values:+,}'$(sql_escape "$path")'"
        synced=$((synced + 1))
    done

    # Expire stale coverage: any audit_coverage row whose commit_hash no
    # longer matches its file's current last_commit is stale. Before deleting,
    # classify each distinct (file, old-hash) pair: if the only changes between
    # the audited commit and the current one are comments/whitespace (per the
    # adapter's optional audit_trivial_comment_regex hook — see ADAPTERS.md),
    # the prior audit still stands, so the rows are re-stamped to the new hash
    # in place (audited_at untouched) instead of queueing a wasteful LLM
    # re-audit. Conservative on doubt: any git-diff failure (gc'd commit,
    # rename, 'unknown'/empty hash, -I unsupported) counts as NON-trivial.
    # An adapter without the hook gets today's delete-all behavior. Deleted
    # rows surface the file in v_next_audit_target for re-audit. All mutations
    # land in one sqlite3 spawn (one UPDATE per trivial tuple + a single bulk
    # DELETE) — per-row spawns are the thing scan.sh explicitly engineered
    # away. Counted by distinct file_id per bucket so the summary matches the
    # user's mental model ("N files expired").
    stale=$(sqlite3 -separator '|' "$DB" "SELECT ac.id, ac.file_id, f.path, ac.commit_hash, f.last_commit FROM audit_coverage ac JOIN files f ON f.id = ac.file_id WHERE ac.commit_hash != f.last_commit ORDER BY ac.file_id, ac.commit_hash;")
    expired=0
    restamped=0
    if [ -n "$stale" ]; then
        comment_re=""
        if command -v audit_trivial_comment_regex >/dev/null 2>&1; then
            comment_re="$(audit_trivial_comment_regex)"
        fi
        updates=""
        delete_ids=""
        del_fids=""
        upd_fids=""
        prev_key=""
        prev_trivial=0
        while IFS='|' read -r cid fid path old new; do
            [ -z "$cid" ] && continue
            # Rows arrive ordered by (file_id, old hash); classify once per
            # distinct tuple and reuse the verdict for its sibling categories.
            key="${fid}|${old}"
            if [ "$key" != "$prev_key" ]; then
                prev_key="$key"
                prev_trivial=0
                if [ -n "$comment_re" ] && [ -n "$old" ] && [ -n "$new" ] \
                   && git diff --quiet -w --ignore-blank-lines -I"$comment_re" "$old" "$new" -- "$path" 2>/dev/null; then
                    prev_trivial=1
                    updates="${updates}UPDATE audit_coverage SET commit_hash='$new' WHERE file_id=$fid AND commit_hash='$old';
"
                fi
            fi
            if [ "$prev_trivial" -eq 1 ]; then
                upd_fids="${upd_fids}${fid}
"
            else
                if [ -z "$delete_ids" ]; then delete_ids="$cid"; else delete_ids="${delete_ids},${cid}"; fi
                del_fids="${del_fids}${fid}
"
            fi
        done <<EOF2
$stale
EOF2
        batch="PRAGMA foreign_keys=ON;
BEGIN;
${updates}"
        [ -n "$delete_ids" ] && batch="${batch}DELETE FROM audit_coverage WHERE id IN (${delete_ids});
"
        sqlite3 "$DB" "${batch}COMMIT;"
        expired=$(printf '%s' "$del_fids" | sort -u | wc -l | tr -d ' ')
        restamped=$(printf '%s' "$upd_fids" | sort -u | wc -l | tr -d ' ')
    fi
    # Prune files deleted from disk: a path that vanished keeps its files
    # row forever otherwise, so its open findings and sweep-queue presence
    # never close out. One sqlite3 spawn: DELETE rows whose path was not
    # discovered this sync (value list sql_escape'd above); runs,
    # audit_coverage, and findings all cascade via ON DELETE CASCADE.
    # SELECT changes() reports only the direct files deletions.
    # The SQL is fed via stdin, not argv: the NOT IN value list grows with
    # the repo and Linux caps a single argv string at MAX_ARG_STRLEN (128 KiB).
    # Safety guard: if discovery returned zero paths (broken adapter),
    # skip the prune entirely — never wipe the table.
    pruned=0
    if [ "$synced" -gt 0 ]; then
        pruned=$(sqlite3 "$DB" <<EOF3
PRAGMA foreign_keys=ON;
DELETE FROM files WHERE path NOT IN (${keep_values});
SELECT changes();
EOF3
)
    fi
    if [ "$expired" -gt 0 ] || [ "$restamped" -gt 0 ] || [ "$pruned" -gt 0 ]; then
        echo "Synced $synced files ($expired coverage expired, $restamped re-stamped trivial, $pruned deleted pruned)."
    else
        echo "Synced $synced files (0 coverage expired)."
    fi
    meta_set last_sync_head "$(current_head)"
}

cmd_status() {
    check_db
    auto_sync
    $Q "$DB" "SELECT * FROM v_dashboard;"
    echo ""
    echo "Top open findings:"
    $Q "$DB" "SELECT finding_id, severity, file_path, title FROM v_open_findings LIMIT 5;"
    echo ""
    echo "Package progress:"
    $Q "$DB" "SELECT package, total_files, fully_covered, pct_covered, last_swept FROM v_packages WHERE total_lines > 200 LIMIT 10;"
    cycles="$(meta_get audit_cycle)"
    # if-block, not `[ -n ] && …`: as the function's last statement the
    # failed test would become the script's exit code on a fresh DB.
    if [ -n "$cycles" ]; then
        echo ""
        echo "Convergent cycles completed: $cycles"
    fi
}

cmd_findings() {
    check_db
    $Q "$DB" "SELECT finding_id, severity, category, file_path, line, title FROM v_open_findings;"
}

cmd_packages() {
    check_db
    auto_sync
    $Q "$DB" "SELECT * FROM v_packages;"
    echo ""
    ready=$(sqlite3 "$DB" "SELECT COUNT(*) FROM v_next_package_sweep;")
    echo "$ready packages ready for deep sweep."
}

cmd_next() {
    check_db
    auto_sync
    $Q "$DB" "SELECT path, line_count, priority_score, categories_covered, categories_remaining FROM v_next_audit_target LIMIT 10;"
}

cmd_history() {
    check_db
    echo "File sweeps:"
    $Q "$DB" "SELECT run_id, file_path, started_at, findings_count, autofixes_count FROM v_run_history LIMIT 10;"
    sweeps=$(sqlite3 "$DB" "SELECT COUNT(*) FROM sweeps;")
    if [ "$sweeps" -gt 0 ]; then
        echo ""
        echo "Package sweeps:"
        $Q "$DB" "SELECT * FROM v_sweep_history LIMIT 10;"
    fi
}

cmd_stale() {
    check_db
    result=$($Q "$DB" "SELECT * FROM v_stale_coverage;" 2>/dev/null)
    if [ -z "$result" ]; then
        echo "No stale coverage found. All audited files are up to date."
    else
        echo "$result"
    fi
}

cmd_finding() {
    check_db
    require_int "$1" "Usage: audit/audit.sh finding <id>"
    $Q "$DB" "
    SELECT f.id, fi.path AS file, f.line, f.category, f.severity, f.status,
           f.title, f.description, f.auto_fixable, f.found_at, f.resolved_at, f.resolution_note
    FROM findings f
    JOIN files fi ON fi.id = f.file_id
    WHERE f.id = $1;"
}

cmd_sweep() {
    check_db
    # batch_n, not "count": POSIX sh has no `local`, and the helpers called
    # between this fail-fast validation and the LIMIT below assign their own
    # counters (auto_scan's scan cadence, auto_sync→cmd_sync's synced-file
    # tally). A shared "count" global let those clobber the requested batch
    # size, so `sweep N` silently ignored N.
    batch_n="${1:-5}"
    require_int "$batch_n" "Usage: audit/audit.sh sweep [N]"
    auto_sync
    # Cycle loop-back: if the previous cycle closed with arch, it armed a
    # re-scan. Run it now (output to stderr so any later pasteable prompt stays
    # clean on stdout) and clear the flag BEFORE computing targets, so this tick
    # queues Phase 1 from fresh static findings + expired-coverage state.
    # cmd_scan resets sweeps_since_scan, so this also satisfies auto_scan's
    # cadence for this tick.
    if [ "$(meta_get force_scan_next)" = "1" ]; then
        meta_set force_scan_next 0
        echo "Loop-back: re-scanning after cycle close..." >&2
        cmd_scan >&2 || echo "sweep: cycle-close re-scan failed (continuing)" >&2
        echo "" >&2
    fi
    auto_scan
    # State is derived, not stored: a file counts as 're-audit' when it has
    # any completed run in history. v_next_audit_target already filters to
    # files with categories_remaining > 0, so we don't need to also check
    # that coverage is absent — partial coverage (e.g., 3/5 covered after
    # mixed staleness expiry) should still be labeled 're-audit'.
    targets=$(sqlite3 -separator '|' "$DB" "
        SELECT t.file_id, t.path, t.line_count, t.priority_score,
               COALESCE((SELECT MAX(r.finished_at) FROM runs r
                         WHERE r.file_id = t.file_id
                           AND r.finished_at IS NOT NULL), '') AS last_run,
               CASE
                   WHEN EXISTS (SELECT 1 FROM runs r
                                WHERE r.file_id = t.file_id
                                  AND r.finished_at IS NOT NULL)
                   THEN 're-audit'
                   ELSE 'first-pass'
               END AS state
        FROM v_next_audit_target t
        LIMIT $batch_n;" 2>/dev/null)
    if [ -z "$targets" ]; then
        # Phase 1 complete. Auto-progress to Phase 2 (deep sweep) if any
        # package is ready; otherwise to Phase 3 (arch sweep). Both messages
        # go to stderr so the next phase's pasteable prompt stays clean on
        # stdout.
        ready=$(sqlite3 "$DB" "SELECT COUNT(*) FROM v_next_package_sweep;")
        if [ "$ready" -gt 0 ]; then
            echo "Phase 1 complete — all files swept. Progressing to Phase 2 (deep sweep)." >&2
            echo "" >&2
            cmd_deep_sweep
        else
            advance_to_arch_or_idle
        fi
        return
    fi

    open=$(sqlite3 "$DB" "SELECT COUNT(*) FROM findings WHERE status = 'open';" 2>/dev/null)
    build_cmd="$(audit_verify_build_cmd)"
    test_cmd="$(audit_verify_test_cmd)"

    # Per-file enrichment: one block per queued file. The queue is small
    # (<=10 files), so per-file git/sqlite3 spawns are fine. All sub-queries
    # key on the integer file_id, so no path ever needs SQL-quoting here.
    queue=""
    while IFS='|' read -r fid path lines prio last_run state; do
        [ -z "$fid" ] && continue
        block="- ${path} (${lines} lines, priority ${prio}, state=${state})"
        if [ "$state" = "re-audit" ] && [ -n "$last_run" ]; then
            # Diffstat since the prior completed run: commit count plus
            # aggregate adds/dels. NF >= 3 keeps renamed-file numstat lines
            # ("adds dels old => new" splits to 4+ fields) in the tally; the
            # numeric guards drop binary files' "-" fields. Empty when no
            # commits landed since last_run.
            diffstat=$(git log --since="$last_run" --numstat --format='%h' -- "$path" 2>/dev/null | awk '
                NF == 1 { c++ }
                NF >= 3 && $1 ~ /^[0-9]+$/ && $2 ~ /^[0-9]+$/ { a += $1; d += $2 }
                END { if (c > 0) printf "%d commit(s), +%d/-%d", c, a, d }')
            block="${block}
  last_run: ${last_run}${diffstat:+ (since then: ${diffstat})}"
            # Prior-run refutation memo (runs.notes), only when non-empty.
            notes=$(sqlite3 "$DB" "SELECT notes FROM runs WHERE file_id = $fid AND finished_at IS NOT NULL ORDER BY finished_at DESC LIMIT 1;")
            [ -n "$notes" ] && block="${block}
  prior run notes: ${notes}"
        fi
        file_findings=$(sqlite3 "$DB" "
            SELECT '  - #' || f.id || ' [' || f.severity || '/' || f.category || '] line ' || COALESCE(f.line, '?') || ': ' || f.title
            FROM findings f
            WHERE f.file_id = $fid AND f.status = 'open'
            ORDER BY CASE f.severity WHEN 'critical' THEN 0 WHEN 'high' THEN 1
            WHEN 'medium' THEN 2 ELSE 3 END, f.id;")
        [ -n "$file_findings" ] && block="${block}
  open findings:
${file_findings}"
        queue="${queue}${block}
"
    done <<EOF2
$targets
EOF2

    cat <<EOF
--- Paste this into Claude Code ---

Sweep the codebase: audit and fix the next batch of files. Follow the sweep skill at \`.claude/skills/sweep/SKILL.md\` (Phase 1) — everything below is data for this batch.

Build command: ${build_cmd}
Test command: ${test_cmd}

There are ${open} open findings from prior sweeps — check if any are resolved by your fixes.

Next files in queue:
${queue}
EOF
}

cmd_deep_sweep() {
    check_db
    auto_sync
    auto_scan
    pkg="$1"
    if [ -z "$pkg" ]; then
        # Pick the largest package ready for deep sweep
        pkg=$(sqlite3 "$DB" "SELECT package FROM v_next_package_sweep LIMIT 1;" 2>/dev/null)
        if [ -z "$pkg" ]; then
            # No package ready. Either Phase 1 isn't done yet (no fully-
            # covered package exists), or every eligible package has been
            # deep-swept. Distinguish by checking for remaining file targets.
            files_left=$(sqlite3 "$DB" "SELECT COUNT(*) FROM v_next_audit_target;" 2>/dev/null)
            if [ "${files_left:-0}" -gt 0 ]; then
                echo "No package is ready for deep sweep yet (Phase 1 still has files to cover)." >&2
                echo "Falling back to Phase 1 (file sweep)." >&2
                echo "" >&2
                cmd_sweep
            else
                echo "Phase 2 complete — all eligible packages deep-swept." >&2
                advance_to_arch_or_idle
            fi
            return
        fi
    fi
    [ -z "$pkg" ] && echo "No packages to sweep." && exit 0

    pkg_sql="$(sql_escape "$pkg")"
    pkg_info=$($Q "$DB" "SELECT * FROM v_packages WHERE package = '$pkg_sql';")
    file_list=$(sqlite3 "$DB" "SELECT path FROM files WHERE path LIKE '$pkg_sql/%' ORDER BY line_count DESC;")
    open_list=$(sqlite3 "$DB" "
        SELECT '- #' || f.id || ' [' || f.severity || '/' || f.category || '] ' || fi.path || COALESCE(':' || f.line, '') || ': ' || f.title
        FROM findings f JOIN files fi ON fi.id = f.file_id
        WHERE fi.path LIKE '$pkg_sql/%' AND f.status = 'open'
        ORDER BY CASE f.severity WHEN 'critical' THEN 0 WHEN 'high' THEN 1
        WHEN 'medium' THEN 2 ELSE 3 END, f.id;")
    [ -z "$open_list" ] && open_list="(none)"

    build_cmd="$(audit_verify_build_cmd)"
    test_cmd="$(audit_verify_test_cmd)"

    cat <<EOF
--- Paste this into Claude Code ---

Deep sweep of the \`${pkg}\` package: a cross-cutting pass over all its files together. Follow the sweep skill at \`.claude/skills/sweep/SKILL.md\` (Phase 2) — everything below is data for this package.

Build command: ${build_cmd}
Test command: ${test_cmd}

Open findings in this package from prior sweeps:
${open_list}

Package info:
${pkg_info}

Files:
${file_list}
EOF
}

cmd_fix() {
    check_db
    require_int "$1" "Usage: audit/audit.sh fix <id>"
    row=$(sqlite3 -separator '|' "$DB" "
    SELECT fi.path, f.line, f.category, f.severity, f.title, f.description, f.id
    FROM findings f JOIN files fi ON fi.id = f.file_id
    WHERE f.id = $1 AND f.status = 'open';")
    [ -z "$row" ] && echo "Finding #$1 not found or already resolved." && exit 1

    file=$(echo "$row" | cut -d'|' -f1)
    line=$(echo "$row" | cut -d'|' -f2)
    category=$(echo "$row" | cut -d'|' -f3)
    severity=$(echo "$row" | cut -d'|' -f4)
    title=$(echo "$row" | cut -d'|' -f5)
    desc=$(echo "$row" | cut -d'|' -f6)
    fid=$(echo "$row" | cut -d'|' -f7)

    location="$file"
    [ -n "$line" ] && location="${file}:${line}"

    build_cmd="$(audit_verify_build_cmd)"
    test_cmd="$(audit_verify_test_cmd)"

    cat <<EOF
--- Paste this into Claude Code ---

Fix audit finding #${fid} [${severity}/${category}] in ${location}:

**${title}**

${desc}

After fixing:
1. Run \`${build_cmd}\` to verify it compiles
2. Run \`${test_cmd}\` to verify tests pass
3. Mark the finding as resolved: \`audit/audit.sh resolve ${fid}\`
4. Commit the fix
EOF
}

cmd_fix_all() {
    check_db
    file="$1"
    if [ -z "$file" ]; then
        file=$(sqlite3 "$DB" "
        SELECT fi.path FROM findings f
        JOIN files fi ON fi.id = f.file_id
        WHERE f.status = 'open'
        GROUP BY fi.path ORDER BY COUNT(*) DESC LIMIT 1;")
        [ -z "$file" ] && echo "No open findings." && exit 0
    fi

    findings=$(sqlite3 -separator '|' "$DB" "
    SELECT f.id, f.line, f.category, f.severity, f.title, f.description
    FROM findings f JOIN files fi ON fi.id = f.file_id
    WHERE fi.path = '$(sql_escape "$file")' AND f.status = 'open'
    ORDER BY CASE f.severity WHEN 'critical' THEN 0 WHEN 'high' THEN 1
    WHEN 'medium' THEN 2 WHEN 'low' THEN 3 END;")
    [ -z "$findings" ] && echo "No open findings for $file" && exit 0

    ids=""
    listing=""
    while IFS='|' read -r fid line cat sev title desc; do
        loc="$file"
        [ -n "$line" ] && loc="${file}:${line}"
        listing="${listing}
- **#${fid}** [${sev}/${cat}] ${loc}: ${title}
  ${desc}"
        if [ -z "$ids" ]; then ids="$fid"; else ids="${ids} ${fid}"; fi
    done <<EOF2
$findings
EOF2

    resolve_cmds=""
    for id in $ids; do
        resolve_cmds="${resolve_cmds}audit/audit.sh resolve ${id}
"
    done

    build_cmd="$(audit_verify_build_cmd)"
    test_cmd="$(audit_verify_test_cmd)"

    cat <<EOF
--- Paste this into Claude Code ---

Fix all open audit findings in ${file}:
${listing}

After fixing:
1. Run \`${build_cmd}\` to verify it compiles
2. Run \`${test_cmd}\` to verify tests pass
3. Mark findings as resolved:
\`\`\`
${resolve_cmds}\`\`\`
4. Commit the fixes
EOF
}

cmd_triage() {
    check_db
    open=$(sqlite3 "$DB" "SELECT COUNT(*) FROM findings WHERE status = 'open';")
    if [ "${open:-0}" -eq 0 ]; then
        echo "Nothing to triage — no open findings."
        return
    fi

    # Findings ordered by severity then age (oldest first). Title and
    # description are free text (quotes, backticks, pipes) and are display
    # data only — description reads LAST so `read` merges any embedded
    # separators into it, and the recording commands in the prompt key on
    # the integer id alone, so no finding text ever flows into SQL.
    findings=$(sqlite3 -separator '|' "$DB" "
    SELECT f.id, fi.path, f.line, f.severity, f.category, f.found_at,
           f.title, f.description
    FROM findings f JOIN files fi ON fi.id = f.file_id
    WHERE f.status = 'open'
    ORDER BY CASE f.severity WHEN 'critical' THEN 0 WHEN 'high' THEN 1
    WHEN 'medium' THEN 2 ELSE 3 END, f.found_at ASC, f.id ASC;")

    build_cmd="$(audit_verify_build_cmd)"
    test_cmd="$(audit_verify_test_cmd)"

    listing=""
    while IFS='|' read -r fid path line sev cat found title desc; do
        [ -z "$fid" ] && continue
        loc="$path"
        [ -n "$line" ] && loc="${path}:${line}"
        listing="${listing}
- **#${fid}** [${sev}/${cat}] ${loc} (found ${found})
  ${title}
  ${desc}"
    done <<EOF2
$findings
EOF2

    cat <<EOF
--- Paste this into Claude Code ---

Interactive triage of all ${open} open audit findings. Walk me through them ONE AT A TIME, in the order listed below (severity, then oldest first).

For each finding:
1. Re-read the cited code first — the finding may already be fixed or obsolete.
2. Give a one-line recommendation: \`fix now\`, \`wontfix\`, or \`keep open\` (with a short reason).
3. Wait for my decision before doing anything.
4. Record the decision (commands below), then move to the next finding.

Open findings:
${listing}

Recording decisions (per finding):
- Fix now — implement the fix, run \`${build_cmd}\` and \`${test_cmd}\` to verify it, commit the fix, then: \`audit/audit.sh resolve <id>\`
- Wontfix — record the rationale (replace <id> and the note; escape any single quotes in the note by doubling them):
\`\`\`
sqlite3 audit/audit.db "PRAGMA foreign_keys=ON; UPDATE findings SET status='wontfix', resolved_at=strftime('%Y-%m-%dT%H:%M:%SZ','now'), resolution_note='<note>' WHERE id=<id> AND status='open';"
\`\`\`
- Keep open — no command; move on.
EOF
}

cmd_resolve() {
    check_db
    require_int "$1" "Usage: audit/audit.sh resolve <id>"
    sqlite3 "$DB" "PRAGMA foreign_keys=ON; UPDATE findings SET status='fixed', resolved_at=strftime('%Y-%m-%dT%H:%M:%SZ','now') WHERE id=$1 AND status='open';"
    echo "Finding #$1 marked as fixed."
}

cmd_wontfix() {
    check_db
    require_int "$1" "Usage: audit/audit.sh wontfix <id>"
    sqlite3 "$DB" "PRAGMA foreign_keys=ON; UPDATE findings SET status='wontfix', resolved_at=strftime('%Y-%m-%dT%H:%M:%SZ','now') WHERE id=$1 AND status='open';"
    echo "Finding #$1 marked as wontfix."
}

cmd_scan() {
    check_db
    ensure_meta
    # Delegates to audit/scan.sh; kept separate to keep this file uncluttered.
    sh "$(dirname "$0")/scan.sh"
    meta_set last_scan_head "$(current_head)"
    meta_set sweeps_since_scan 0
}

cmd_arch_sweep() {
    check_db
    auto_sync
    auto_scan
    # Delegates to audit/arch.sh; same separation rationale as cmd_scan.
    # Pass the optional package arg through verbatim — arch.sh validates it.
    # Kept PURE: just the report + prompt. The convergent cycle behavior
    # (cycle-close re-scan / idle) lives in advance_to_arch_or_idle on the
    # sweep dispatch path, so a direct `arch-sweep` never triggers a re-scan.
    sh "$(dirname "$0")/arch.sh" "$1"
}

# advance_to_arch_or_idle: the convergent cycle controller. Called from the
# sweep dispatch path once Phase 1 and Phase 2 are both complete (no file
# targets, no package ready for deep sweep). Closes the file→deep→arch→rescan
# loop, idling cleanly when the repo hasn't changed.
#
#   * Fresh cycle end (last_arch_head != current HEAD, or unset): run the arch
#     sweep (report + pasteable prompt on stdout, as today), then close the
#     cycle — record current HEAD as last_arch_head and arm force_scan_next so
#     the NEXT sweep tick re-scans (seeding fresh findings + expiring coverage
#     for any intervening commits) before computing targets. That re-scan is
#     what loops the cycle back to Phase 1.
#   * Converged (last_arch_head == current HEAD, nothing changed): do NOT
#     re-run arch. If open findings remain, surface them via the existing
#     fix-all path; otherwise emit an idle message to stderr and exit 0 with no
#     pasteable prompt.
advance_to_arch_or_idle() {
    last_arch="$(meta_get last_arch_head)"
    cur="$(current_head)"
    # Normalize an empty HEAD (no git / detached / some CI) to a stable
    # sentinel so the converged comparison below can match it. Without this,
    # an empty cur could never equal last_arch and every tick would re-run
    # arch — the busy-loop this controller exists to prevent.
    [ -z "$cur" ] && cur="no-git"

    # Converged: arch already ran at this exact repo state and nothing has
    # moved since. Don't burn tokens re-reading unchanged code.
    if [ "$cur" = "$last_arch" ]; then
        open=$(sqlite3 "$DB" "SELECT COUNT(*) FROM findings WHERE status = 'open';" 2>/dev/null)
        if [ "${open:-0}" -gt 0 ]; then
            echo "Audit converged — ${open} open finding(s) remain. Surfacing fixes." >&2
            echo "" >&2
            cmd_fix_all
        else
            echo "Audit converged — nothing to do until the code changes." >&2
        fi
        return
    fi

    # Fresh cycle end: run arch, then close the cycle.
    echo "Phases 1 & 2 complete. Progressing to Phase 3 (architecture sweep)." >&2
    echo "" >&2
    cmd_arch_sweep
    meta_set last_arch_head "$cur"
    # Only arm the HEAD-tracked re-scan loop-back when there's a real HEAD;
    # a re-scan that loops back on commit movement is meaningless without git.
    # The sentinel was already stored as last_arch_head above, so the next
    # tick hits the converged branch and idles — no busy-loop either way.
    [ "$cur" != "no-git" ] && meta_set force_scan_next 1
    # Optional cycle counter, surfaced in `status`.
    cycles="$(meta_get audit_cycle)"
    [ -z "$cycles" ] && cycles=0
    meta_set audit_cycle "$((cycles + 1))"
    echo "" >&2
    echo "Cycle closed — re-scan armed; next sweep re-enters Phase 1 from fresh state." >&2
}

case "${1:-}" in
    status)     cmd_status ;;
    findings)   cmd_findings ;;
    packages)   cmd_packages ;;
    next)       cmd_next ;;
    history)    cmd_history ;;
    stale)      cmd_stale ;;
    sync)       cmd_sync ;;
    scan)       cmd_scan ;;
    init)       cmd_init "$2" ;;
    sweep)      cmd_sweep "$2" ;;
    deep-sweep) cmd_deep_sweep "$2" ;;
    arch-sweep) cmd_arch_sweep "$2" ;;
    finding)    cmd_finding "$2" ;;
    triage)     cmd_triage ;;
    fix)        cmd_fix "$2" ;;
    fix-all)    cmd_fix_all "$2" ;;
    resolve)    cmd_resolve "$2" ;;
    wontfix)    cmd_wontfix "$2" ;;
    help|-h|--help) usage ;;
    *)          usage ;;
esac
