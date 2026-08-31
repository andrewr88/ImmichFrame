#!/bin/sh
# Audit DB in-place migration.
#
# ---------------------------------------------------------------------------
# ONE-OFF SCRIPT. NOT A GENERAL MIGRATION FRAMEWORK.
#
# This is a single-use migration for the T1 schema extensions (2026-04-16)
# plus the Phase 2 re-qualification view change and the dashboard
# files_needing_audit column (2026-06-09). It is intended to be deleted
# once all users have migrated to the extended schema. Avoid adding
# further migration steps here — treat future schema changes with a
# fresh, purpose-built script (or a proper migration tool).
#
# NOTE: the view definitions for v_packages, v_next_package_sweep,
# v_sweep_history, and v_dashboard inside steps 4-6 are duplicated from
# audit/schema.sql. They must be kept in sync with schema.sql for as long
# as this script exists.
# ---------------------------------------------------------------------------
#
# Applies schema changes to an existing audit/audit.db without rebuilding
# from scratch. Safe to re-run: each step checks whether it has already
# been applied and no-ops if so.
#
# Currently handles:
#   - Add findings.source (TEXT NOT NULL DEFAULT 'llm' CHECK(source IN ('llm','static-tool')))
#   - Add findings.tool   (TEXT NOT NULL DEFAULT '')
#   - Add partial unique index idx_findings_dedup for static-tool findings
#   - Extend sweeps.sweep_type CHECK to include 'architecture'
#   - Recreate v_next_package_sweep with changed_since_sweep
#     re-qualification (2026-06-09), tightened so only files that
#     existed at sweep time (first_seen_at <= last_swept) count
#   - Recreate v_dashboard with the uncapped files_needing_audit count
#   - Normalize legacy 'whole repo' sweeps.scope rows to 'whole-repo'
#
# Usage: audit/migrate.sh
#
# For a fresh install, use audit/audit.sh init instead (it applies
# audit/schema.sql directly).
set -e

DB="audit/audit.db"

if [ ! -f "$DB" ]; then
    echo "No database at $DB. Nothing to migrate."
    echo "Run 'audit/audit.sh init' to create a fresh DB."
    exit 0
fi

echo "Migrating $DB..."

# --- Step 1: findings.source -----------------------------------------------
has_source=$(sqlite3 "$DB" "SELECT COUNT(*) FROM pragma_table_info('findings') WHERE name='source';")
if [ "$has_source" = "0" ]; then
    echo "  [+] adding findings.source"
    sqlite3 "$DB" <<'SQL'
PRAGMA foreign_keys=ON;
ALTER TABLE findings ADD COLUMN source TEXT NOT NULL DEFAULT 'llm'
    CHECK(source IN ('llm', 'static-tool'));
SQL
else
    echo "  [=] findings.source already present"
fi

# --- Step 2: findings.tool -------------------------------------------------
has_tool=$(sqlite3 "$DB" "SELECT COUNT(*) FROM pragma_table_info('findings') WHERE name='tool';")
if [ "$has_tool" = "0" ]; then
    echo "  [+] adding findings.tool"
    sqlite3 "$DB" <<'SQL'
PRAGMA foreign_keys=ON;
ALTER TABLE findings ADD COLUMN tool TEXT NOT NULL DEFAULT '';
SQL
else
    echo "  [=] findings.tool already present"
fi

# --- Step 3: dedup index ---------------------------------------------------
has_dedup=$(sqlite3 "$DB" "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='idx_findings_dedup';")
if [ "$has_dedup" = "0" ]; then
    echo "  [+] creating idx_findings_dedup"
    sqlite3 "$DB" <<'SQL'
PRAGMA foreign_keys=ON;
CREATE UNIQUE INDEX IF NOT EXISTS idx_findings_dedup
    ON findings(file_id, IFNULL(line, -1), tool, title)
    WHERE status = 'open' AND source = 'static-tool';
