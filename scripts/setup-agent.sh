#!/bin/sh
# Records which agent this clone belongs to and installs the shared hooks.
# Run once per clone, before your first commit.

set -e

if [ -z "$1" ]; then
    echo "usage: ./scripts/setup-agent.sh <your-name>    # e.g. codex, claude"
    exit 1
fi

NAME=$1
REPO=$(git rev-parse --show-toplevel)

git config contentpilot.agent "$NAME"
git config core.hooksPath .githooks
chmod +x "$REPO"/.githooks/* 2>/dev/null || true

echo "Agent identity: $NAME"
echo "Hooks:          .githooks (versioned, shared)"

if [ -f "$REPO/.claims/$NAME.md" ]; then
    echo "Claim:          .claims/$NAME.md"
    grep -E '^(phase|status|migrations):' "$REPO/.claims/$NAME.md" | sed 's/^/                /'
else
    echo ""
    echo "No claim yet. Before writing code:"
    echo "  cp .claims/EXAMPLE.md .claims/$NAME.md"
    echo "  # edit it, then commit and push it"
fi

echo ""
echo "Also set, per shell, so two agents do not fight over containers:"
echo "  export COMPOSE_PROJECT_NAME=contentpilot-$NAME"
