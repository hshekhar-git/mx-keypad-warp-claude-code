#!/bin/sh
# install-hooks.sh [--uninstall] — wires deck-hook.sh into ~/.claude/settings.json.
#
# Additive and idempotent: every entry it writes runs deck-hook.sh, and it removes its own previous
# entries first, so re-running never stacks duplicates and other tools' hooks are left untouched.
# settings.json as it was BEFORE the first install is kept as settings.json.claudedeck.bak; re-running
# does not overwrite that with a copy that already has these hooks in it.

set -eu

SETTINGS="${CLAUDE_SETTINGS:-$HOME/.claude/settings.json}"
ROOT="${CLAUDE_DECK_ROOT:-$HOME/.claude/deck}"
HERE="$(cd "$(dirname "$0")" && pwd)"
HOOK="$ROOT/deck-hook.sh"
TAG="deck-hook.sh"

command -v jq >/dev/null 2>&1 || { echo "jq is required (macOS ships it at /usr/bin/jq)" >&2; exit 1; }

mkdir -p "$(dirname "$SETTINGS")"
[ -f "$SETTINGS" ] || echo '{}' > "$SETTINGS"
jq -e 'type == "object"' "$SETTINGS" >/dev/null || { echo "$SETTINGS is not a JSON object; leaving it alone" >&2; exit 1; }

# Work from a scratch copy; the .bak is only (re)written when the file does not have our hooks yet,
# so it always holds the last version of settings.json that was free of them.
SRC="$SETTINGS.claudedeck.src"
cp "$SETTINGS" "$SRC"
trap 'rm -f "$SRC" "$SETTINGS.tmp"' EXIT
if ! grep -q "$TAG" "$SETTINGS"; then
  cp "$SETTINGS" "$SETTINGS.claudedeck.bak"
fi

# Drop every hook that runs our script, then any matcher group and event left empty by that.
STRIP='
  if (.hooks | type) == "object" then
    .hooks |= (with_entries(
      .value |= (if type == "array"
        then map(if (.hooks | type) == "array"
                 then .hooks |= map(select(((.command // "") | contains($tag)) | not))
                 else . end)
             | map(select((.hooks | type) != "array" or (.hooks | length) > 0))
        else . end))
      | with_entries(select((.value | type) != "array" or (.value | length) > 0)))
  else . end'

if [ "${1:-}" = "--uninstall" ]; then
  jq --arg tag "$TAG" "$STRIP | if .hooks == {} then del(.hooks) else . end" "$SRC" > "$SETTINGS.tmp"
  mv "$SETTINGS.tmp" "$SETTINGS"
  echo "Removed the keypad hooks from $SETTINGS"
  exit 0
fi

mkdir -p "$ROOT/sessions"
chmod 700 "$ROOT"
cp "$HERE/deck-hook.sh" "$HOOK"
chmod 700 "$HOOK"
[ -f "$ROOT/config.json" ] || cp "$HERE/../config.example.json" "$ROOT/config.json"

# event:argument[:matcher]
EVENTS='SessionStart:start
UserPromptSubmit:prompt
PreToolUse:pre
PostToolUse:post
PostToolUseFailure:post
PermissionRequest:permission
Notification:notify:permission_prompt
PreCompact:compact
Stop:stop
StopFailure:stopfail
SessionEnd:end'

SPEC="$(printf '%s\n' "$EVENTS" | jq -R -s 'split("\n") | map(select(length > 0) | split(":") | {event: .[0], arg: .[1], matcher: (.[2] // "")})')"

jq --arg tag "$TAG" --arg hook "$HOOK" --argjson spec "$SPEC" "$STRIP"'
  | .hooks = ((.hooks // {}) as $h
      | reduce $spec[] as $s ($h;
          .[$s.event] = ((.[$s.event] // []) + [{
            matcher: $s.matcher,
            hooks: [{type: "command", command: ("\"" + $hook + "\" " + $s.arg), timeout: 5}]
          }])))' "$SRC" > "$SETTINGS.tmp"
mv "$SETTINGS.tmp" "$SETTINGS"

echo "Wired $(printf '%s\n' "$EVENTS" | wc -l | tr -d ' ') Claude Code events to $HOOK"
echo "Pre-install backup: $SETTINGS.claudedeck.bak"
echo "Sessions started from now on report themselves; running ones need a restart (or /hooks review)."
