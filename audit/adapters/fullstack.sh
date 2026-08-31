#!/bin/sh
# Fullstack language adapter for the audit system (C# / .NET half).
#
# The audit core (audit/audit.sh, audit/scan.sh, audit/arch.sh) is
# language-agnostic. Every project-specific decision — which files to track, how
# to prioritise them, the build/test commands embedded in generated prompts, and
# the catalog of static analyzers — lives here. The core sources exactly one
# adapter, selected by AUDIT_ADAPTER, via `. audit/adapters/${AUDIT_ADAPTER}.sh`.
#
# ImmichFrame is two stacks in one repo (an ASP.NET Core 8 API plus a SvelteKit
# SPA), so this is deliberately ONE composite adapter rather than one per
# language: the core can only source a single file, and a `dotnet.sh` /
# `svelte.sh` split would force two disjoint databases over one codebase. Hence
# the name. This file currently covers the C# half only; the TypeScript/Svelte
# seams land in the same members later.
#
# See audit/ADAPTERS.md for the contract and audit/adapters/go.sh for the
# reference implementation this follows.
#
# Architecture members (audit_arch_*) are deliberately ABSENT, so `arch-sweep`
# is NOT available under this adapter. The core's placeholder substitution
# covers only the optional Section A/B/D/E bodies; audit/arch.sh:45 calls
# audit_arch_preflight unguarded, so running arch-sweep here dies with
# "audit_arch_preflight: not found". The audit-arch Make target refuses with a
# clear message instead of entering that path. Authoring them is a later task.
#
# POSIX sh only. The core is `#!/bin/sh` and SOURCES this file, so a bashism
# here breaks the core itself: no arrays, no `[[`, no `local`, no `${var,,}`.
#
# ---------------------------------------------------------------------------
# Adapter contract (functions/variables the core calls)
# ---------------------------------------------------------------------------
#
# Discovery & priority (used by audit/audit.sh cmd_sync):
#   audit_discover_sources      Repo-relative source paths, one per line.
#   audit_priority_score <path> Integer 0-100; higher = swept sooner.
#   audit_trivial_comment_regex ERE for comment-only lines (optional hook).
#
# Verify commands (used by audit/audit.sh prompt templates):
#   audit_verify_build_cmd   Print the build command string (no trailing NL).
#   audit_verify_test_cmd    Print the test command string (no trailing NL).
#
# Scan tool catalog (used by audit/scan.sh). The core owns the generic harness
# (file-id cache, sql_escape/rel_path/truncate_str/count_lines, apply_batch,
# batch header/footer, file sync, "all analyzers missing" check, summary
# rendering). The adapter owns the tools:
#   AUDIT_SCAN_TOOLS        Space-separated, ordered list of tool ids.
#   scan_<id>               One driver per id; sets the counter/state vars.
#   audit_scan_label <id>   Display name for the summary table.
#   audit_scan_state <id>   "ran" or "missing".
#   audit_scan_new/_dup/_skip <id>   Per-tool counters.
#
# The drivers rely on these being defined by the core before they run: DB,
# REPO_ROOT, TMP_DIR, FILES_CACHE, and the helper functions lookup_fid,
# sql_escape, rel_path, truncate_str, count_lines, apply_batch,
# write_batch_header, write_batch_footer.

# ---------------------------------------------------------------------------
# Discovery & priority
# ---------------------------------------------------------------------------

# The four C# project directories of ImmichFrame.sln. Named explicitly rather
# than discovered from the .sln so that discovery never sweeps in stray *.cs
# under docs/, misc/ or a scratch directory.
AUDIT_CS_PROJECTS="ImmichFrame.Core ImmichFrame.WebApi ImmichFrame.Core.Tests ImmichFrame.WebApi.Tests"

