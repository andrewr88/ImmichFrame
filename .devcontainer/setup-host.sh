#!/bin/sh
# Runs on the HOST before the container starts (via initializeCommand).
# Pre-creates the bind-mount sources and installs the VS Code/Cursor Shift+Enter
# keybinding for Claude Code. No dependencies beyond sh, mkdir, grep, and sed.

set -e

# Ensure docker-compose override exists
test -f .devcontainer/docker-compose.override.yml || echo 'services: {}' > .devcontainer/docker-compose.override.yml

# Pre-create host paths for bind mounts. Docker creates a missing bind source itself, as a
# root-owned *directory* - which for the rtk/gh mounts means an unwritable directory in the
# container, and for ~/.gitconfig means a directory where the host's own git expects a file.
mkdir -p "$HOME/.config/rtk" "$HOME/.local/share/rtk" "$HOME/.config/gh" "$HOME/.ssh"
[ -e "$HOME/.gitconfig" ] || touch "$HOME/.gitconfig"

# --- Shift+Enter keybinding for VS Code/Cursor terminal ---
# Detect keybindings path — try VS Code first, then Cursor
case "$(uname -s)" in
    Darwin)
        CODE_DIR="$HOME/Library/Application Support/Code/User"
        CURSOR_DIR="$HOME/Library/Application Support/Cursor/User"
        ;;
    *)
        CODE_DIR="$HOME/.config/Code/User"
        CURSOR_DIR="$HOME/.config/Cursor/User"
        ;;
esac

install_keybinding() {
    KB_DIR="$1"
    KB_FILE="$KB_DIR/keybindings.json"

    # Already has the keybinding — skip
    if [ -f "$KB_FILE" ] && grep -q "workbench.action.terminal.sendSequence" "$KB_FILE" 2>/dev/null; then
        echo "[setup-host] Shift+Enter keybinding already in $KB_FILE"
        return 0
    fi

    mkdir -p "$KB_DIR"

    if [ -f "$KB_FILE" ] && [ -s "$KB_FILE" ]; then
        # File exists with content — append before closing ]
        cp "$KB_FILE" "$KB_FILE.bak"
        if grep -q '[^[:space:]\[\]]' "$KB_FILE" 2>/dev/null; then
            # Has existing entries — add comma
            sed -i.tmp 's/]$/,\
  {"key":"shift+enter","command":"workbench.action.terminal.sendSequence","args":{"text":"\\u001b\\r"},"when":"terminalFocus"}\
]/' "$KB_FILE"
        else
            # Empty array
            printf '[\n  {"key":"shift+enter","command":"workbench.action.terminal.sendSequence","args":{"text":"\\u001b\\r"},"when":"terminalFocus"}\n]\n' > "$KB_FILE"
        fi
        rm -f "$KB_FILE.tmp"
    else
        # No file — create fresh
        printf '[\n  {"key":"shift+enter","command":"workbench.action.terminal.sendSequence","args":{"text":"\\u001b\\r"},"when":"terminalFocus"}\n]\n' > "$KB_FILE"
    fi

    echo "[setup-host] Installed Shift+Enter keybinding in $KB_FILE"
}

# Install for whichever editor directories exist (or create for Code by default)
INSTALLED=0
if [ -d "$CODE_DIR" ] || [ -d "$(dirname "$CODE_DIR")/Code" ]; then
    install_keybinding "$CODE_DIR"
    INSTALLED=1
fi
if [ -d "$CURSOR_DIR" ] || [ -d "$(dirname "$CURSOR_DIR")/Cursor" ]; then
    install_keybinding "$CURSOR_DIR"
    INSTALLED=1
fi
# Default to VS Code if neither directory existed
if [ "$INSTALLED" = "0" ]; then
    install_keybinding "$CODE_DIR"
fi
