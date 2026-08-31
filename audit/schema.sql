-- Continuous Codebase Audit Tracking Database
-- Used by Claude Code agents via sqlite3 CLI
--
-- Initialize:  sqlite3 audit/audit.db < audit/schema.sql
-- Rebuild:     rm audit/audit.db && sqlite3 audit/audit.db < audit/schema.sql
--
-- IMPORTANT: Every sqlite3 CLI invocation that does writes involving
-- foreign keys MUST start with: PRAGMA foreign_keys=ON;

PRAGMA journal_mode=WAL;
PRAGMA foreign_keys=ON;

-----------------------------------------------------------------------
-- FILES: every source file tracked for audit
-----------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS files (
    id              INTEGER PRIMARY KEY,
    path            TEXT NOT NULL UNIQUE,
    line_count      INTEGER NOT NULL DEFAULT 0,
    last_commit     TEXT NOT NULL DEFAULT '',
    priority_score  REAL NOT NULL DEFAULT 50.0,
    first_seen_at   TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
    updated_at      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now'))
);

CREATE INDEX IF NOT EXISTS idx_files_priority ON files(priority_score DESC);

-----------------------------------------------------------------------
-- RUNS: each audit execution
-----------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS runs (
    id              INTEGER PRIMARY KEY,
    file_id         INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
    started_at      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
    finished_at     TEXT,
    duration_ms     INTEGER,
    categories      TEXT NOT NULL DEFAULT '',
    findings_count  INTEGER NOT NULL DEFAULT 0,
    autofixes_count INTEGER NOT NULL DEFAULT 0,
    trigger_type    TEXT NOT NULL DEFAULT 'manual' CHECK(trigger_type IN (
                        'scheduled', 'manual'
                    )),
    notes           TEXT NOT NULL DEFAULT ''
);

CREATE INDEX IF NOT EXISTS idx_runs_file ON runs(file_id);
CREATE INDEX IF NOT EXISTS idx_runs_started ON runs(started_at DESC);

-----------------------------------------------------------------------
-- AUDIT_COVERAGE: per-file, per-category tracking
-----------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS audit_coverage (
    id          INTEGER PRIMARY KEY,
    file_id     INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
    category    TEXT NOT NULL CHECK(category IN (
                    'security', 'bugs', 'tests', 'refactoring', 'enhancements'
                )),
    audited_at  TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
    commit_hash TEXT NOT NULL DEFAULT '',
    run_id      INTEGER REFERENCES runs(id) ON DELETE SET NULL,
    UNIQUE(file_id, category)
);

CREATE INDEX IF NOT EXISTS idx_coverage_file ON audit_coverage(file_id);
CREATE INDEX IF NOT EXISTS idx_coverage_category ON audit_coverage(category);

-----------------------------------------------------------------------
-- FINDINGS: issues discovered during audits
-----------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS findings (
    id              INTEGER PRIMARY KEY,
    file_id         INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
    run_id          INTEGER REFERENCES runs(id) ON DELETE SET NULL,
    sweep_id        INTEGER REFERENCES sweeps(id) ON DELETE SET NULL,
    line            INTEGER,
    category        TEXT NOT NULL CHECK(category IN (
                        'security', 'bugs', 'tests', 'refactoring', 'enhancements'
                    )),
    severity        TEXT NOT NULL CHECK(severity IN (
                        'critical', 'high', 'medium', 'low'
                    )),
    title           TEXT NOT NULL,
    description     TEXT NOT NULL DEFAULT '',
    auto_fixable    INTEGER NOT NULL DEFAULT 0,
    status          TEXT NOT NULL DEFAULT 'open' CHECK(status IN (
                        'open', 'fixed', 'wontfix'
                    )),
    source          TEXT NOT NULL DEFAULT 'llm' CHECK(source IN (
                        'llm', 'static-tool'
                    )),
    tool            TEXT NOT NULL DEFAULT '',
    found_at        TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
    resolved_at     TEXT,
    resolution_note TEXT NOT NULL DEFAULT ''
);

CREATE INDEX IF NOT EXISTS idx_findings_file ON findings(file_id);
CREATE INDEX IF NOT EXISTS idx_findings_status ON findings(status);
CREATE INDEX IF NOT EXISTS idx_findings_severity ON findings(severity);
CREATE INDEX IF NOT EXISTS idx_findings_category ON findings(category);
CREATE INDEX IF NOT EXISTS idx_findings_open ON findings(status, severity)
    WHERE status = 'open';

-- Dedup index: prevent duplicate static-tool findings for the same
-- (file, line, tool, title) when there's still an open or wontfix row.
-- Allows INSERT OR IGNORE to be idempotent across repeated analyzer
-- runs. wontfix is included so a triaged false-positive does NOT get
-- re-seeded as open on every subsequent scan (the rationale lives in
-- the resolution_note of the existing wontfix row).
-- Fixed rows DO free the slot so a regression in fixed code can be
-- re-inserted as a fresh open finding.
CREATE UNIQUE INDEX IF NOT EXISTS idx_findings_dedup
    ON findings(file_id, IFNULL(line, -1), tool, title)
    WHERE status IN ('open', 'wontfix') AND source = 'static-tool';

-----------------------------------------------------------------------
-- AUTO_FIXES: records of findings automatically fixed by the agent
-----------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS auto_fixes (
    id          INTEGER PRIMARY KEY,
    finding_id  INTEGER NOT NULL REFERENCES findings(id) ON DELETE CASCADE,
    run_id      INTEGER REFERENCES runs(id) ON DELETE SET NULL,
    fix_summary TEXT NOT NULL DEFAULT '',
    diff_ref    TEXT NOT NULL DEFAULT '',
    applied_at  TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now'))
);

