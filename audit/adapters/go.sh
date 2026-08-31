#!/bin/sh
# Go language adapter for the audit system.
#
# The audit core (audit/audit.sh, audit/scan.sh) is language-agnostic. Every
# Go-specific decision — which files to track, how to prioritise them, the
# build/test commands embedded in generated prompts, and the catalog of static
# analyzers — lives here. The core sources exactly one adapter, selected by
# AUDIT_ADAPTER (default "go"), via `. audit/adapters/${AUDIT_ADAPTER}.sh`.
#
# This is the reference adapter; it began as a byte-for-byte extraction of the
# previously hardcoded Go logic (source discovery has since moved from `find`
# to git's index — see audit_discover_sources). To support another language,
# copy this file to audit/adapters/<lang>.sh and reimplement the contract below.
#
# ---------------------------------------------------------------------------
# Adapter contract (functions/variables the core calls)
# ---------------------------------------------------------------------------
#
# Discovery & priority (used by audit/audit.sh cmd_sync):
#   audit_discover_sources
#       Emit the repo-relative paths of source files to track, one per line.
#       The core sorts and iterates this list. Paths must NOT carry a leading
#       "./" (the core strips it defensively but the contract is repo-relative).
#   audit_priority_score <path>
#       Print the integer priority score (0-100) for one repo-relative path.
#   audit_trivial_comment_regex
#       Optional. Print an ERE matching comment-only lines. cmd_sync feeds it
#       to `git diff -I` (with -w --ignore-blank-lines) to classify a stale
#       coverage row's old→new change as trivial (comment/whitespace only):
#       trivial rows are re-stamped to the new commit instead of expired. If
#       the adapter omits this hook, the core skips classification entirely
#       and every stale row is expired (the historical behavior).
#
# Verify commands (used by audit/audit.sh prompt templates):
#   audit_verify_build_cmd   Print the build command string (no trailing NL).
#   audit_verify_test_cmd    Print the test command string (no trailing NL).
#
# Scan tool catalog (used by audit/scan.sh). The core owns the generic harness
# (file-id cache, sql_escape/rel_path/truncate_str/count_lines, apply_batch,
# batch header/footer, file sync, "all analyzers missing" check, summary
# rendering). The adapter owns the tools:
#   AUDIT_SCAN_TOOLS
#       Space-separated, ordered list of tool ids. The core iterates this for
#       running drivers, the all-missing check, the total-new tally, and the
#       per-tool summary lines (preserving the original [n/5] order).
#   scan_<id>            One driver function per id. Same logic as before; sets
#                        the per-tool counter/state variables below. The core's
#                        harness helpers (write_batch_header, apply_batch, …)
#                        and TMP_DIR/FILES_CACHE are in scope when these run.
#   audit_scan_label <id>   Print the display name used in the summary table.
#   audit_scan_state <id>   Print "ran" or "missing" for one id.
#   audit_scan_new   <id>   Print the new-findings count for one id.
#   audit_scan_dup   <id>   Print the duplicate count for one id.
#   audit_scan_skip  <id>   Print the skipped (untracked-path) count for one id.
#
# The scan drivers and harness helpers rely on these variables being defined by
# the core before the drivers run: DB, REPO_ROOT, TMP_DIR, FILES_CACHE, and the
# helper functions lookup_fid, sql_escape, rel_path, truncate_str, count_lines,
# apply_batch, write_batch_header, write_batch_footer.
#
# Architecture report (used by audit/arch.sh arch-sweep). The core owns generic
# directory target resolution (whole-repo vs subtree, TARGET_SCOPE/TARGET_DIR/
# TARGET_FILE_LIKE), Section C (largest files, a pure files-table query), report
# assembly, and the Claude-prompt skeleton. The adapter owns the language/project
# specific analysis:
#   audit_arch_preflight
#       Verify the language toolchain/preconditions and make module/graph state
#       available to the section functions. Go: require go.mod + a working
#       `go list -m`; on success set AUDIT_ARCH_MODULE (a plain assignment — the
#       core sources the adapter, so the value is in scope for the report header
#       and the section funcs without exporting it) and stash any other state in
#       TMP_DIR. On
#       failure, print a clear error to stderr and return non-zero (core exits 1).
#   audit_arch_resolve_nondir <arg>
#       Optional. Given a target arg that is NOT an existing directory, print a
#       repo-relative directory for it, or nothing if it can't be resolved. Go
#       maps a fully-qualified import path back to its subtree by stripping the
#       module prefix. Called by core only after its own `[ -d ]` check fails.
#   audit_arch_has_sources <dir>
#       Optional. Return zero if <dir> (an existing directory the core has
#       already validated) holds language source files, non-zero if it is
#       devoid of them — the core rejects an empty/non-source subtree with a
#       clear error before handing it to the analyzers (which would otherwise
#       emit stderr spam). Go checks for *.go files (top-level glob, then a
#       bounded recursive find). If an adapter omits this function the core
#       SKIPS the check entirely, letting the graph build surface a clear
#       empty-scope error rather than falsely rejecting a valid source tree.
#   audit_arch_build_graph
#       Build the package + internal-import graph scoped to TARGET_SCOPE/
#       TARGET_DIR. Go runs `go list` and writes $TMP_DIR/rel.imports (one
#       "<pkg-rel>|<comma-list of internal deps>" line per repo package) and
#       $TMP_DIR/target.rel (one in-scope repo-relative package per line),
#       used by the section functions. May return non-zero on a fatal toolchain
#       error (core exits 1).
#   audit_arch_section_a / _b / _d / _e
#       Emit the body of Sections A (import graph fan-in/out), B (complexity
#       hotspots), D (public API surface), and E (layer violations) exactly as
#       the report shows them, including their "=== Section X: ... ===" headers
#       and surrounding blank lines. Section C (largest files) is core's. An
#       adapter that omits any of these gets a core placeholder line instead.
#
# Arch state variables the core defines before calling the arch functions:
# DB, REPO_ROOT, TMP_DIR, TARGET_SCOPE, TARGET_DIR, TARGET_FILE_LIKE.