SQL
else
    echo "  [=] idx_findings_dedup already present"
fi

# --- Step 4: sweeps.sweep_type CHECK extended to include 'architecture' ----
# SQLite can't ALTER a CHECK constraint, so we rebuild the sweeps table.
# Views v_packages and v_sweep_history reference sweeps and would break
# the DROP, so we drop and recreate them inside the same transaction.
# Detect whether the CHECK already permits 'architecture' by inspecting
# the stored CREATE TABLE SQL.
sweep_sql=$(sqlite3 "$DB" "SELECT sql FROM sqlite_master WHERE type='table' AND name='sweeps';")
case "$sweep_sql" in
    *architecture*)
        echo "  [=] sweeps.sweep_type already allows 'architecture'"
        ;;
    *)
        echo "  [+] rebuilding sweeps to extend sweep_type CHECK"
        sqlite3 -bail "$DB" <<'SQL'
PRAGMA foreign_keys=OFF;
BEGIN;

DROP VIEW IF EXISTS v_sweep_history;
DROP VIEW IF EXISTS v_next_package_sweep;
DROP VIEW IF EXISTS v_packages;

CREATE TABLE sweeps_new (
    id              INTEGER PRIMARY KEY,
    scope           TEXT NOT NULL,
    sweep_type      TEXT NOT NULL DEFAULT 'package' CHECK(sweep_type IN (
                        'package', 'cross-cutting', 'architecture'
                    )),
    started_at      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
    finished_at     TEXT,
    findings_count  INTEGER NOT NULL DEFAULT 0,
    autofixes_count INTEGER NOT NULL DEFAULT 0,
    notes           TEXT NOT NULL DEFAULT ''
);

INSERT INTO sweeps_new
    (id, scope, sweep_type, started_at, finished_at,
     findings_count, autofixes_count, notes)
SELECT id, scope, sweep_type, started_at, finished_at,
       findings_count, autofixes_count, notes
FROM sweeps;

DROP TABLE sweeps;
ALTER TABLE sweeps_new RENAME TO sweeps;

CREATE INDEX IF NOT EXISTS idx_sweeps_scope ON sweeps(scope);

CREATE VIEW IF NOT EXISTS v_packages AS
SELECT
    REPLACE(f.path, '/' || REPLACE(REPLACE(f.path,
        RTRIM(f.path, REPLACE(f.path, '/', '')), ''), '/', ''), '') AS package,
    COUNT(DISTINCT f.id)                                             AS total_files,
    COUNT(DISTINCT CASE WHEN ac.cnt = 5 THEN f.id END)               AS fully_covered,
    SUM(f.line_count)                                                AS total_lines,
    ROUND(COUNT(DISTINCT CASE WHEN ac.cnt = 5 THEN f.id END) * 100.0
        / COUNT(DISTINCT f.id), 0)                                   AS pct_covered,
    (SELECT s.finished_at FROM sweeps s
     WHERE s.scope = REPLACE(f.path, '/' || REPLACE(REPLACE(f.path,
        RTRIM(f.path, REPLACE(f.path, '/', '')), ''), '/', ''), '')
     ORDER BY s.finished_at DESC LIMIT 1)                            AS last_swept
FROM files f
LEFT JOIN (
    SELECT file_id, COUNT(*) AS cnt FROM audit_coverage GROUP BY file_id
) ac ON ac.file_id = f.id
GROUP BY package
ORDER BY
    last_swept IS NULL DESC,
    pct_covered ASC,
    total_lines DESC;

CREATE VIEW IF NOT EXISTS v_next_package_sweep AS
SELECT package, total_files, total_lines, pct_covered, last_swept,
       changed_since_sweep