# audit_discover_sources: repo-relative list of files to track.
#
# Sourced from git's index (`git ls-files`), like go.sh, NOT from a bare find.
# Rooting a find at the project directories does not keep untracked files out —
# a C# scratch or work-in-progress file has to live inside a project to compile,
# so it lands squarely in the search path. Tracking those is actively harmful:
# they consume sweep-queue slots, and `git log -1` reports no history for them,
# so they enter the files table with last_commit='unknown' and their staleness
# path never fires (the core treats an empty/unknown hash as non-trivial and can
# never re-stamp coverage against it).
#
# The obj/ and bin/ exclusions stay path-based rather than leaning on
# .gitignore, so the intent survives a gitignore edit. Both hold generated
# compiler output (AssemblyInfo, GlobalUsings, the NSwag-generated ImmichApi) —
# machine-written, regenerated on every build, and meaningless to audit.
#
# Directory.Packages.props is tracked as a source file even though it is not
# C#: `dotnet list package --vulnerable` reports per *package*, but a finding
# row needs a file_id from a tracked path. This repo uses Central Package
# Management, so that one file holds every version and is where a bump is
# actually made — it is the correct owner for dependency CVEs. See
# scan_vulnerable below.
#
# The `[ -f ]` filter drops index entries whose working-tree copy is absent
# (an `rm` without `git rm`), which would otherwise abort cmd_sync's `wc -l`
# under set -e. git ls-files emits bare repo-relative paths, which is already
# the contract (the core strips a leading "./" defensively anyway).
audit_discover_sources() {
    for d in $AUDIT_CS_PROJECTS; do
        git ls-files -- "$d/*.cs"
    done \
    | grep -Ev '(^|/)(obj|bin)/' \
    | while IFS= read -r p; do [ -f "$p" ] && printf '%s\n' "$p"; done
    git ls-files -- 'Directory.Packages.props' \
    | while IFS= read -r p; do [ -f "$p" ] && printf '%s\n' "$p"; done
}

# audit_priority_score <path>: priority score for a repo-relative path.
#
# Path-prefix scoring only — no git-churn weighting, because the core already
# surfaces staleness separately (v_next_audit_target orders on coverage age).
# The *.Tests/* arm comes FIRST so that a test file never inherits the score of
# the production area it mirrors.
audit_priority_score() {
    priority=50
    case "$1" in
        # Audited, but last: no production blast radius.
        *.Tests/*)                              priority=25 ;;
        # Settings loading — holds the auth secret, Immich API keys, webhooks.
        ImmichFrame.WebApi/Helpers/Config/*)    priority=95 ;;
        # The composition root: DI, middleware order, auth wiring.
        ImmichFrame.WebApi/Program.cs)          priority=90 ;;
        # Auth middleware, SanitizeString, the profile registry.
        ImmichFrame.WebApi/Helpers/*)           priority=85 ;;
        # Request handling — client-supplied input lands here.
        ImmichFrame.WebApi/Controllers/*)       priority=80 ;;
        # Asset pools: the busiest and most intricate area of the codebase.
        ImmichFrame.Core/Logic/*)               priority=70 ;;
        # Outbound HTTP and credential handling.
        ImmichFrame.Core/Api/*)                 priority=65 ;;
        ImmichFrame.Core/Services/*)            priority=65 ;;
        # DTO boundary — the client-leak surface.
        ImmichFrame.WebApi/Models/*)            priority=55 ;;
        ImmichFrame.Core/Models/*)              priority=55 ;;
        # Everything else in Core, plus Directory.Packages.props, keeps 50.
    esac
    printf '%s' "$priority"
}

# audit_trivial_comment_regex: ERE for comment-only lines in C# sources.
#
# Matches line comments (//) at any indentation, but only when '//' is followed
# by whitespace or end-of-line. C# XML doc comments are '///' — a third slash,
# not whitespace — so they do NOT match, and a change to one correctly counts as
# non-trivial and forces a re-audit. That is deliberate: this codebase uses
# '/// <summary>' to record design rationale (ProfileRegistry.cs,
# ProfileServices.cs), so a doc-comment edit is a change of documented intent,
# not cosmetics. Same reasoning go.sh applies to '//go:build' directives.
# Block comments (/* */) fall to the conservative default (non-trivial).
audit_trivial_comment_regex() {
    printf '%s' '^[[:space:]]*//([[:space:]]|$)'
}