# ---------------------------------------------------------------------------
# Discovery & priority
# ---------------------------------------------------------------------------

# audit_discover_sources: repo-relative list of Go source files to track.
# Sources the list from git's index (`git ls-files`) rather than a bare
# `find`: find also swept up untracked, gitignored scratch (tmp/ build
# artefacts and session scratchpads), which consumed sweep-queue slots and
# seeded findings against files nobody owns. The node_modules/vendor/gen
# exclusions stay path-based despite the switch because generated code is
# COMMITTED in this repo (e.g. the sqlc output under internal/db/gen/), so
# git status cannot distinguish it from handwritten source. The `(^|/)`
# anchor keeps the old `-not -path '*/gen/*'` semantics for a top-level
# gen/ vendor/ node_modules/ — find matched against "./"-prefixed paths,
# while git ls-files emits bare relative ones. The `[ -f ]` filter drops
# index entries whose working-tree copy is absent (rm without git rm),
# which would otherwise abort cmd_sync's `wc -l` under set -e.
audit_discover_sources() {
    git ls-files -- '*.go' \
    | grep -Ev '(^|/)(node_modules|vendor|gen)/' \
    | while IFS= read -r p; do [ -f "$p" ] && printf '%s\n' "$p"; done
}

# audit_priority_score <path>: priority score for a repo-relative Go path.
# Reproduces the original case block exactly.
audit_priority_score() {
    priority=50
    case "$1" in
        *_test.go) priority=30 ;;
        *handler_auth*|*middleware*|*csrf*|*auth/*) priority=90 ;;
        *handler_webauthn*|*crypto/*|*sshutil/*) priority=85 ;;
        *handler_aiproxy*|*aiproxy/*) priority=80 ;;
        *handler_terminal*|*updater/*) priority=75 ;;
        *handler_*) priority=70 ;;
        *scheduler*) priority=65 ;;
        *db/*) priority=60 ;;
        *models/*) priority=40 ;;
    esac
    printf '%s' "$priority"
}

# audit_trivial_comment_regex: ERE for comment-only lines in Go sources.
# Matches line comments (//) at any indentation, but only when '//' is
# followed by whitespace or end-of-line. Go toolchain/linter directives
# (//go:build, //go:generate, //go:embed, //nolint, //export, ...) are all
# '//word' with no space after the slashes, while gofmt'd prose comments are
# '// text' — so requiring a space/EOL excludes the whole directive class.
# Non-gofmt'd '//comment' lines and block comments fall to the conservative
# default (non-trivial), which is the safe direction.
audit_trivial_comment_regex() {
    printf '%s' '^[[:space:]]*//([[:space:]]|$)'
}

# ---------------------------------------------------------------------------
# Verify commands embedded in generated prompts
# ---------------------------------------------------------------------------

audit_verify_build_cmd() { printf '%s' 'go build ./...'; }
audit_verify_test_cmd()  { printf '%s' 'go test ./...'; }

# ---------------------------------------------------------------------------
# Scan tool catalog
# ---------------------------------------------------------------------------

# Ordered tool ids. The core iterates this for running, the all-missing check,
# the new-findings tally, and the summary (preserving [1/5]..[5/5] order).
AUDIT_SCAN_TOOLS="gosec staticcheck govulncheck errcheck ineffassign"

# Per-tool counters/state. Held as shell variables; surfaced via the
# audit_scan_* accessors below and printed by the core's summary.
GOSEC_NEW=0;        GOSEC_DUP=0;        GOSEC_SKIP=0;        GOSEC_STATE=missing
STATICCHECK_NEW=0;  STATICCHECK_DUP=0;  STATICCHECK_SKIP=0;  STATICCHECK_STATE=missing
GOVULNCHECK_NEW=0;  GOVULNCHECK_DUP=0;  GOVULNCHECK_SKIP=0;  GOVULNCHECK_STATE=missing
ERRCHECK_NEW=0;     ERRCHECK_DUP=0;     ERRCHECK_SKIP=0;     ERRCHECK_STATE=missing
INEFFASSIGN_NEW=0;  INEFFASSIGN_DUP=0;  INEFFASSIGN_SKIP=0;  INEFFASSIGN_STATE=missing

# Accessors mapping a tool id to its display name and counter/state vars. These
# keep the core's harness free of any tool-specific variable names.
audit_scan_label() {
    case "$1" in
        gosec)       printf '%s' 'gosec' ;;
        staticcheck) printf '%s' 'staticcheck' ;;
        govulncheck) printf '%s' 'govulncheck' ;;
        errcheck)    printf '%s' 'errcheck' ;;
        ineffassign) printf '%s' 'ineffassign' ;;
    esac
}

audit_scan_state() {
    case "$1" in
        gosec)       printf '%s' "$GOSEC_STATE" ;;
        staticcheck) printf '%s' "$STATICCHECK_STATE" ;;
        govulncheck) printf '%s' "$GOVULNCHECK_STATE" ;;
        errcheck)    printf '%s' "$ERRCHECK_STATE" ;;
        ineffassign) printf '%s' "$INEFFASSIGN_STATE" ;;
    esac
}

