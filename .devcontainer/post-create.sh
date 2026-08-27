#!/usr/bin/env bash
# Runs once, after the container is created. Safe to re-run by hand.
set -euo pipefail

cd "$(dirname "$0")/.."

echo "==> Restoring .NET dependencies"
# Also generates the Immich API client, which the Core project builds from an OpenAPI spec.
dotnet restore ImmichFrame.sln

echo "==> Installing frontend dependencies"
# Subshells rather than --prefix: immichFrame.Web has a 'prepare' hook (svelte-kit sync)
# that expects to run with the project as the working directory.
(cd immichFrame.Web && npm ci)

echo "==> Installing docs dependencies"
(cd docs && npm ci)

# The API reads docker/.env in Development (see Program.cs), so seed it from the example
# and let the developer fill in real values. Never overwrite an existing one.
if [ ! -f docker/.env ]; then
    echo "==> Seeding docker/.env from docker/example.env"
    cp docker/example.env docker/.env
    SEEDED_ENV=1
else
    SEEDED_ENV=0
fi

cat <<'EOF'

==> Ready.

    make dev                                 API on http://localhost:5217 (+ /swagger)
    npm --prefix immichFrame.Web run dev     web on http://localhost:5173, proxies /api to 5217
    make test-core / make test-webapi        tests
    make docs                                docs on http://localhost:3000

EOF

if [ "$SEEDED_ENV" = "1" ]; then
    cat <<'EOF'
    NOTE: docker/.env holds placeholder values (ImmichServerUrl=URL, ApiKey=KEY).
          The API refuses to start until they point at a real Immich server.

EOF
fi
