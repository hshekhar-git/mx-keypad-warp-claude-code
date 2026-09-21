#!/bin/sh
# install-hooks.sh [--uninstall] — wires deck-hook.sh (hooks) and deck-statusline.sh (status line)
# into ~/.claude/settings.json.
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
SLINE="$ROOT/deck-statusline.sh"
SLTAG="deck-statusline.sh"

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
  # The status line goes back to what it was: the one we set aside, or none at all.
  if [ -s "$ROOT/statusline-original.json" ]; then
    RESTORE="$(cat "$ROOT/statusline-original.json")"
  else
    RESTORE=null
  fi
  jq --arg tag "$TAG" --arg sltag "$SLTAG" --argjson restore "$RESTORE" "$STRIP"'
    | if .hooks == {} then del(.hooks) else . end
    | if ((.statusLine.command // "") | contains($sltag))
      then (if $restore == null then del(.statusLine) else .statusLine = $restore end)
      else . end' "$SRC" > "$SETTINGS.tmp"
  mv "$SETTINGS.tmp" "$SETTINGS"
  rm -f "$ROOT/statusline-original.json" "$ROOT/statusline-original.cmd"
  echo "Removed the keypad hooks from $SETTINGS"
  exit 0
fi

mkdir -p "$ROOT/sessions"
chmod 700 "$ROOT"
cp "$HERE/deck-hook.sh" "$HOOK"
chmod 700 "$HOOK"
cp "$HERE/deck-statusline.sh" "$SLINE"
chmod 700 "$SLINE"

# A status line that is not ours is set aside, not replaced: ours runs it on the same input and
# prints its output, so it looks exactly as it did; --uninstall puts it back.
if jq -e --arg sltag "$SLTAG" '(.statusLine // null) != null and (((.statusLine.command // "") | contains($sltag)) | not)' "$SRC" >/dev/null; then
  jq -c '.statusLine' "$SRC" > "$ROOT/statusline-original.json"
  jq -r '.statusLine.command // ""' "$SRC" > "$ROOT/statusline-original.cmd"
  chmod 600 "$ROOT/statusline-original.json" "$ROOT/statusline-original.cmd"
fi
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

jq --arg tag "$TAG" --arg hook "$HOOK" --arg sline "$SLINE" --argjson spec "$SPEC" "$STRIP"'
  | .statusLine = ((.statusLine // {}) + {type: "command", command: ("\"" + $sline + "\"")})
  | .hooks = ((.hooks // {}) as $h
      | reduce $spec[] as $s ($h;
          .[$s.event] = ((.[$s.event] // []) + [{
            matcher: $s.matcher,
            hooks: [{type: "command", command: ("\"" + $hook + "\" " + $s.arg), timeout: 5}]
          }])))' "$SRC" > "$SETTINGS.tmp"
mv "$SETTINGS.tmp" "$SETTINGS"

echo "Wired $(printf '%s\n' "$EVENTS" | wc -l | tr -d ' ') Claude Code events to $HOOK"
echo "Status line -> $SLINE (plan usage, and each session's model / effort / context)"
[ -s "$ROOT/statusline-original.cmd" ] && echo "  your existing status line is kept: it still runs, and its output is what you see"
echo "Pre-install backup: $SETTINGS.claudedeck.bak"
echo "Sessions started from now on report themselves; running ones need a restart (or /hooks review)."