audit_scan_new() {
    case "$1" in
        gosec)       printf '%s' "$GOSEC_NEW" ;;
        staticcheck) printf '%s' "$STATICCHECK_NEW" ;;
        govulncheck) printf '%s' "$GOVULNCHECK_NEW" ;;
        errcheck)    printf '%s' "$ERRCHECK_NEW" ;;
        ineffassign) printf '%s' "$INEFFASSIGN_NEW" ;;
    esac
}

audit_scan_dup() {
    case "$1" in
        gosec)       printf '%s' "$GOSEC_DUP" ;;
        staticcheck) printf '%s' "$STATICCHECK_DUP" ;;
        govulncheck) printf '%s' "$GOVULNCHECK_DUP" ;;
        errcheck)    printf '%s' "$ERRCHECK_DUP" ;;
        ineffassign) printf '%s' "$INEFFASSIGN_DUP" ;;
    esac
}

audit_scan_skip() {
    case "$1" in
        gosec)       printf '%s' "$GOSEC_SKIP" ;;
        staticcheck) printf '%s' "$STATICCHECK_SKIP" ;;
        govulncheck) printf '%s' "$GOVULNCHECK_SKIP" ;;
        errcheck)    printf '%s' "$ERRCHECK_SKIP" ;;
        ineffassign) printf '%s' "$INEFFASSIGN_SKIP" ;;
    esac
}

# ---- gosec -----------------------------------------------------------
scan_gosec() {
    echo "[1/5] gosec..."
    if ! command -v gosec >/dev/null 2>&1; then
        echo "  [!] gosec not installed — run 'make audit-tools'"
        return
    fi
    GOSEC_STATE=ran
    out="$TMP_DIR/gosec.json"
    # gosec -no-fail -> exit 0 even with findings; stderr suppressed with -quiet.
    # G706 (log-injection via taint analysis) is excluded project-wide:
    # log.Printf is operator-only stderr output (no log-based access control,
    # no external sink), and the universal mitigation would mean wrapping
    # every error/string passed to a log call across the repo for negligible
    # gain. If we ever ship logs to a remote collector, revisit.
    gosec -quiet -fmt json -no-fail \
          -exclude=G706 \
          -exclude-dir=vendor -exclude-dir=node_modules \
          ./... > "$out" 2>"$TMP_DIR/gosec.err" || true

    count="$(jq '.Issues | length' "$out" 2>/dev/null || echo 0)"
    if [ "$count" -eq 0 ]; then
        return
    fi

    batch="$TMP_DIR/gosec.sql"
    write_batch_header "$batch"
    attempted=0
    # One record per issue: file|line|severity|rule_id|details.
    # Using | as the separator (not @tsv) keeps empty fields addressable
    # and avoids whitespace-collapse in `read`; we also strip newlines
    # from `details` to keep each record on a single line.
    jq -r '.Issues[] | .details as $d | "\(.file)|\(.line)|\(.severity)|\(.rule_id)|\($d | gsub("\n"; "  "))"' "$out" \
    | while IFS='|' read -r file line sev rule_id details; do
        [ -z "$file" ] && continue
        # gosec emits `line` as either "42" or "42-47" for multi-line
        # findings; keep only the first number to stay compatible with
        # findings.line (INTEGER) and the dedup index.
        line="${line%%-*}"
        rel="$(rel_path "$file")"
        fid="$(lookup_fid "$rel")"
        if [ -z "$fid" ]; then
            echo "  [skip] untracked path: $rel" >&2
            echo "SKIP" >> "$TMP_DIR/gosec.skips"
            continue
        fi
        case "$sev" in
            HIGH)   severity=high   ;;
            MEDIUM) severity=medium ;;
            LOW)    severity=low    ;;
            *)      severity=medium ;;
        esac
        title_raw="[$rule_id] $details"
        title="$(truncate_str 200 "$title_raw")"
        desc="$(truncate_str 2000 "$details")"
        ft="$(sql_escape "$title")"
        fd="$(sql_escape "$desc")"
        printf "INSERT OR IGNORE INTO findings (file_id, line, tool, source, severity, category, title, description, status) VALUES (%s, %s, 'gosec', 'static-tool', '%s', 'security', '%s', '%s', 'open');\n" \
            "$fid" "$line" "$severity" "$ft" "$fd" >> "$batch"
        echo "INS" >> "$TMP_DIR/gosec.ins"
    done
    attempted="$(count_lines "$TMP_DIR/gosec.ins")"
    GOSEC_SKIP="$(count_lines "$TMP_DIR/gosec.skips")"
    write_batch_footer "$batch"

    if [ "$attempted" -gt 0 ]; then
        result="$(apply_batch gosec "$batch" "$attempted")"
        GOSEC_NEW="${result% *}"
        GOSEC_DUP="${result#* }"
    fi
}

