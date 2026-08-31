#!/bin/sh
# Architecture-report generator for the audit system.
#
# Produces a textual report for a target (one source subtree, or the whole
# repo if no arg) followed by a Claude prompt that embeds the same report.
# The prompt asks Claude to review the architecture with numbers in hand
# and record a sweeps.sweep_type='architecture' row when done.
#
# Called from `audit/audit.sh arch-sweep` and from `make audit-arch`.
# Kept out of audit.sh so the dispatcher stays short (same pattern as scan.sh).
#
# This file is the language-neutral core: generic directory target resolution,
# Section C (largest files, a pure files-table query), report assembly, and the
# Claude-prompt skeleton. Every language/project specific decision — toolchain
# preflight, the import graph, and Sections A/B/D/E — comes from the adapter
# selected by AUDIT_ADAPTER (default "go"); see audit/adapters/go.sh for the
# arch contract.
set -e

DB="audit/audit.db"
REPO_ROOT="$(pwd)"

# Language adapter: provides the arch preflight, package-graph build, and the
# language-specific Section A/B/D/E bodies. The generic skeleton below stays
# language-neutral. Selected by AUDIT_ADAPTER (default "go").
AUDIT_ADAPTER="${AUDIT_ADAPTER:-go}"
ADAPTER_FILE="$(dirname "$0")/adapters/${AUDIT_ADAPTER}.sh"
if [ ! -f "$ADAPTER_FILE" ]; then
    echo "Audit adapter not found: $ADAPTER_FILE (set AUDIT_ADAPTER to a valid adapter)." >&2
    exit 1
fi
. "$ADAPTER_FILE"

if [ ! -f "$DB" ]; then
    echo "No audit database found at $DB. Run: audit/audit.sh init" >&2
    exit 1
fi

# Working directory for temp files; cleaned on any exit.
TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT INT HUP TERM

# Verify the language toolchain/preconditions and make module/graph state
# available to the section functions (adapter-provided).
audit_arch_preflight || exit 1

# Tunable used by the core Section C. Section-specific tunables (complexity
# thresholds, etc.) live in the adapter with the sections that use them.
TOP_FILES=15

TARGET="${1:-}"

# Resolve target into:
#   TARGET_SCOPE      — label for the report (e.g. "whole-repo" or a subtree path)
#   TARGET_DIR        — directory for the adapter's analyzers (the repo root for
#                       whole-repo mode, or the subtree directory for scoped mode)
#   TARGET_FILE_LIKE  — SQL LIKE pattern matching files table rows in scope
#
# This is generic directory-level resolution. The only language-specific step is
# mapping a non-directory arg (e.g. a fully-qualified import path) back to a
# subtree, which the adapter contributes via audit_arch_resolve_nondir.
resolve_target() {
    # Treat `.`, `./`, and the repo root as whole-repo mode so LIKE patterns
    # and scope labels don't break on `arch-sweep .`.
    case "$TARGET" in
        ""|"."|"./") TARGET="" ;;
        "$REPO_ROOT"|"$REPO_ROOT/") TARGET="" ;;
    esac
    if [ -z "$TARGET" ]; then
        TARGET_SCOPE="whole-repo"
        TARGET_DIR="$REPO_ROOT"
        TARGET_FILE_LIKE="%"
        return
    fi

    # Scoped mode. Accept either a relative subtree path or a path the
    # adapter can map to one. Trim leading ./ for robustness.
    arg="${TARGET#./}"
    if [ ! -d "$arg" ]; then
        # Not a directory as-is; let the adapter try to resolve it (Go strips
        # the module prefix off a fully-qualified import path).
        resolved="$(audit_arch_resolve_nondir "$arg" 2>/dev/null || true)"
        [ -n "$resolved" ] && arg="$resolved"
    fi
    if [ ! -d "$arg" ]; then
        echo "arch-sweep: target '$TARGET' is not a directory under $REPO_ROOT." >&2
        echo "  Use a repo-relative subtree path (e.g. 'src/server' or 'cmd/app')." >&2
        exit 1
    fi
    # Validate it's actually a source subtree before handing it to the adapter,
    # which otherwise emits long stderr spam. The notion of "source file" is
    # language-specific, so the check lives behind the adapter contract. If an
    # adapter doesn't implement audit_arch_has_sources, skip the check and let
    # the graph build surface a clear empty-scope error rather than falsely
    # rejecting a valid (non-Go) source tree.
    if command -v audit_arch_has_sources >/dev/null 2>&1 \
       && ! audit_arch_has_sources "$arg"; then
        echo "arch-sweep: '$arg' contains no source files." >&2
        exit 1
    fi

    TARGET_SCOPE="$arg"
    TARGET_DIR="$REPO_ROOT/$arg"
    TARGET_FILE_LIKE="$arg/%"
}

# -------- Section C: File-size ranking --------------------------------
#
# Generic: a pure files-table query by line_count, scoped via TARGET_FILE_LIKE.

section_c() {
    echo ""
    echo "=== Section C: Largest files ==="
    echo ""
    printf "  %-7s %s\n" "Lines" "File"
    printf "  %-7s %s\n" "-----" "----"
    sqlite3 -separator '|' "$DB" "
        SELECT line_count, path FROM files
        WHERE path LIKE '$TARGET_FILE_LIKE'
        ORDER BY line_count DESC
        LIMIT $TOP_FILES;" \
    | awk -F'|' '{ printf "  %-7s %s\n", $1, $2 }'
}

# arch_section <letter> <adapter-fn>: emit an adapter-provided section, or a
# placeholder if this adapter doesn't implement it. The Go adapter implements
# all of A/B/D/E, so Go output is unchanged.
arch_section() {
    letter="$1"
    fn="$2"
    if command -v "$fn" >/dev/null 2>&1; then
        "$fn"
    else
        echo ""
        echo "=== Section $letter ==="
        echo ""
        echo "  (not available for adapter '$AUDIT_ADAPTER')"
    fi
}

# -------- Render report -----------------------------------------------

resolve_target
audit_arch_build_graph || exit 1

# Build the report body once and reuse it for both stdout and the Claude
# prompt. Running each section twice would double the cost of the adapter's
# per-package analysis.
REPORT_BODY="$TMP_DIR/report.txt"
{
    cat <<EOF
Architecture report for: $TARGET_SCOPE
Module:                  $AUDIT_ARCH_MODULE
Generated:               $(date -u +'%Y-%m-%dT%H:%M:%SZ')
EOF
    arch_section A audit_arch_section_a
    arch_section B audit_arch_section_b
    section_c
    arch_section D audit_arch_section_d
    arch_section E audit_arch_section_e
    echo ""
    echo "=== End of report ==="
} > "$REPORT_BODY"

cat "$REPORT_BODY"
echo ""

# -------- Claude prompt -----------------------------------------------

BUILD_CMD="$(audit_verify_build_cmd)"
TEST_CMD="$(audit_verify_test_cmd)"

cat <<EOF
--- Paste this into Claude Code ---

Architecture sweep of: $TARGET_SCOPE
Follow the sweep skill at \`.claude/skills/sweep/SKILL.md\` (Phase 3) — the report below is the data to work from.

Build command: ${BUILD_CMD}
Test command: ${TEST_CMD}

=== Architecture report ===

EOF
cat "$REPORT_BODY"