FROM (
    SELECT
        p.package,
        p.total_files,
        p.total_lines,
        p.pct_covered,
        p.last_swept,
        (SELECT COUNT(DISTINCT r.file_id)
         FROM runs r
         JOIN files f ON f.id = r.file_id
         WHERE f.path LIKE p.package || '/%'
           AND f.path NOT LIKE p.package || '/%/%'
           -- files first tracked after the sweep are additions, not
           -- changes; without this a corpus expansion (e.g. newly-
           -- tracked test files) re-qualifies unchanged packages
           AND f.first_seen_at <= p.last_swept
           AND r.finished_at IS NOT NULL
           AND r.finished_at > p.last_swept) AS changed_since_sweep
    FROM v_packages p
    WHERE p.pct_covered = 100
)
WHERE last_swept IS NULL
   OR changed_since_sweep >= MIN(total_files, MAX(3, ROUND(total_files * 0.3)))
ORDER BY total_lines DESC;

CREATE VIEW IF NOT EXISTS v_sweep_history AS
SELECT
    id AS sweep_id,
    scope,
    sweep_type,
    started_at,
    finished_at,
    findings_count,
    autofixes_count,
    notes
FROM sweeps
ORDER BY started_at DESC;

-- Verify referential integrity inside the open transaction. If any row
-- exists in pragma_foreign_key_check, the INSERT below trips a failing
-- CHECK constraint, which aborts the statement, rolls back the whole
-- transaction, and (with sqlite3 -bail + set -e) fails the script before
-- any success message is printed. RAISE(ABORT,...) would be cleaner but
-- SQLite only allows it inside a trigger body.
CREATE TEMP TABLE _fk_guard (violations INTEGER CHECK(violations = 0));
INSERT INTO _fk_guard(violations)
SELECT COUNT(*) FROM pragma_foreign_key_check;

COMMIT;
PRAGMA foreign_keys=ON;
SQL
        ;;
esac

# --- Step 5: v_next_package_sweep re-qualification on change ---------------
# Phase 2 re-qualification (2026-06-09): a swept package re-enters the
# deep-sweep queue once enough of its files changed since last_swept
# (>= 30% of files, min 3, capped at package size). Only files that
# existed at sweep time count (first_seen_at <= last_swept) — files
# first tracked after the sweep are additions, not changes. Detect a
# stale view shape by the absence of the first_seen_at guard in its SQL
# (this also catches the interim filterless changed_since_sweep shape).
# Views carry no data, so DROP + recreate is safe and idempotent.
npsweep_sql=$(sqlite3 "$DB" "SELECT sql FROM sqlite_master WHERE type='view' AND name='v_next_package_sweep';")
case "$npsweep_sql" in
    *first_seen_at*)
        echo "  [=] v_next_package_sweep already has the first_seen_at change guard"
        ;;
    *)
        echo "  [+] recreating v_next_package_sweep with re-qualification"
        sqlite3 -bail "$DB" <<'SQL'
PRAGMA foreign_keys=ON;
BEGIN;

DROP VIEW IF EXISTS v_next_package_sweep;

CREATE VIEW IF NOT EXISTS v_next_package_sweep AS
SELECT package, total_files, total_lines, pct_covered, last_swept,
       changed_since_sweep
FROM (
    SELECT
        p.package,
        p.total_files,
        p.total_lines,
        p.pct_covered,
        p.last_swept,
        (SELECT COUNT(DISTINCT r.file_id)
         FROM runs r
         JOIN files f ON f.id = r.file_id
         WHERE f.path LIKE p.package || '/%'
           AND f.path NOT LIKE p.package || '/%/%'
           -- files first tracked after the sweep are additions, not
           -- changes; without this a corpus expansion (e.g. newly-
           -- tracked test files) re-qualifies unchanged packages
           AND f.first_seen_at <= p.last_swept
           AND r.finished_at IS NOT NULL
           AND r.finished_at > p.last_swept) AS changed_since_sweep
    FROM v_packages p
    WHERE p.pct_covered = 100
)
WHERE last_swept IS NULL
   OR changed_since_sweep >= MIN(total_files, MAX(3, ROUND(total_files * 0.3)))