# ---------------------------------------------------------------------------
# Verify commands embedded in generated prompts
# ---------------------------------------------------------------------------

audit_verify_build_cmd() { printf '%s' 'dotnet build ImmichFrame.sln'; }
audit_verify_test_cmd()  { printf '%s' 'dotnet test ImmichFrame.sln'; }

# ---------------------------------------------------------------------------
# Scan tool catalog
# ---------------------------------------------------------------------------

# audit_cs_diagnostic_category <diagnostic-id>: map a Roslyn diagnostic id to a
# findings.category value. Shared by scan_build and scan_roslynator so the same
# rule cannot be filed as two different categories depending on which analyzer
# host happened to report it — both hosts emit the same id namespace.
#   NUnit*   the NUnit analyzers speak about test correctness
#   CA*/IDE* code-quality and style suggestions, not defects
#   RCS*     Roslynator's own refactoring rules
#   ASP*     ASP.NET Core usage suggestions ("Suggest using IHeaderDictionary
#            properties", "Use AddAuthorizationBuilder") — advice, not defects
#   SYSLIB*  source-generator/obsoletion advice ("Convert to
#            'GeneratedRegexAttribute'") — likewise advice
#   default  compiler CS* diagnostics: nullability holes and friends, the one
#            family here that really is a latent defect
audit_cs_diagnostic_category() {
    case "$1" in
        NUnit*)                       printf '%s' 'tests' ;;
        CA*|IDE*|RCS*|ASP*|SYSLIB*)   printf '%s' 'refactoring' ;;
        *)                            printf '%s' 'bugs' ;;
    esac
}

# Ordered tool ids. The core iterates this for running, the all-missing check,
# the new-findings tally, and the summary (preserving [1/3]..[3/3] order).
AUDIT_SCAN_TOOLS="build vulnerable roslynator"

# Per-tool counters/state. Held as shell variables; surfaced via the
# audit_scan_* accessors below and printed by the core's summary. A driver's
# parse loop runs in a subshell (it is the right-hand side of a pipe), so it
# CANNOT assign these directly — it appends a marker line to a temp file and the
# driver body counts those files after the loop, exactly as go.sh does.
BUILD_NEW=0;       BUILD_DUP=0;       BUILD_SKIP=0;       BUILD_STATE=missing
VULNERABLE_NEW=0;  VULNERABLE_DUP=0;  VULNERABLE_SKIP=0;  VULNERABLE_STATE=missing
ROSLYNATOR_NEW=0;  ROSLYNATOR_DUP=0;  ROSLYNATOR_SKIP=0;  ROSLYNATOR_STATE=missing

# Accessors mapping a tool id to its display name and counter/state vars. These
# keep the core's harness free of any tool-specific variable names.
audit_scan_label() {
    case "$1" in
        build)      printf '%s' 'dotnet build' ;;
        vulnerable) printf '%s' 'nuget-vuln' ;;
        roslynator) printf '%s' 'roslynator' ;;
    esac
}

audit_scan_state() {
    case "$1" in
        build)      printf '%s' "$BUILD_STATE" ;;
        vulnerable) printf '%s' "$VULNERABLE_STATE" ;;
        roslynator) printf '%s' "$ROSLYNATOR_STATE" ;;
    esac
}

audit_scan_new() {
    case "$1" in
        build)      printf '%s' "$BUILD_NEW" ;;
        vulnerable) printf '%s' "$VULNERABLE_NEW" ;;
        roslynator) printf '%s' "$ROSLYNATOR_NEW" ;;
    esac
}

audit_scan_dup() {
    case "$1" in
        build)      printf '%s' "$BUILD_DUP" ;;
        vulnerable) printf '%s' "$VULNERABLE_DUP" ;;
        roslynator) printf '%s' "$ROSLYNATOR_DUP" ;;
    esac
}

