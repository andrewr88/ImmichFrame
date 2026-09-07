#!/usr/bin/env bash
# Runs once, after the container is created. Safe to re-run by hand.
set -euo pipefail

cd "$(dirname "$0")/.."

# Claude writes its config, history and credentials under $HOME, which lives in the container
# image and is therefore thrown away on every rebuild. Symlinking it into a gitignored directory
# inside the workspace keeps it across rebuilds without bind-mounting anything from the host.
#
# Guarded to containers deliberately: the rm below would delete the real ~/.claude if this script
# were ever run on the host by hand, which the header above openly invites.
if [ -f /.dockerenv ]; then
    CLAUDE_DATA_DIR="$PWD/.claude-data"

    mkdir -p "$CLAUDE_DATA_DIR/.claude"
    [ -f "$CLAUDE_DATA_DIR/.claude.json" ] || echo '{}' > "$CLAUDE_DATA_DIR/.claude.json"

    rm -rf "$HOME/.claude" "$HOME/.claude.json"
    ln -sf "$CLAUDE_DATA_DIR/.claude" "$HOME/.claude"
    ln -sf "$CLAUDE_DATA_DIR/.claude.json" "$HOME/.claude.json"

    echo "==> Claude state persisted to $CLAUDE_DATA_DIR"
else
    echo "==> Skipping Claude state symlink (not in a container)"
fi

# The host's ~/.ssh is bind-mounted read-only (see docker-compose.override.yml), so ssh cannot
# append a host key on first use and every fresh `git fetch` over SSH would fail the host-key
# check. Pre-populate a writable known_hosts; GIT_SSH_COMMAND in devcontainer.json lists it
# first, so new keys land there, and the read-only ~/.ssh/known_hosts second, so anything
# already trusted on the host machine is trusted in here without re-scanning.
KNOWN_HOSTS="$HOME/.local/ssh/known_hosts"
if [ ! -s "$KNOWN_HOSTS" ]; then
    mkdir -p "$(dirname "$KNOWN_HOSTS")"
    if ssh-keyscan -t rsa,ecdsa,ed25519 github.com 2>/dev/null > "$KNOWN_HOSTS"; then
        chmod 600 "$KNOWN_HOSTS"
        echo "==> Wrote GitHub host keys to $KNOWN_HOSTS"
    else
        # No network at postCreate is not worth failing the whole setup over; the file just
        # stays empty and ssh falls back to prompting on first connect.
        rm -f "$KNOWN_HOSTS"
        echo "==> WARNING: ssh-keyscan failed; $KNOWN_HOSTS not seeded"
    fi
fi

# Route git's HTTPS auth through gh, using the credentials bind-mounted from the host at
# ~/.config/gh. Best-effort: this writes to ~/.gitconfig, which is mounted read-only, so it
# fails when the host has not already run it - in which case the host's own gh helper lines
# are what the mount carries in anyway.
gh auth setup-git 2>/dev/null || echo "==> Skipping gh auth setup-git (already configured on the host, or gh not logged in)"

# RTK, the token-optimising CLI proxy. Its config and data are bind-mounted from the host, so
# this only has to initialise a machine that has never run it.
if command -v rtk >/dev/null 2>&1; then
    echo "==> Initialising rtk"
    rtk init --global || true
fi

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

# TINES_TOKEN and TINES_URL arrive through remoteEnv in devcontainer.json, forwarded from the
# host shell. If they are unset - someone building this container without them - the install
# is skipped rather than failing the setup. Note the script runs under `set -euo pipefail`, so
# an install that does run and fails will abort postCreate; that is deliberate, but it means a
# rebuild with the server unreachable stops here.
if [[ -n "${TINES_TOKEN:-}" && -n "${TINES_URL:-}" ]]; then
    echo "==> Installing Tines agent from $TINES_URL"
    curl -sSL -H "Authorization: Bearer $TINES_TOKEN" "$TINES_URL/tines/install/" | TINES_AUTO=1 bash
else
    echo "==> Skipping Tines agent (TINES_TOKEN / TINES_URL not set)"
fi

# The Tines claude-tmux shim takes over `claude` on PATH, so tell it where the real binary is
# rather than let it search PATH and find itself. Resolved to the versioned executable, because
# ~/.local/bin/claude may be the shim by this point. No-op when Tines is not installed.
if [ -d "$HOME/.tines" ]; then
    CLAUDE_REAL="$(ls -1d "$HOME"/.local/share/claude/versions/* 2>/dev/null | sort -V | tail -1)"

    if [ -x "${CLAUDE_REAL:-}" ]; then
        printf '%s\n' "$CLAUDE_REAL" > "$HOME/.tines/claude-bin"
        echo "==> Pointed claude-tmux at $CLAUDE_REAL"
    else
        echo "==> WARNING: Claude Code binary not found; claude-tmux will not start"
    fi
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
