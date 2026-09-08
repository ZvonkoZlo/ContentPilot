#!/bin/sh
# Who owns what, right now.

REPO=$(git rev-parse --show-toplevel)
ME=$(git config --get contentpilot.agent || echo "<unset>")

echo "You are: $ME"
echo ""

for claim in "$REPO"/.claims/*.md; do
    name=$(basename "$claim" .md)
    case "$name" in README|EXAMPLE) continue ;; esac

    status=$(grep -i '^status:' "$claim" | head -1 | sed 's/^[Ss]tatus:[[:space:]]*//')
    phase=$(grep -i '^phase:' "$claim" | head -1 | sed 's/^[Pp]hase:[[:space:]]*//')
    branch=$(grep -i '^branch:' "$claim" | head -1 | sed 's/^[Bb]ranch:[[:space:]]*//')
    migrations=$(grep -i '^migrations:' "$claim" | head -1 | sed 's/^[Mm]igrations:[[:space:]]*//')

    marker=""
    [ "$name" = "$ME" ] && marker="  <- you"
    [ "$migrations" = "true" ] && marker="$marker  [owns migrations]"

    echo "$name — phase $phase, branch $branch, $status$marker"
    sed -n '/^## paths/,/^## /p' "$claim" | grep -v '^## ' | grep -v '^[[:space:]]*$' | sed 's/^/    /'
    echo ""
done