audit_scan_skip() {
    case "$1" in
        build)      printf '%s' "$BUILD_SKIP" ;;
        vulnerable) printf '%s' "$VULNERABLE_SKIP" ;;
        roslynator) printf '%s' "$ROSLYNATOR_SKIP" ;;
    esac
}

# ---- dotnet build ----------------------------------------------------
# The C# compiler and the Roslyn analyzers bundled with the SDK (CA*, CS*,
# NUnit*, ASP*) are themselves a static analyzer; the build is the cheapest way
# to run them.
scan_build() {
    echo "[1/3] dotnet build..."
    if ! command -v dotnet >/dev/null 2>&1; then
        echo "  [!] dotnet not installed — install the .NET 8 SDK"
        return
    fi
    BUILD_STATE=ran
    raw="$TMP_DIR/build.raw"
    out="$TMP_DIR/build.txt"
    # --no-incremental is load-bearing, not a tidy-up: MSBuild replays NO
    # diagnostics for a project it considers up to date, so a plain
    # `dotnet build` on an unchanged tree emits zero warnings. Without this the
    # driver would silently find nothing on every run after the first and the
    # zero would read as "dedup is working".
    # Exit is non-zero whenever the build has errors — those are exactly the
    # diagnostics we want, so tolerate it and let the parse decide.
    dotnet build ImmichFrame.sln --no-incremental \
        > "$raw" 2>"$TMP_DIR/build.err" || true

    # MSBuild prints every diagnostic twice — once inline as the project
    # compiles and once in the end-of-build summary — byte for byte. Deduping
    # here keeps the reported `duplicate` count honest instead of showing half
    # the findings as dupes of themselves.
    grep -E '\([0-9]+,[0-9]+\): (warning|error) ' "$raw" | sort -u > "$out" || true
    if [ ! -s "$out" ]; then
        return
    fi

    batch="$TMP_DIR/build.sql"
    write_batch_header "$batch"
    # Diagnostic lines look like:
    #   /abs/path/File.cs(12,5): warning CS8618: message [/abs/path/Proj.csproj]
    # Emit one "path|line|kind|code|message" record each. `message` is LAST so
    # that a '|' inside it lands in the final read variable untouched (POSIX
    # read assigns the unsplit remainder to the last name).
    awk '{
        if (match($0, /\([0-9]+,[0-9]+\): (warning|error) [A-Za-z][A-Za-z0-9]+: /) == 0) next
        file = substr($0, 1, RSTART - 1)
        tok  = substr($0, RSTART, RLENGTH)
        msg  = substr($0, RSTART + RLENGTH)
        sub(/ \[[^][]*\]$/, "", msg)          # drop the trailing [Project.csproj]
        ln = tok;   sub(/^\(/, "", ln);       sub(/,.*$/, "", ln)
        kind = tok; sub(/^\([0-9]+,[0-9]+\): /, "", kind); sub(/ .*$/, "", kind)
        code = tok; sub(/^\([0-9]+,[0-9]+\): (warning|error) /, "", code); sub(/:.*$/, "", code)
        print file "|" ln "|" kind "|" code "|" msg
    }' "$out" \
    | while IFS='|' read -r file line kind code msg; do
        [ -z "$file" ] && continue
        rel="$(rel_path "$file")"
        fid="$(lookup_fid "$rel")"
        if [ -z "$fid" ]; then
            echo "  [skip] untracked path: $rel" >&2
            echo "SKIP" >> "$TMP_DIR/build.skips"
            continue
        fi
        case "$kind" in
            error) severity=high   ;;
            *)     severity=medium ;;
        esac
        category="$(audit_cs_diagnostic_category "$code")"
        title="$(truncate_str 200 "[$code] $msg")"
        desc="$(truncate_str 2000 "$msg")"
        ft="$(sql_escape "$title")"
        fd="$(sql_escape "$desc")"
        printf "INSERT OR IGNORE INTO findings (file_id, line, tool, source, severity, category, title, description, status) VALUES (%s, %s, 'build', 'static-tool', '%s', '%s', '%s', '%s', 'open');\n" \
            "$fid" "$line" "$severity" "$category" "$ft" "$fd" >> "$batch"
        echo "INS" >> "$TMP_DIR/build.ins"
    done
    attempted="$(count_lines "$TMP_DIR/build.ins")"
    BUILD_SKIP="$(count_lines "$TMP_DIR/build.skips")"
    write_batch_footer "$batch"

    if [ "$attempted" -gt 0 ]; then
        result="$(apply_batch build "$batch" "$attempted")"
        BUILD_NEW="${result% *}"
        BUILD_DUP="${result#* }"
    fi
}

