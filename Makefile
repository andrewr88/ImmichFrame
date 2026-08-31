.PHONY: docs
.PHONY: immichFrame.Web

dev:
	dotnet run --project ./ImmichFrame.WebApi

test-webapi:
	dotnet test ./ImmichFrame.WebApi.Tests/ImmichFrame.WebApi.Tests.csproj

test-core:
	dotnet test ./ImmichFrame.Core.Tests/ImmichFrame.Core.Tests.csproj

docs:
	npm --prefix docs run start

api:
	curl http://localhost:5217/swagger/v1/swagger.json -o ./openApi/swagger.json
	npm --prefix immichFrame.Web run api


docker-build-prod:
	docker buildx build --platform linux/amd64 --no-cache . --target final -t ghcr.io/immichframe/immichframe:latest --build-arg VERSION=1.0.0.0
	
docker-prod:
	docker compose -f ./docker/docker-compose.yml up --build -V --remove-orphans

docker-prune:
	docker system prune -a

## Codebase audit (audit/README.md). AUDIT_ADAPTER selects the language adapter;
## audit/adapters/fullstack.sh is this repo's, covering the C# projects.
## Override per invocation: make audit AUDIT_ADAPTER=go
AUDIT_ADAPTER ?= fullstack
export AUDIT_ADAPTER

.PHONY: audit audit-init audit-scan audit-sweep audit-next audit-findings audit-arch audit-tools

audit:
	audit/audit.sh status

audit-init:
	audit/audit.sh init
	audit/audit.sh sync

audit-scan:
	audit/audit.sh scan

audit-sweep:
	audit/audit.sh sweep

audit-next:
	audit/audit.sh next

audit-findings:
	audit/audit.sh findings

## Phase 3 is not wired up for this repo: audit/adapters/fullstack.sh implements
## no audit_arch_* members and audit/arch.sh calls audit_arch_preflight
## unguarded, so refuse with a clear message rather than dying in the shell.
audit-arch:
	@if [ "$(AUDIT_ADAPTER)" = "fullstack" ]; then \
	    echo "make audit-arch: unavailable with the fullstack adapter." >&2; \
	    echo "  audit/adapters/fullstack.sh does not implement the audit_arch_* contract" >&2; \
	    echo "  members (see audit/ADAPTERS.md), and audit/arch.sh calls audit_arch_preflight" >&2; \
	    echo "  unguarded. Phase 1 (audit-sweep) and Phase 2 (deep-sweep) are unaffected." >&2; \
	    exit 1; \
	fi; \
	audit/audit.sh arch-sweep $(PKG)

## Analyzers the scan drivers shell out to. dotnet ships with the SDK and
## ~/.dotnet/tools is already on PATH, so only roslynator needs installing —
## and roslynator.dotnet.cli carries no analyzer assemblies, so its own RCS
## rules never load until Roslynator.Analyzers is unpacked where the adapter's
## AUDIT_ROSLYNATOR_ANALYZERS points.
ROSLYNATOR_ANALYZERS_VERSION ?= 4.12.9
ROSLYNATOR_ANALYZERS_DIR ?= $(HOME)/.local/share/roslynator-analyzers

## -f on curl matters: without it a wrong version number gets a 404 whose XML
## error body is written to the .nupkg and curl still exits 0, so unzip fails
## with the stale file left behind. The rm -rf matters because unzip -o
## overwrites but never deletes: a release shipping a different roslyn* segment
## would leave the previous one in place, the adapter's probe would find it, and
## the scan would quietly run the old rules. Download to a temp name and only
## replace the tree once the bytes are in hand.
audit-tools:
	dotnet tool install -g roslynator.dotnet.cli
	mkdir -p $(dir $(ROSLYNATOR_ANALYZERS_DIR))
	curl -fsSL -o $(ROSLYNATOR_ANALYZERS_DIR).nupkg \
	    https://www.nuget.org/api/v2/package/Roslynator.Analyzers/$(ROSLYNATOR_ANALYZERS_VERSION)
	rm -rf $(ROSLYNATOR_ANALYZERS_DIR)
	mkdir -p $(ROSLYNATOR_ANALYZERS_DIR)
	unzip -oq $(ROSLYNATOR_ANALYZERS_DIR).nupkg -d $(ROSLYNATOR_ANALYZERS_DIR)
	rm -f $(ROSLYNATOR_ANALYZERS_DIR).nupkg