CREATE INDEX IF NOT EXISTS idx_auto_fixes_finding ON auto_fixes(finding_id);
CREATE INDEX IF NOT EXISTS idx_auto_fixes_run ON auto_fixes(run_id);

-----------------------------------------------------------------------
-- SWEEPS: package-level and cross-cutting audit passes
-----------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS sweeps (
    id              INTEGER PRIMARY KEY,
    scope           TEXT NOT NULL,                  -- package path, cross-cutting concern, or architecture scope
    sweep_type      TEXT NOT NULL DEFAULT 'package' CHECK(sweep_type IN (
                        'package', 'cross-cutting', 'architecture'
                    )),
    started_at      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
    finished_at     TEXT,
    findings_count  INTEGER NOT NULL DEFAULT 0,
    autofixes_count INTEGER NOT NULL DEFAULT 0,
    notes           TEXT NOT NULL DEFAULT ''
);

CREATE INDEX IF NOT EXISTS idx_sweeps_scope ON sweeps(scope);

-----------------------------------------------------------------------
-- META: simple key/value store for audit-level state
-----------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS meta (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL DEFAULT ''
);

-----------------------------------------------------------------------
-- VIEWS
-----------------------------------------------------------------------

-- Next audit target: files ranked by urgency
CREATE VIEW IF NOT EXISTS v_next_audit_target AS
SELECT
    f.id           AS file_id,
    f.path,
    f.line_count,
    f.priority_score,
    COUNT(ac.id)   AS categories_covered,
    5 - COUNT(ac.id) AS categories_remaining,
    COALESCE(MAX(ac.audited_at), '1970-01-01T00:00:00Z') AS last_audited,
    (5 - COUNT(ac.id)) * 20.0 + f.priority_score AS audit_urgency
FROM files f
LEFT JOIN audit_coverage ac ON ac.file_id = f.id
GROUP BY f.id
HAVING categories_remaining > 0
ORDER BY audit_urgency DESC, last_audited ASC
LIMIT 20;

-- Open findings by severity
CREATE VIEW IF NOT EXISTS v_open_findings AS
SELECT
    f.id          AS finding_id,
    fi.path       AS file_path,
    f.line,
    f.category,
    f.severity,
    f.title,
    f.description,
    f.auto_fixable,
    f.found_at
FROM findings f
JOIN files fi ON fi.id = f.file_id
WHERE f.status = 'open'
ORDER BY
    CASE f.severity
        WHEN 'critical' THEN 0
        WHEN 'high'     THEN 1
        WHEN 'medium'   THEN 2
        WHEN 'low'      THEN 3
    END,
    f.found_at DESC;

-- Coverage summary per file
CREATE VIEW IF NOT EXISTS v_coverage_summary AS
SELECT
    f.id         AS file_id,
    f.path,
    f.line_count,
    f.priority_score,
    COUNT(ac.id)                    AS categories_covered,
    5 - COUNT(ac.id)                AS categories_remaining,
    GROUP_CONCAT(ac.category, ', ') AS covered_categories,
    MIN(ac.audited_at)              AS oldest_audit,
    MAX(ac.audited_at)              AS newest_audit
FROM files f
LEFT JOIN audit_coverage ac ON ac.file_id = f.id
GROUP BY f.id
ORDER BY categories_remaining DESC, f.priority_score DESC;

-- Run history with file paths
CREATE VIEW IF NOT EXISTS v_run_history AS
SELECT
    r.id            AS run_id,
    fi.path         AS file_path,
    r.started_at,
    r.finished_at,
    r.duration_ms,
    r.categories,
    r.findings_count,
    r.autofixes_count,
    r.trigger_type,
    r.notes
FROM runs r
JOIN files fi ON fi.id = r.file_id
ORDER BY r.started_at DESC;

-- Dashboard: overall stats. files_needing_audit is the uncapped count of
-- files with < 5 categories covered (files_without_coverage only counts
-- zero-coverage files, and v_next_audit_target is LIMIT 20 so counting
-- it caps at 20).
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

-- Packages: file coverage progress per package
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

-- Next package sweep: fully covered packages that have never been swept,
-- or that re-qualify because enough of them changed since the last sweep.
-- changed_since_sweep counts distinct files in the package (direct
-- children only, matching v_packages semantics) with a completed run
-- after last_swept — re-audits only happen when a file changed, so
-- post-sweep runs are a faithful change proxy. Only files that already
-- existed at sweep time count (first_seen_at <= last_swept): files first
-- tracked after the sweep are additions, not changes — without the guard
-- a corpus expansion (e.g. newly-tracked test files) re-qualifies
-- packages that haven't changed. Threshold: >= 30% of the
-- package's files, at least 3, but never more than the package size.
-- A deep sweep must re-stamp coverage for files it modified (per the
-- sweep skill) so its own session's runs don't re-qualify the package.
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

-- Sweep history
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

-- Stale coverage: files changed since last audit
CREATE VIEW IF NOT EXISTS v_stale_coverage AS
SELECT
    f.path,
    f.last_commit,
    ac.category,
    ac.commit_hash AS audited_at_commit,
    ac.audited_at
FROM files f
JOIN audit_coverage ac ON ac.file_id = f.id
WHERE f.last_commit != ac.commit_hash
ORDER BY f.priority_score DESC;