# ---- nuget vulnerability audit ---------------------------------------
# `dotnet list package --vulnerable` reports per package, not per source line.
# Every finding is attributed to Directory.Packages.props: with Central Package
# Management that file holds every pinned version and is where the bump is
# made. Transitive packages have no entry there, so they get line 0.
scan_vulnerable() {
    echo "[2/3] dotnet list package --vulnerable..."
    if ! command -v dotnet >/dev/null 2>&1; then
        echo "  [!] dotnet not installed — install the .NET 8 SDK"
        return
    fi
    VULNERABLE_STATE=ran
    raw="$TMP_DIR/vulnerable.raw"
    out="$TMP_DIR/vulnerable.json"
    # --format json is supported by the .NET 8 SDK (verified on 8.0.415).
    # Needs network access to query the NuGet advisory feed; when offline the
    # command still exits cleanly with an empty report, so no findings rather
    # than a failed scan — which is the graceful-degradation behaviour we want.
    dotnet list package --vulnerable --include-transitive --format json \
        > "$raw" 2>"$TMP_DIR/vulnerable.err" || true
    # The SDK can prefix restore/NU* notices before the JSON document; start
    # the payload at the line that opens the object.
    sed -n '/^{/,$p' "$raw" > "$out"
    if ! jq -e . "$out" >/dev/null 2>&1; then
        echo "  [!] no parsable JSON from 'dotnet list package'; see $TMP_DIR/vulnerable.raw" >&2
        return
    fi

    owner="Directory.Packages.props"
    fid="$(lookup_fid "$owner")"
    if [ -z "$fid" ]; then
        echo "  [skip] untracked path: $owner" >&2
        VULNERABLE_SKIP=1
        return
    fi

    # One "id|version|severity|advisoryurl" record per (package, advisory).
    # `unique` collapses the same package reported by several projects — with
    # one shared Directory.Packages.props they would all be the same finding.
    jq -r '[ .projects[]? | .frameworks[]?
             | (.topLevelPackages[]?, .transitivePackages[]?)
             | . as $p
             | ($p.vulnerabilities[]?
                | "\($p.id)|\($p.resolvedVersion)|\(.severity)|\(.advisoryurl)") ]
           | unique | .[]' "$out" > "$TMP_DIR/vulnerable.records" 2>/dev/null \
        || : > "$TMP_DIR/vulnerable.records"

    if [ ! -s "$TMP_DIR/vulnerable.records" ]; then
        return
    fi

    batch="$TMP_DIR/vulnerable.sql"
    write_batch_header "$batch"
    while IFS='|' read -r name version sev url; do
        [ -z "$name" ] && continue
        # Line of the pin in Directory.Packages.props, 0 when absent (which is
        # the normal case for a transitive dependency). -F because package ids
        # are dotted and would otherwise read as regex.
        line="$(grep -Fn "PackageVersion Include=\"$name\"" "$owner" 2>/dev/null | head -1 | cut -d: -f1)"
        [ -z "$line" ] && line=0
        case "$sev" in
            Critical) severity=critical ;;
            High)     severity=high     ;;
            Moderate) severity=medium   ;;
            Low)      severity=low      ;;
            *)        severity=medium   ;;
        esac
        # The advisory id is the last path segment of the URL (GHSA-xxxx-...).
        # Putting it in the title keeps two advisories against the same package
        # as two distinct findings under idx_findings_dedup(file_id, line, tool, title).
        adv="${url##*/}"
        [ -z "$adv" ] && adv="$sev"
        title="$(truncate_str 200 "[$adv] vulnerable package $name $version")"
        desc="$(truncate_str 2000 "$sev severity advisory against $name $version — bump the pin in $owner. $url")"
        ft="$(sql_escape "$title")"
        fd="$(sql_escape "$desc")"
        printf "INSERT OR IGNORE INTO findings (file_id, line, tool, source, severity, category, title, description, status) VALUES (%s, %s, 'vulnerable', 'static-tool', '%s', 'security', '%s', '%s', 'open');\n" \
            "$fid" "$line" "$severity" "$ft" "$fd" >> "$batch"
        echo "INS" >> "$TMP_DIR/vulnerable.ins"
    done < "$TMP_DIR/vulnerable.records"
    attempted="$(count_lines "$TMP_DIR/vulnerable.ins")"
    write_batch_footer "$batch"

    if [ "$attempted" -gt 0 ]; then
        result="$(apply_batch vulnerable "$batch" "$attempted")"
        VULNERABLE_NEW="${result% *}"
        VULNERABLE_DUP="${result#* }"
    fi
}