# ---- staticcheck -----------------------------------------------------
scan_staticcheck() {
    echo "[2/5] staticcheck..."
    if ! command -v staticcheck >/dev/null 2>&1; then
        echo "  [!] staticcheck not installed — run 'make audit-tools'"
        return
    fi
    STATICCHECK_STATE=ran
    out="$TMP_DIR/staticcheck.json"
    # staticcheck exits 1 on findings — tolerate it; exits >1 on a real error.
    staticcheck -f json ./... > "$out" 2>"$TMP_DIR/staticcheck.err" || rc=$?
    if [ "${rc:-0}" -gt 1 ]; then
        echo "  [!] staticcheck failed (exit ${rc}); see $TMP_DIR/staticcheck.err" >&2
        return
    fi
    rc=0
    if [ ! -s "$out" ]; then
        return
    fi

    batch="$TMP_DIR/staticcheck.sql"
    write_batch_header "$batch"
    # Strip newlines in message (some cgo errors embed them). Use | separator
    # to avoid whitespace-collapse on empty fields.
    jq -r '. | .message as $m | "\(.code)|\(.location.file)|\(.location.line)|\($m | gsub("\n"; "  "))"' "$out" \
    | while IFS='|' read -r code file line msg; do
        [ -z "$file" ] && continue
        rel="$(rel_path "$file")"
        fid="$(lookup_fid "$rel")"
        if [ -z "$fid" ]; then
            echo "  [skip] untracked path: $rel" >&2
            echo "SKIP" >> "$TMP_DIR/staticcheck.skips"
            continue
        fi
        # Category from code prefix.
        case "$code" in
            SA*) category=bugs ;;
            S*)  category=refactoring ;;  # S, ST, SA prefix — SA handled above
            U*)  category=refactoring ;;
            QF*) category=refactoring ;;  # QuickFix diagnostics
            *)   category=bugs ;;
        esac
        title_raw="[$code] $msg"
        title="$(truncate_str 200 "$title_raw")"
        desc="$(truncate_str 2000 "$msg")"
        ft="$(sql_escape "$title")"
        fd="$(sql_escape "$desc")"
        printf "INSERT OR IGNORE INTO findings (file_id, line, tool, source, severity, category, title, description, status) VALUES (%s, %s, 'staticcheck', 'static-tool', 'medium', '%s', '%s', '%s', 'open');\n" \
            "$fid" "$line" "$category" "$ft" "$fd" >> "$batch"
        echo "INS" >> "$TMP_DIR/staticcheck.ins"
    done
    attempted="$(count_lines "$TMP_DIR/staticcheck.ins")"
    STATICCHECK_SKIP="$(count_lines "$TMP_DIR/staticcheck.skips")"
    write_batch_footer "$batch"

    if [ "$attempted" -gt 0 ]; then
        result="$(apply_batch staticcheck "$batch" "$attempted")"
        STATICCHECK_NEW="${result% *}"
        STATICCHECK_DUP="${result#* }"
    fi
}

# ---- govulncheck -----------------------------------------------------
# govulncheck emits a stream of pretty-printed JSON objects (not NDJSON).
# We parse with `jq -s` (slurp). Findings are module-level: no source line.
# Each finding is attached (line=NULL) to the first tracked .go file that
# imports the vulnerable module. If nothing imports it (dep of a dep),
# we skip with a notice — there's no useful file to pin it to.
scan_govulncheck() {
    echo "[3/5] govulncheck..."
    if ! command -v govulncheck >/dev/null 2>&1; then
        echo "  [!] govulncheck not installed — run 'make audit-tools'"
        return
    fi
    GOVULNCHECK_STATE=ran
    out="$TMP_DIR/govulncheck.json"
    govulncheck -json ./... > "$out" 2>"$TMP_DIR/govulncheck.err" || true

    # Build an OSV metadata map: id -> summary. Fall back to empty array on
    # jq failure so the rest of the parser keeps working.
    jq -s '[.[] | select(.osv) | {id: .osv.id, summary: .osv.summary}] | unique_by(.id)' "$out" \
        > "$TMP_DIR/govulncheck.osvs.json" 2>/dev/null \
        || echo "[]" > "$TMP_DIR/govulncheck.osvs.json"

    # Every finding with a module path gives us (osv_id, module).
    jq -s -c '[.[] | select(.finding) | .finding | {osv: .osv, fixed: (.fixed_version // ""), module: (.trace[0].module // ""), pkg: (.trace[0].package // "")}] | unique_by("\(.osv)\(.pkg)")' "$out" \
        > "$TMP_DIR/govulncheck.findings.json" 2>/dev/null \
        || echo "[]" > "$TMP_DIR/govulncheck.findings.json"

    count="$(jq 'length' "$TMP_DIR/govulncheck.findings.json")"
    if [ "$count" -eq 0 ]; then
        return
    fi

    batch="$TMP_DIR/govulncheck.sql"
    write_batch_header "$batch"
    # Build a list of tracked files once; grep searches this list per finding.
    # Reuse FILES_CACHE (path<TAB>id) by stripping to just the path column.
    awk -F'\t' '{ print $1 }' "$FILES_CACHE" > "$TMP_DIR/govulncheck.tracked.txt"

    # For each unique finding, pick the first tracked .go file that imports
    # the vulnerable module; record one finding against it.
    # NB: @tsv collapses consecutive empty fields because `read` treats tab
    # as whitespace. We use `|` as a field separator instead so that empty
    # fixed_version / pkg values don't shift other columns.
    jq -r '.[] | "\(.osv)|\(.fixed)|\(.module)|\(.pkg)"' "$TMP_DIR/govulncheck.findings.json" \
    | while IFS='|' read -r osv_id fixed module pkg; do
        [ -z "$module" ] && continue
        # Find a tracked file that imports this module. Search imports for
        # the most-specific path first (package, then module).
        # Fixed-string match (-F) prevents `.` from acting as regex. We look
        # for either the exact import (`"path"`) or a sub-package import
        # (`"path/...`), so attribution works for module-level entries while
        # still refusing to match `"path_something"` or `"path-other"`.
        candidate=""
        for search in "$pkg" "$module"; do
            [ -z "$search" ] && continue
            while IFS= read -r f; do
                if [ -f "$f" ] && grep -Fq -e "\"$search\"" -e "\"$search/" "$f" 2>/dev/null; then
                    candidate="$f"
                    break
                fi
            done < "$TMP_DIR/govulncheck.tracked.txt"
            [ -n "$candidate" ] && break
        done
        if [ -z "$candidate" ]; then
            echo "  [skip] $osv_id: no tracked file imports $module" >&2
            echo "SKIP" >> "$TMP_DIR/govulncheck.skips"
            continue
        fi
        fid="$(lookup_fid "$candidate")"
        [ -z "$fid" ] && continue

        summary="$(jq -r --arg id "$osv_id" '.[] | select(.id == $id) | .summary' "$TMP_DIR/govulncheck.osvs.json" | head -1)"
        [ -z "$summary" ] && summary="$module"
        title_raw="[$osv_id] $module"
        title="$(truncate_str 200 "$title_raw")"
        if [ -n "$fixed" ]; then
            desc_raw="fixed in $fixed — $summary"
        else
            desc_raw="no fixed version — $summary"
        fi
        desc="$(truncate_str 2000 "$desc_raw")"
        ft="$(sql_escape "$title")"
        fd="$(sql_escape "$desc")"
        printf "INSERT OR IGNORE INTO findings (file_id, line, tool, source, severity, category, title, description, status) VALUES (%s, NULL, 'govulncheck', 'static-tool', 'critical', 'security', '%s', '%s', 'open');\n" \
            "$fid" "$ft" "$fd" >> "$batch"
        echo "INS" >> "$TMP_DIR/govulncheck.ins"
    done
    attempted="$(count_lines "$TMP_DIR/govulncheck.ins")"
    GOVULNCHECK_SKIP="$(count_lines "$TMP_DIR/govulncheck.skips")"
    write_batch_footer "$batch"

    if [ "$attempted" -gt 0 ]; then
        result="$(apply_batch govulncheck "$batch" "$attempted")"
        GOVULNCHECK_NEW="${result% *}"
        GOVULNCHECK_DUP="${result#* }"
    fi
}