ORDER BY total_lines DESC;

COMMIT;
SQL
        ;;
esac

# --- Step 6: v_dashboard files_needing_audit --------------------------------
# Uncapped count of files with < 5 categories covered (2026-06-09):
# files_without_coverage only counts zero-coverage files, and
# v_next_audit_target is LIMIT 20 so counting it caps at 20. Detect a
# stale view shape by the absence of files_needing_audit in its stored
# SQL. Views carry no data, so DROP + recreate is safe and idempotent.
dashboard_sql=$(sqlite3 "$DB" "SELECT sql FROM sqlite_master WHERE type='view' AND name='v_dashboard';")
case "$dashboard_sql" in
    *files_needing_audit*)
        echo "  [=] v_dashboard already has files_needing_audit"
        ;;
    *)
        echo "  [+] recreating v_dashboard with files_needing_audit"
        sqlite3 -bail "$DB" <<'SQL'
PRAGMA foreign_keys=ON;
BEGIN;

DROP VIEW IF EXISTS v_dashboard;

CREATE VIEW IF NOT EXISTS v_dashboard AS
SELECT
    (SELECT COUNT(*) FROM files)                             AS total_files,
    (SELECT COUNT(DISTINCT file_id) FROM audit_coverage)     AS files_with_coverage,
    (SELECT COUNT(*) FROM files) -
        (SELECT COUNT(DISTINCT file_id) FROM audit_coverage) AS files_without_coverage,
    (SELECT COUNT(*) FROM files f
     WHERE (SELECT COUNT(*) FROM audit_coverage ac
            WHERE ac.file_id = f.id) < 5)                    AS files_needing_audit,
    (SELECT COUNT(*) FROM audit_coverage)                    AS total_coverage_records,
    (SELECT ROUND(COUNT(*) * 100.0 /
        NULLIF((SELECT COUNT(*) FROM files), 0) / 5.0, 1)
     FROM audit_coverage)                                    AS coverage_pct,
    (SELECT COUNT(*) FROM findings WHERE status = 'open')    AS open_findings,
    (SELECT COUNT(*) FROM findings WHERE status = 'fixed')   AS fixed_findings,
    (SELECT COUNT(*) FROM findings WHERE status = 'wontfix') AS wontfix_findings,
    (SELECT COUNT(*) FROM findings
     WHERE status = 'open' AND severity = 'critical')        AS open_critical,
    (SELECT COUNT(*) FROM findings
     WHERE status = 'open' AND severity = 'high')            AS open_high,
    (SELECT COUNT(*) FROM findings
     WHERE status = 'open' AND severity = 'medium')          AS open_medium,
    (SELECT COUNT(*) FROM findings
     WHERE status = 'open' AND severity = 'low')             AS open_low,
    (SELECT COUNT(*) FROM runs)                              AS total_runs,
    (SELECT COUNT(*) FROM auto_fixes)                        AS total_auto_fixes;

COMMIT;
SQL
        ;;
esac

# --- Step 7: normalize legacy 'whole repo' sweep scope labels ---------------
# arch.sh historically labelled whole-repo architecture sweeps with the
# two-word scope 'whole repo'; it now emits 'whole-repo'. Fold the legacy
# rows into the canonical label. The UPDATE is naturally idempotent (the
# WHERE clause matches nothing after the first run).
legacy_scopes=$(sqlite3 "$DB" "SELECT COUNT(*) FROM sweeps WHERE scope='whole repo';")
if [ "$legacy_scopes" = "0" ]; then
    echo "  [=] no legacy 'whole repo' sweep scopes"
else
    echo "  [+] normalizing $legacy_scopes 'whole repo' sweep scope(s) to 'whole-repo'"
    sqlite3 "$DB" "PRAGMA foreign_keys=ON; UPDATE sweeps SET scope='whole-repo' WHERE scope='whole repo';"
fi

echo "Migration complete."