# ---- roslynator ------------------------------------------------------
# Roslynator re-hosts whatever analyzers the projects already reference (the
# SDK's CA*/CS*/ASP*/SYSLIB* plus NUnit.Analyzers) and reports them at info
# severity — a broader net than the build, which only surfaces what is
# configured as a warning.
#
# Roslynator's OWN rules (RCS*) are not part of that: they ship in the separate
# Roslynator.Analyzers NuGet package, and roslynator.dotnet.cli contains no
# analyzer assembly at all. Without -a the report is 100% other people's
# analyzers and not one RCS diagnostic appears. `make audit-tools` unpacks that
# package under the root below; the driver passes -a when a usable analyzer
# directory is found and says so plainly when it is not.
AUDIT_ROSLYNATOR_ANALYZERS_ROOT="${AUDIT_ROSLYNATOR_ANALYZERS_ROOT:-$HOME/.local/share/roslynator-analyzers/analyzers/dotnet}"

# audit_roslynator_analyzer_dir: print the analyzer directory to hand to -a, or
# nothing if none is usable.
#
# The package ships one tree per Roslyn generation (roslyn3.8, roslyn4.7, ...)
# and which segments exist changes between releases, so the segment is PROBED
# rather than hardcoded: pinning it would mean a version bump in the Makefile
# silently dropped every RCS finding while the scan still reported success.
# A tree with no *.dll (an interrupted or partial unpack) is skipped, so a
# half-written directory cannot pass the probe. Highest generation wins,
# compared on a zero-padded numeric key because a plain string compare would
# order roslyn4.12 below roslyn4.7.
audit_roslynator_analyzer_dir() {
    best=""
    best_key=""
    for d in "$AUDIT_ROSLYNATOR_ANALYZERS_ROOT"/roslyn*/cs; do
        [ -d "$d" ] || continue
        ls "$d"/*.dll >/dev/null 2>&1 || continue
        seg="${d%/cs}"
        seg="${seg##*/roslyn}"
        key="$(printf '%s' "$seg" | awk -F. '{ printf "%04d%04d", $1, $2 }')"
        if [ -z "$best_key" ] || [ "$key" -gt "$best_key" ]; then
            best="$d"
            best_key="$key"
        fi
    done
    printf '%s' "$best"
}