# ---- errcheck --------------------------------------------------------
scan_errcheck() {
    echo "[4/5] errcheck..."
    if ! command -v errcheck >/dev/null 2>&1; then
        echo "  [!] errcheck not installed — run 'make audit-tools'"
        return
    fi
    ERRCHECK_STATE=ran
    out="$TMP_DIR/errcheck.txt"
    # errcheck exits 1 when it finds issues; treat exit >=2 as failure.
    # -ignoretests: test files have intentional unchecked errors (e.g.
    # deferred closes in test servers); scope them out of the scan.
    errcheck -ignoretests ./... > "$out" 2>"$TMP_DIR/errcheck.err" || rc=$?
    if [ "${rc:-0}" -ge 2 ]; then
        echo "  [!] errcheck failed (exit ${rc}); see $TMP_DIR/errcheck.err" >&2
        return
    fi
    rc=0
    if [ ! -s "$out" ]; then
        return
    fi

    batch="$TMP_DIR/errcheck.sql"
    write_batch_header "$batch"
    # Lines look like: path:line:col\tfunction_call
    while IFS="	" read -r loc call; do
        [ -z "$loc" ] && continue
        [ -z "$call" ] && continue
        # Split path:line:col.
        file="${loc%%:*}"
        rest="${loc#*:}"
        line="${rest%%:*}"
        rel="$(rel_path "$file")"
        fid="$(lookup_fid "$rel")"
        if [ -z "$fid" ]; then
            echo "  [skip] untracked path: $rel" >&2
            echo "SKIP" >> "$TMP_DIR/errcheck.skips"
            continue
        fi
        title_raw="unchecked error: $call"
        title="$(truncate_str 200 "$title_raw")"
        desc_raw="$loc	$call"
        desc="$(truncate_str 2000 "$desc_raw")"
        ft="$(sql_escape "$title")"
        fd="$(sql_escape "$desc")"
        printf "INSERT OR IGNORE INTO findings (file_id, line, tool, source, severity, category, title, description, status) VALUES (%s, %s, 'errcheck', 'static-tool', 'medium', 'bugs', '%s', '%s', 'open');\n" \
            "$fid" "$line" "$ft" "$fd" >> "$batch"
        echo "INS" >> "$TMP_DIR/errcheck.ins"
    done < "$out"
    attempted="$(count_lines "$TMP_DIR/errcheck.ins")"
    ERRCHECK_SKIP="$(count_lines "$TMP_DIR/errcheck.skips")"
    write_batch_footer "$batch"

    if [ "$attempted" -gt 0 ]; then
        result="$(apply_batch errcheck "$batch" "$attempted")"
        ERRCHECK_NEW="${result% *}"
        ERRCHECK_DUP="${result#* }"
    fi
}

