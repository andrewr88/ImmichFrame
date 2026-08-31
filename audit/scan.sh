#!/bin/sh
# Static-analyzer scan driver for the audit system.
#
# Runs each installed analyzer, parses its output, and upserts findings
# into audit/audit.db with source='static-tool' and the correct tool name.
# Idempotent via the partial unique index idx_findings_dedup defined in
# audit/schema.sql — repeated scans on an unchanged tree insert zero rows.
#
# Called from audit/audit.sh and from `make audit-scan`.
set -e

DB="audit/audit.db"
REPO_ROOT="$(pwd)"

# Language adapter: provides the analyzer catalog (AUDIT_SCAN_TOOLS, the
# scan_<tool> drivers, their counter/state variables, and the audit_scan_*
# accessors). The generic harness below stays language-neutral. Selected by
# AUDIT_ADAPTER (default "go"); sourced after the harness helpers are defined
# so the drivers can call them.
AUDIT_ADAPTER="${AUDIT_ADAPTER:-go}"
ADAPTER_FILE="$(dirname "$0")/adapters/${AUDIT_ADAPTER}.sh"
if [ ! -f "$ADAPTER_FILE" ]; then
    echo "Audit adapter not found: $ADAPTER_FILE (set AUDIT_ADAPTER to a valid adapter)." >&2
    exit 1
fi

if [ ! -f "$DB" ]; then
    echo "No audit database found at $DB. Run: audit/audit.sh init" >&2
    exit 1
fi

if ! command -v jq >/dev/null 2>&1; then
    cat >&2 <<'EOF'
jq is not installed but is required for JSON parsing.
Install it:  apt-get install jq   (Debian/Ubuntu)
             brew install jq       (macOS)
EOF
    exit 1
fi

# Working directory for temp files; cleaned on any exit.
TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT INT HUP TERM

# file_id cache: the files table is loaded once (path<TAB>id) before scans
# run. With 800+ findings, spawning sqlite3 per record cost tens of seconds;
# this turns every lookup into an awk match against a small file.
FILES_CACHE="$TMP_DIR/files.cache"

# lookup_fid <relpath>: print file id, empty if not tracked.
lookup_fid() {
    awk -F'\t' -v p="$1" '$1 == p { print $2; exit }' "$FILES_CACHE"
}

# sql_escape <value>: double single quotes for safe SQL interpolation.
sql_escape() {
    printf '%s' "$1" | sed "s/'/''/g"
}

# rel_path <abs_or_rel>: strip leading REPO_ROOT/ or ./ so the path matches
# files.path (which is stored repo-relative).
rel_path() {
    # Strip leading REPO_ROOT/ if present.
    case "$1" in
        "$REPO_ROOT"/*) printf '%s' "${1#$REPO_ROOT/}" ;;
        ./*)            printf '%s' "${1#./}" ;;
        *)              printf '%s' "$1" ;;
    esac
}

# truncate <maxlen> <str>: POSIX-safe length cap.
truncate_str() {
    max="$1"; val="$2"
    printf '%s' "$val" | awk -v max="$max" '{
        if (length($0) > max) print substr($0, 1, max); else print $0
    }'
}

# count_lines <file>: number of lines in a file, or 0 if absent. Trims
# the leading whitespace some wc implementations emit.
count_lines() {
    if [ -f "$1" ]; then
        wc -l < "$1" | tr -d ' \t'
    else
        printf '0'
    fi
}

# Apply a .sql batch and report (new, duplicate) counts by diffing
# total open-findings-for-this-tool before vs after.
apply_batch() {
    tool="$1"; batch="$2"; attempted="$3"
    before="$(sqlite3 "$DB" "SELECT COUNT(*) FROM findings WHERE source='static-tool' AND tool='$tool' AND status='open';")"
    sqlite3 "$DB" < "$batch"
    after="$(sqlite3 "$DB" "SELECT COUNT(*) FROM findings WHERE source='static-tool' AND tool='$tool' AND status='open';")"
    new=$((after - before))
    dup=$((attempted - new))
    [ "$dup" -lt 0 ] && dup=0
    printf '%s %s' "$new" "$dup"
}

# Header for every batch file: wrap inserts in a transaction and turn on
# foreign keys (schema enforces file_id referential integrity).
write_batch_header() {
    batch="$1"
    printf 'PRAGMA foreign_keys=ON;\nBEGIN;\n' > "$batch"
}

write_batch_footer() {
    batch="$1"
    printf 'COMMIT;\n' >> "$batch"
}

# Source the language adapter now that the harness helpers above are defined
# (the scan_<tool> drivers call them). The adapter supplies AUDIT_SCAN_TOOLS,
# the drivers, their counter/state variables, and the audit_scan_* accessors.
. "$ADAPTER_FILE"

# ---- main ------------------------------------------------------------

# Refresh the files table before scanning. Without this, a brand-new source
# file that hasn't been synced would be silently skipped as "untracked path".
echo "Syncing files..."
sh "$(dirname "$0")/audit.sh" sync >/dev/null

# Load FILES_CACHE from the (now up-to-date) files table.
sqlite3 -separator "	" "$DB" "SELECT path, id FROM files;" > "$FILES_CACHE"

# Run each analyzer driver in the adapter's declared order.
for tool in $AUDIT_SCAN_TOOLS; do
    "scan_$tool"
done

# Check that at least one tool ran; otherwise the setup is broken.
any_ran=0
for tool in $AUDIT_SCAN_TOOLS; do
    [ "$(audit_scan_state "$tool")" = "ran" ] && any_ran=1
done
if [ "$any_ran" -eq 0 ]; then
    echo "" >&2
    echo "All analyzers are missing. Install them with: make audit-tools" >&2
    exit 1
fi

total_new=0
for tool in $AUDIT_SCAN_TOOLS; do
    total_new=$((total_new + $(audit_scan_new "$tool")))
done

# Per-tool line formatter.
fmt_line() {
    name="$1"; state="$2"; new="$3"; dup="$4"; skip="$5"
    if [ "$state" = "missing" ]; then
        printf "  %-12s not installed (run 'make audit-tools')\n" "$name"
        return
    fi
    if [ "$skip" -gt 0 ]; then
        printf "  %-12s %3d new, %3d duplicate, %d skipped (untracked paths)\n" "$name" "$new" "$dup" "$skip"
    else
        printf "  %-12s %3d new, %3d duplicate\n" "$name" "$new" "$dup"
    fi
}

echo ""
echo "Scan complete."
for tool in $AUDIT_SCAN_TOOLS; do
    fmt_line "$(audit_scan_label "$tool")" "$(audit_scan_state "$tool")" \
             "$(audit_scan_new "$tool")" "$(audit_scan_dup "$tool")" "$(audit_scan_skip "$tool")"
done
echo ""

if [ "$total_new" -eq 0 ]; then
    echo "No new findings. All analyzer output matches findings already on file."
else
    echo "$total_new new findings seeded with source='static-tool'."
fi
echo "Next: 'audit/audit.sh findings' to review, or 'audit/audit.sh sweep' to let Claude triage."
