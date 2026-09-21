#!/bin/sh
# deck-statusline.sh — Claude Code's status line command, used here as a data tap.
#
# Claude Code hands its status line a JSON payload that carries things no hook does: your plan usage
# (five-hour and weekly, as percentages with reset times), and - per session - the exact model,
# effort level and context-window fill. This writes them where the keypad plugin can read them:
#
#   ~/.claude/deck/usage.json                  plan usage, shared by every session
#   ~/.claude/deck/status/<session_id>.json    that session's model / effort / context
#
# and then prints a status line. If you already had a status line, the installer keeps its command
# in ~/.claude/deck/statusline-original.cmd and this runs it on the same input and prints ITS output,
# so yours looks exactly as it did.
#
# Reads no credentials and makes no network calls: everything comes from Claude Code's own payload.

umask 077
ROOT="${CLAUDE_DECK_ROOT:-$HOME/.claude/deck}"
PAYLOAD="$(cat 2>/dev/null)"
[ -n "$PAYLOAD" ] || exit 0

JQ=/usr/bin/jq
[ -x "$JQ" ] || JQ="$(command -v jq 2>/dev/null)"

if [ -n "$JQ" ] && mkdir -p "$ROOT/status" 2>/dev/null; then
  # One jq run, three lines out: shared usage ("null" when this session has none to report, so a
  # session on an API key cannot blank what a subscription session wrote), this session's status,
  # and the text for the status line.
  OUT="$(printf '%s' "$PAYLOAD" | "$JQ" -r '
    (now | floor) as $now
    | (.rate_limits // null) as $rl
    | (if $rl != null and (($rl.five_hour // null) != null or ($rl.seven_day // null) != null)
       then {ts: $now, plan: (.subscription_type // null),
             five_hour: ($rl.five_hour // null), seven_day: ($rl.seven_day // null),
             model_scoped: ($rl.model_scoped // [])}
       else null end) as $usage
    | {ts: $now,
       session_id: (.session_id // ""),
       model_id: (.model.id // ""),
       model_name: (.model.display_name // ""),
       effort: (.effort.level // null),
       ctx_pct: (.context_window.used_percentage // null),
       ctx_size: (.context_window.context_window_size // null),
       ctx_tokens: (.context_window.total_input_tokens // null)} as $status
    | ([ (.model.display_name // empty),
         (if (.context_window.used_percentage // null) != null then "ctx \(.context_window.used_percentage | floor)%" else empty end),
         (if ($rl.five_hour.used_percentage // null) != null then "5h \($rl.five_hour.used_percentage | floor)%" else empty end),
         (if ($rl.seven_day.used_percentage // null) != null then "wk \($rl.seven_day.used_percentage | floor)%" else empty end)
       ] | join(" · ")) as $text
    | ($usage | tojson), ($status | tojson), $text, (.transcript_path // "")' 2>/dev/null)"

  USAGE="$(printf '%s\n' "$OUT" | sed -n 1p)"
  STATUS="$(printf '%s\n' "$OUT" | sed -n 2p)"
  TEXT="$(printf '%s\n' "$OUT" | sed -n 3p)"
  TRANSCRIPT="$(printf '%s\n' "$OUT" | sed -n 4p)"

  # The numbers a session reports are the ones on ITS last API response, so a session that has sat
  # idle since Tuesday reports Tuesday's. Every session redraws now and then, and the last writer
  # would win - so a report only counts if its session has been active more recently than the one
  # already on file. The transcript's modification time is when that session last heard from the API.
  if [ -n "$USAGE" ] && [ "$USAGE" != "null" ]; then
    ACTIVITY="$(stat -f %m "$TRANSCRIPT" 2>/dev/null || echo 0)"
    KNOWN="$(sed -n 's/.*"activity":\([0-9]*\).*/\1/p' "$ROOT/usage.json" 2>/dev/null)"
    if [ "${ACTIVITY:-0}" -ge "${KNOWN:-0}" ]; then
      printf '%s\n' "$USAGE" | sed "s/^{/{\"activity\":${ACTIVITY:-0},/" > "$ROOT/usage.json.tmp.$$" \
        && mv -f "$ROOT/usage.json.tmp.$$" "$ROOT/usage.json"
    fi
  fi

  SID="$(printf '%s' "$STATUS" | "$JQ" -r '.session_id // empty' 2>/dev/null)"
  case "$SID" in
    *[!0-9A-Za-z-]* | "") ;;
    *) printf '%s\n' "$STATUS" > "$ROOT/status/$SID.json.tmp.$$" && mv -f "$ROOT/status/$SID.json.tmp.$$" "$ROOT/status/$SID.json" ;;
  esac
fi

# Your own status line, if you had one, stays exactly what it was.
if [ -s "$ROOT/statusline-original.cmd" ]; then
  printf '%s' "$PAYLOAD" | sh -c "$(cat "$ROOT/statusline-original.cmd")"
else
  printf '%s' "${TEXT:-}"
fi
exit 0