# ---- ineffassign -----------------------------------------------------
# ineffassign writes findings to stderr and exits non-zero when it finds
# things. Each line:  file:line:col: message
scan_ineffassign() {
    echo "[5/5] ineffassign..."
    if ! command -v ineffassign >/dev/null 2>&1; then
        echo "  [!] ineffassign not installed — run 'make audit-tools'"
        return
    fi
    INEFFASSIGN_STATE=ran
    err="$TMP_DIR/ineffassign.err"
    # Don't care about exit code; findings arrive on stderr.
    ineffassign ./... >/dev/null 2>"$err" || true

    if [ ! -s "$err" ]; then
        return
    fi

    batch="$TMP_DIR/ineffassign.sql"
    write_batch_header "$batch"
    while IFS= read -r raw; do
        [ -z "$raw" ] && continue
        # Strip any "tool failed" banner lines; real findings contain ":line:col:".
        case "$raw" in
            *:*:*:*) : ;;
            *) continue ;;
        esac
        file="${raw%%:*}"
        rest="${raw#*:}"
        line="${rest%%:*}"
        after_line="${rest#*:}"
        # after_line still starts with "col: message"; skip the col and the space.
        message="${after_line#*: }"
        rel="$(rel_path "$file")"
        fid="$(lookup_fid "$rel")"
        if [ -z "$fid" ]; then
            echo "  [skip] untracked path: $rel" >&2
            echo "SKIP" >> "$TMP_DIR/ineffassign.skips"
            continue
        fi
        title="$(truncate_str 200 "$message")"
        desc="$(truncate_str 2000 "$raw")"
        ft="$(sql_escape "$title")"
        fd="$(sql_escape "$desc")"
        printf "INSERT OR IGNORE INTO findings (file_id, line, tool, source, severity, category, title, description, status) VALUES (%s, %s, 'ineffassign', 'static-tool', 'low', 'bugs', '%s', '%s', 'open');\n" \
            "$fid" "$line" "$ft" "$fd" >> "$batch"
        echo "INS" >> "$TMP_DIR/ineffassign.ins"
    done < "$err"
    attempted="$(count_lines "$TMP_DIR/ineffassign.ins")"
    INEFFASSIGN_SKIP="$(count_lines "$TMP_DIR/ineffassign.skips")"
    write_batch_footer "$batch"

    if [ "$attempted" -gt 0 ]; then
        result="$(apply_batch ineffassign "$batch" "$attempted")"
        INEFFASSIGN_NEW="${result% *}"
        INEFFASSIGN_DUP="${result#* }"
    fi
}

# ---------------------------------------------------------------------------
# Architecture report (Go/Omnispoon-specific analysis)
# ---------------------------------------------------------------------------
#
# Tunables. Kept modest so the prompt stays digestible. TOP_FILES lives in core
# (Section C uses it); the rest are private to the Go sections here.
TOP_COMPLEXITY=15
CYCLO_OVER=10

# audit_arch_preflight: verify the Go toolchain and resolve the module path.
# Sets AUDIT_ARCH_MODULE (used by the core report header and the section funcs).
audit_arch_preflight() {
    if [ ! -f "go.mod" ]; then
        echo "go.mod not found in $REPO_ROOT; arch-sweep must be run from the repo root." >&2
        return 1
    fi
    # Module path used to detect internal imports. `go list -m` is the source of
    # truth — avoids hand-parsing go.mod.
    AUDIT_ARCH_MODULE="$(go list -m 2>/dev/null)"
    if [ -z "$AUDIT_ARCH_MODULE" ]; then
        echo "Failed to read module path via 'go list -m'." >&2
        return 1
    fi
}