scan_roslynator() {
    echo "[3/3] roslynator..."
    if ! command -v roslynator >/dev/null 2>&1; then
        echo "  [!] roslynator not installed — run 'make audit-tools'"
        return
    fi
    xml="$TMP_DIR/roslynator.xml"
    analyzers="$(audit_roslynator_analyzer_dir)"
    # Reset rc rather than relying on "${rc:-0}": rc is a plain shell variable
    # in the sourced adapter, so a value left behind by an earlier driver would
    # otherwise leak into this check.
    rc=0
    # -o is required: without it roslynator only pretty-prints to the console.
    # Exit 1 means "diagnostics reported" — tolerate it; >1 is a real failure.
    # The two invocations are spelled out rather than built up in a variable so
    # that a path containing a space cannot be re-split by the expansion.
    if [ -n "$analyzers" ]; then
        roslynator analyze ImmichFrame.sln -o "$xml" -v q \
            -a "$analyzers" \
            >"$TMP_DIR/roslynator.out" 2>"$TMP_DIR/roslynator.err" || rc=$?
    else
        echo "  [!] RCS rules unavailable: no analyzer assemblies under $AUDIT_ROSLYNATOR_ANALYZERS_ROOT"
        echo "      (run 'make audit-tools'); continuing with the project's own analyzers."
        roslynator analyze ImmichFrame.sln -o "$xml" -v q \
            >"$TMP_DIR/roslynator.out" 2>"$TMP_DIR/roslynator.err" || rc=$?
    fi
    if [ "$rc" -gt 1 ]; then
        echo "  [!] roslynator failed (exit ${rc}); see $TMP_DIR/roslynator.err" >&2
        return
    fi
    # State is set only once the run is known to have succeeded — go.sh sets it
    # before the check, but -a gives that a concrete failure mode: a truncated
    # unpack leaves the directory present, the probe passes, roslynator exits
    # >1, and the summary would read "ran, 0 new" — indistinguishable from a
    # clean scan. Leaving it "missing" makes the bail-out visible.
    ROSLYNATOR_STATE=ran
    if [ ! -s "$xml" ]; then
        return
    fi

    batch="$TMP_DIR/roslynator.sql"
    write_batch_header "$batch"
    # The report is XML, and xmllint/python are not dependencies of this kit, so
    # parse with awk: accumulate the fields of one <Diagnostic> block and flush
    # on </Diagnostic>.
    #
    # The document opens with a <Summary> whose <Diagnostic> entries are ALSO
    # multi-line elements wrapping <Description>/<HelpLink> — what separates
    # them from real findings is that they carry no <FilePath>, so requiring one
    # at flush time is the guard. Every field is additionally reset on each
    # <Diagnostic Id= line, so a block missing its close tag cannot leak state
    # into the next one.
    #
    # <Message> may span several physical lines (any diagnostic whose text
    # contains a newline), so accumulate from <Message> until </Message> and
    # join with a single space, stripping the continuation lines' XML indent —
    # taking only the first line, as this did before, silently truncated them.
    # The text is XML-escaped; unesc() reverses the five predefined entities
    # (&amp; last, or it would double-decode "&amp;lt;"). `message` is emitted
    # LAST so a '|' inside it survives the read below intact.
    awk '
        function unesc(t) {
            gsub(/&lt;/,   "<",  t)
            gsub(/&gt;/,   ">",  t)
            gsub(/&quot;/, "\"", t)
            gsub(/&apos;/, "'"'"'", t)
            gsub(/&amp;/,  "\\&", t)
            return t
        }
        /<Diagnostic Id="/ {
            id = $0; sub(/^.*<Diagnostic Id="/, "", id); sub(/".*$/, "", id)
            sev = ""; msg = ""; fp = ""; ln = ""; inmsg = 0; next
        }
        inmsg == 1 {
            part = $0; sub(/^[[:space:]]+/, "", part)
            if (index(part, "</Message>") > 0) {
                sub(/<\/Message>.*$/, "", part)
                msg = unesc(msg " " part)
                inmsg = 0
            } else {
                msg = msg " " part
            }
            next
        }
        /<Severity>/ { sev = $0; sub(/^.*<Severity>/, "", sev); sub(/<\/Severity>.*$/, "", sev); next }
        /<FilePath>/ { fp  = $0; sub(/^.*<FilePath>/, "", fp);  sub(/<\/FilePath>.*$/, "", fp);  next }
        /<Location Line="/ { ln = $0; sub(/^.*<Location Line="/, "", ln); sub(/".*$/, "", ln); next }
        /<Message>/ {
            msg = $0; sub(/^.*<Message>/, "", msg)
            if (index(msg, "</Message>") > 0) {
                sub(/<\/Message>.*$/, "", msg)
                msg = unesc(msg)
            } else {
                inmsg = 1
            }
            next
        }
        /<\/Diagnostic>/ {
            if (fp != "" && ln != "") print fp "|" ln "|" sev "|" id "|" msg
            sev = ""; msg = ""; fp = ""; ln = ""; inmsg = 0; next
        }
    ' "$xml" \
    | while IFS='|' read -r file line sev id msg; do
        [ -z "$file" ] && continue
        # CS* are compiler diagnostics: scan_build already owns them, at the
        # same file and line, and MSBuild's wording is strictly better because
        # it names the offending symbol ("Non-nullable field '_pool' must ...")
        # where Roslynator re-emits only the generic rule title. Dropping them
        # here is what stops every build finding having a roslynator twin.
        # Not counted as a skip — nothing was missed, it is deliberate routing.
        case "$id" in
            CS*) continue ;;
        esac
        rel="$(rel_path "$file")"
        fid="$(lookup_fid "$rel")"
        if [ -z "$fid" ]; then
            echo "  [skip] untracked path: $rel" >&2
            echo "SKIP" >> "$TMP_DIR/roslynator.skips"
            continue
        fi
        case "$sev" in
            Error)   severity=high   ;;
            Warning) severity=medium ;;
            Info)    severity=low    ;;
            *)       severity=low    ;;
        esac
        title="$(truncate_str 200 "[$id] $msg")"
        desc="$(truncate_str 2000 "$msg")"
        ft="$(sql_escape "$title")"
        fd="$(sql_escape "$desc")"
        category="$(audit_cs_diagnostic_category "$id")"
        printf "INSERT OR IGNORE INTO findings (file_id, line, tool, source, severity, category, title, description, status) VALUES (%s, %s, 'roslynator', 'static-tool', '%s', '%s', '%s', '%s', 'open');\n" \
            "$fid" "$line" "$severity" "$category" "$ft" "$fd" >> "$batch"
        echo "INS" >> "$TMP_DIR/roslynator.ins"
    done
    attempted="$(count_lines "$TMP_DIR/roslynator.ins")"
    ROSLYNATOR_SKIP="$(count_lines "$TMP_DIR/roslynator.skips")"
    write_batch_footer "$batch"

    if [ "$attempted" -gt 0 ]; then
        result="$(apply_batch roslynator "$batch" "$attempted")"
        ROSLYNATOR_NEW="${result% *}"
        ROSLYNATOR_DUP="${result#* }"
    fi
}

# ---------------------------------------------------------------------------
# Architecture report — not implemented
# ---------------------------------------------------------------------------

# audit_arch_preflight: refuse arch-sweep cleanly.
#
# The core's placeholder substitution covers only the optional Section A/B/D/E
# bodies. audit/arch.sh:45 calls this function unguarded, so WITHOUT this stub
# every arch entry point dies with "audit_arch_preflight: not found" — and there
# are three that never touch the Makefile guard: `audit/audit.sh arch-sweep`
# (documented at audit/README.md:45), the command
# .claude/skills/sweep/phase-3-architecture.md tells an agent to run, and
# advance_to_arch_or_idle (audit.sh:772), which calls cmd_arch_sweep on its own
# once Phases 1 and 2 complete.
#
# Returning non-zero makes arch.sh's `|| exit 1` refuse with the message below.
# This stub is deleted when the real audit_arch_* members land.
audit_arch_preflight() {
    echo "arch-sweep: the fullstack adapter implements no audit_arch_* members" >&2
    echo "  (see audit/ADAPTERS.md, 'Architecture report'). Phase 1 (sweep) and" >&2
    echo "  Phase 2 (deep-sweep) are unaffected." >&2
    return 1
}