# audit_arch_resolve_nondir <arg>: map a fully-qualified import path back to its
# repo-relative subtree by stripping the module prefix. Prints nothing if the
# arg doesn't carry the module prefix (core then reports "not a directory").
audit_arch_resolve_nondir() {
    arg="$1"
    case "$arg" in
        "$AUDIT_ARCH_MODULE"/*) printf '%s' "${arg#$AUDIT_ARCH_MODULE/}" ;;
    esac
}

# audit_arch_has_sources <dir>: return zero if <dir> holds Go source files.
# Reproduces the core's former hardcoded check: a top-level *.go glob, then a
# bounded recursive find. Lets the core reject an empty/non-source subtree
# before the analyzers run.
audit_arch_has_sources() {
    ls "$1"/*.go >/dev/null 2>&1 \
        || find "$1" -maxdepth 3 -name '*.go' -print -quit | grep -q .
}

# audit_arch_build_graph: produce the package list ($TMP_DIR/target.pkgs), the
# internal-import graph ($TMP_DIR/rel.imports), and the in-scope package list
# ($TMP_DIR/target.rel). Scoped to TARGET_SCOPE/TARGET_DIR.
audit_arch_build_graph() {
    # Package list for the target subtree (cmd/* included in whole-repo mode so
    # the dep graph is complete).
    if [ "$TARGET_SCOPE" = "whole-repo" ]; then
        list_arg="./..."
    else
        list_arg="./$TARGET_SCOPE/..."
    fi
    # `|| true` so an import cycle or build error surfaces via the captured
    # stderr below, rather than aborting silently under `set -e`.
    go list "$list_arg" > "$TMP_DIR/target.pkgs" 2>"$TMP_DIR/list.err" || true
    if [ ! -s "$TMP_DIR/target.pkgs" ]; then
        echo "arch-sweep: 'go list $list_arg' returned no packages." >&2
        [ -s "$TMP_DIR/list.err" ] && sed 's/^/  /' "$TMP_DIR/list.err" >&2
        return 1
    fi

    # Every package in the repo, not just the target — needed for fan-in.
    go list -f '{{.ImportPath}}|{{join .Imports ","}}' ./... \
        > "$TMP_DIR/all.imports" 2>/dev/null

    # Filter each line's imports to only internal (module-local) deps, and
    # strip the module prefix so output is repo-relative. Output format:
    # "<pkg-relative>|<comma-list of relative deps>"
    awk -F'|' -v mod="$AUDIT_ARCH_MODULE" '
    {
        pkg = $1
        sub("^" mod "/", "", pkg)
        sub("^" mod "$", ".", pkg)
        n = split($2, imps, ",")
        out = ""
        for (i = 1; i <= n; i++) {
            imp = imps[i]
            if (index(imp, mod "/") == 1) {
                rel = substr(imp, length(mod) + 2)
                if (out == "") out = rel
                else out = out "," rel
            }
        }
        print pkg "|" out
    }' "$TMP_DIR/all.imports" > "$TMP_DIR/rel.imports"

    # Package list (for iteration), scoped to the target.
    awk -F'|' -v mod="$AUDIT_ARCH_MODULE" '
    {
        pkg = $1
        sub("^" mod "/", "", pkg)
        sub("^" mod "$", ".", pkg)
        print pkg
    }' "$TMP_DIR/target.pkgs" > "$TMP_DIR/target.rel"
}

# -------- Section A: Import graph (fan-in / fan-out) ------------------

audit_arch_section_a() {
    echo ""
    echo "=== Section A: Import graph ==="
    echo ""
    printf "  %-34s %-16s %s\n" "Package" "Fan-out (deps)" "Fan-in (importers)"
    printf "  %-34s %-16s %s\n" "----------------------------------" "--------------" "---------------------------------"

    # For each in-scope package, compute:
    #   fan_out = count of internal imports on its own line in rel.imports
    #   fan_in  = count of OTHER packages whose import list contains this one
    #             (exact match between commas; not substring)
    while IFS= read -r pkg; do
        fan_out=0
        imp_line=$(awk -F'|' -v p="$pkg" '$1 == p { print $2 }' "$TMP_DIR/rel.imports")
        if [ -n "$imp_line" ]; then
            fan_out=$(printf '%s' "$imp_line" | awk -F',' '{ print NF }')
        fi

        # Fan-in: walk every other line; each line's imports are comma-separated.
        # Use awk so we don't spawn grep per package.
        fan_in=$(awk -F'|' -v p="$pkg" '
            $1 != p && $2 != "" {
                n = split($2, a, ",")
                for (i = 1; i <= n; i++) if (a[i] == p) { print $1; next }
            }
        ' "$TMP_DIR/rel.imports" | sort -u)
        in_count=0
        if [ -n "$fan_in" ]; then
            in_count=$(printf '%s\n' "$fan_in" | wc -l | tr -d ' ')
        fi

        # Compact sample of importers (first 3) for quick read.
        sample=$(printf '%s' "$fan_in" | head -3 | tr '\n' ',' | sed 's/,$//; s/,/, /g')
        if [ -n "$sample" ] && [ "$in_count" -gt 3 ]; then
            sample="$sample, ..."
        fi

        if [ -n "$sample" ]; then
            printf "  %-34s %-16d %d  (%s)\n" "$pkg" "$fan_out" "$in_count" "$sample"
        else
            printf "  %-34s %-16d %d\n" "$pkg" "$fan_out" "$in_count"
        fi
    done < "$TMP_DIR/target.rel"
}

# -------- Section B: Complexity hotspots ------------------------------

audit_arch_section_b() {
    echo ""
    echo "=== Section B: Complexity hotspots (cyclomatic > $CYCLO_OVER) ==="
    echo ""
    if ! command -v gocyclo >/dev/null 2>&1; then
        echo "  [!] gocyclo not installed; skipping. Install with: make audit-tools"
        return
    fi

    # gocyclo emits one line per function: "<complexity> <pkg> <name> <file:line:col>".
    # Exit codes: 0 = no over-threshold funcs, 1 = some funcs over (or parse
    # error — disambiguate by checking whether stdout has any matches).
    out="$TMP_DIR/gocyclo.out"
    err="$TMP_DIR/gocyclo.err"
    gocyclo -over "$CYCLO_OVER" "$TARGET_DIR" > "$out" 2>"$err" || rc=$?
    if [ "${rc:-0}" -gt 1 ]; then
        echo "  [!] gocyclo failed (exit ${rc}):" >&2
        [ -s "$err" ] && sed 's/^/    /' "$err" >&2
        return
    fi
    # rc=1 with empty stdout + non-empty stderr = parse error, not "no hits".
    # Surface it so the user doesn't silently miss broken files.
    if [ "${rc:-0}" -eq 1 ] && [ ! -s "$out" ] && [ -s "$err" ]; then
        echo "  [!] gocyclo reported errors:" >&2
        sed 's/^/    /' "$err" >&2
    fi
    rc=0
    if [ ! -s "$out" ]; then
        echo "  (no functions exceed cyclomatic $CYCLO_OVER)"
        return
    fi

    printf "  %-11s %-48s %s\n" "Complexity" "Function" "File:Line"
    printf "  %-11s %-48s %s\n" "----------" "------------------------------------------------" "---------------------------------"

    # Sort numerically descending; take top N. Strip the REPO_ROOT prefix from
    # file paths so output is repo-relative. Trim trailing :col to file:line.
    sort -rn -k1,1 "$out" | head -n "$TOP_COMPLEXITY" \
        | awk -v root="$REPO_ROOT/" '
        {
            cx = $1
            loc = $NF
            sub("^" root, "", loc)
            n = split(loc, parts, ":")
            if (n >= 3) { loc = parts[1] ":" parts[2] }
            fn = ""
            for (i = 2; i <= NF-1; i++) fn = fn (fn == "" ? "" : " ") $i
            printf "  %-11s %-48s %s\n", cx, fn, loc
        }'
}

# -------- Section D: Public API surface -------------------------------

audit_arch_section_d() {
    echo ""
    echo "=== Section D: Public API surface ==="
    echo ""
    printf "  %-34s %-18s %s\n" "Package" "Exported symbols" "Exported funcs"
    printf "  %-34s %-18s %s\n" "----------------------------------" "----------------" "--------------"

    # For each in-scope package, run `go doc -short` (one line per exported
    # symbol) and count top-level funcs from the same output. Using -short for
    # both keeps funcs ⊆ symbols — counting methods via `go doc -all` produced
    # misleadingly large numbers (e.g. 165 for internal/db) that don't compare
    # against the symbol count.
    while IFS= read -r pkg; do
        if [ "$pkg" = "." ]; then
            imp="$AUDIT_ARCH_MODULE"
        else
            imp="$AUDIT_ARCH_MODULE/$pkg"
        fi
        short=$(go doc -short "$imp" 2>/dev/null || true)
        if [ -z "$short" ]; then
            sym_count=0
            funcs=0
        else
            sym_count=$(printf '%s\n' "$short" | wc -l | tr -d ' ')
            funcs=$(printf '%s\n' "$short" | awk '/^func /{ c++ } END { print c+0 }')
        fi
        printf "  %-34s %-18s %s\n" "$pkg" "$sym_count" "$funcs"
    done < "$TMP_DIR/target.rel"
}

# -------- Section E: Layer violations ---------------------------------
#
# These rules are specific to omnispoon and hard-coded per the brief. They
# work off rel.imports so we reuse parsed data rather than re-running go list.
#
# Rule caveats:
#   - `internal/models` should be pure data. The brief says "stdlib and its
#     own package", but in practice `models` imports
#     `github.com/go-webauthn/webauthn/webauthn` intentionally (the User
#     type embeds webauthn.Credential). Treating that as a violation would
#     be a false positive, so the rule below flags only *internal*
#     (module-local) imports outside the models subtree. Third-party
#     imports are allowed in models for now.
#   - `internal/db` shouldn't know about `internal/web`.
#   - `internal/crypto` should only import stdlib; module-local only if
#     importing `internal/models`.
#   - No `_test.go` file may be imported from non-test code. Go rejects
#     this at build time, but we still scan — a stale generated import
#     line is easier to spot here than in a build error.

audit_arch_section_e() {
    echo ""
    echo "=== Section E: Layer violations ==="
    echo ""

    violations="$TMP_DIR/violations.txt"
    : > "$violations"

    # When scoped to a subtree, only consider packages inside it. target.rel
    # already holds the in-scope package list (repo-relative). For whole-repo
    # mode the grep pattern below is "." so every package matches.
    if [ "$TARGET_SCOPE" = "whole-repo" ]; then
        scope_filter="$TMP_DIR/rel.imports"
    else
        scope_filter="$TMP_DIR/rel.imports.scoped"
        awk -F'|' 'NR==FNR { keep[$1]=1; next } keep[$1]' \
            "$TMP_DIR/target.rel" "$TMP_DIR/rel.imports" > "$scope_filter"
    fi

    # Rule 1: internal/models importing other internal/* packages.
    awk -F'|' '
        $1 ~ /^internal\/models(\/.*)?$/ && $2 != "" {
            n = split($2, a, ",")
            for (i = 1; i <= n; i++) {
                if (a[i] ~ /^internal\// && a[i] !~ /^internal\/models(\/.*)?$/) {
                    print "  [models-leak] " $1 " imports " a[i]
                }
            }
        }
    ' "$scope_filter" >> "$violations"

    # Rule 2: internal/db importing internal/web.
    awk -F'|' '
        $1 ~ /^internal\/db(\/.*)?$/ && $2 != "" {
            n = split($2, a, ",")
            for (i = 1; i <= n; i++) {
                if (a[i] ~ /^internal\/web(\/.*)?$/) {
                    print "  [db-to-web]   " $1 " imports " a[i]
                }
            }
        }
    ' "$scope_filter" >> "$violations"

    # Rule 3: internal/crypto importing any internal/* other than internal/models.
    awk -F'|' '
        $1 ~ /^internal\/crypto(\/.*)?$/ && $2 != "" {
            n = split($2, a, ",")
            for (i = 1; i <= n; i++) {
                if (a[i] ~ /^internal\// && a[i] !~ /^internal\/models(\/.*)?$/) {
                    print "  [crypto-leak] " $1 " imports " a[i]
                }
            }
        }
    ' "$scope_filter" >> "$violations"

    # Rule 4: any non-test .go file importing a _test package. Scan the
    # files table for non-test sources and grep for `"…_test"` in import
    # clauses. Keep it light — we don't parse Go, just pattern-match the
    # common import-block shapes. Scope to TARGET_FILE_LIKE so an
    # `arch-sweep internal/web` run only flags files in that subtree.
    sqlite3 -separator '|' "$DB" "
        SELECT path FROM files
        WHERE path LIKE '$TARGET_FILE_LIKE'
          AND path LIKE '%.go'
          AND path NOT LIKE '%_test.go';" \
    | while IFS= read -r f; do
        [ -f "$f" ] || continue
        bad=$(awk '
            /^import \(/        { in_block = 1; next }
            in_block && /^\)/   { in_block = 0; next }
            {
                line = $0
                if (in_block || line ~ /^import "/ || line ~ /^import [A-Za-z_]+ "/) {
                    if (match(line, /"[^"]*_test"/)) { print line }
                }
            }
        ' "$f")
        if [ -n "$bad" ]; then
            printf '%s\n' "$bad" | while IFS= read -r l; do
                printf "  [test-in-prod] %s: %s\n" "$f" "$l" >> "$violations"
            done
        fi
    done

    if [ -s "$violations" ]; then
        cat "$violations"
    else
        echo "  No layer violations detected."
    fi
}
