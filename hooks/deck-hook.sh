#!/bin/sh
# deck-hook.sh <event> — records one Claude Code session's state for the ClaudeDeck keypad plugin.
#
#   SessionStart        -> start       idle (kept as-is when the start is only a compaction)
#   UserPromptSubmit    -> prompt      busy, remembers the prompt, restarts the turn clock
#   PreToolUse          -> pre         busy + which tool and on what; AskUserQuestion / ExitPlanMode
#                                      raise attention instead, because they block on you
#   PostToolUse(Failure)-> post        busy, tool cleared
#   PermissionRequest   -> permission  attention, with the tool and its input so a key can show it
#   Notification        -> notify      attention, only if something was actually running
#   PreCompact          -> compact     busy, tool "compact"
#   Stop                -> stop        done
#   StopFailure         -> stopfail    error
#   SessionEnd          -> end         removes the session's file
#
# One file per session: ~/.claude/deck/sessions/<key>.json, written atomically. The key is the Warp
# pane uuid when there is one (so `claude --resume` keeps its tile), else the Claude session id.
#
# Always exits 0 and never prints: a status display must not be able to break, slow down or answer
# for the session it is watching.

set -u
umask 077

ROOT="${CLAUDE_DECK_ROOT:-$HOME/.claude/deck}"
DIR="$ROOT/sessions"
EVENT="${1:-}"
[ -n "$EVENT" ] || exit 0

JQ=/usr/bin/jq
[ -x "$JQ" ] || JQ="$(command -v jq 2>/dev/null)" || exit 0
[ -n "$JQ" ] || exit 0

PAYLOAD="$(cat 2>/dev/null)"
[ -n "$PAYLOAD" ] || PAYLOAD='{}'

# --- identity ---------------------------------------------------------------------------------
WUUID="${WARP_TERMINAL_SESSION_UUID:-}"
case "$WUUID" in *[!0-9a-f]* | "") WUUID="" ;; esac
[ ${#WUUID} -eq 32 ] || WUUID=""

if [ -n "$WUUID" ]; then
  KEY="$WUUID"
  TERM_KIND="warp"
else
  SID="$(printf '%s' "$PAYLOAD" | "$JQ" -r '.session_id // empty' 2>/dev/null)"
  case "$SID" in *[!0-9A-Za-z-]* | "") exit 0 ;; esac
  KEY="c-$SID"
  TERM_KIND="other"
fi

mkdir -p "$DIR" 2>/dev/null || exit 0
[ -O "$ROOT" ] || exit 0

FILE="$DIR/$KEY.json"

if [ "$EVENT" = "end" ]; then
  rm -f "$FILE"
  exit 0
fi

# --- metadata, only when it can have changed --------------------------------------------------
# A git call and a walk up the process tree are too much for the hot path (every tool call), so
# they run on start, on each prompt, and whenever the file is missing - which is how a session
# that predates the hook heals itself.
PROJECT=""
BRANCH=""
PID=""
if [ "$EVENT" = "start" ] || [ "$EVENT" = "prompt" ] || [ ! -r "$FILE" ]; then
  CWD="$(printf '%s' "$PAYLOAD" | "$JQ" -r '.cwd // empty' 2>/dev/null)"
  [ -n "$CWD" ] || CWD="$PWD"
  TOP="$(git -C "$CWD" rev-parse --show-toplevel 2>/dev/null)"
  if [ -n "$TOP" ]; then
    PROJECT="$(basename "$TOP")"
    BRANCH="$(git -C "$CWD" rev-parse --abbrev-ref HEAD 2>/dev/null)"
    [ "$BRANCH" = "HEAD" ] && BRANCH="$(git -C "$CWD" rev-parse --short HEAD 2>/dev/null)"
  fi

  p=$$
  n=0
  while [ "$n" -lt 12 ]; do
    c="$(ps -o comm= -p "$p" 2>/dev/null | sed 's|.*/||')"
    if [ "$c" = "claude" ]; then PID="$p"; break; fi
    p="$(ps -o ppid= -p "$p" 2>/dev/null | tr -d ' ')"
    { [ -z "$p" ] || [ "$p" -le 1 ]; } && break
    n=$((n + 1))
  done
fi

OLD="$FILE"
[ -r "$OLD" ] || OLD=/dev/null

TMP="$FILE.tmp.$$"
printf '%s' "$PAYLOAD" | "$JQ" -c \
  --slurpfile old "$OLD" \
  --arg ev "$EVENT" \
  --arg key "$KEY" \
  --arg term "$TERM_KIND" \
  --arg warp "$WUUID" \
  --arg bundle "${__CFBundleIdentifier:-}" \
  --arg project "$PROJECT" \
  --arg branch "$BRANCH" \
  --arg pid "$PID" \
  --arg pwd "$PWD" \
  --argjson now "$(date +%s)" '
  def clean(n): tostring | gsub("[\\n\\r\\t ]+"; " ") | ltrimstr(" ") | .[0:n];

  . as $p
  | (($old[0] // {}) | if type == "object" then . else {} end) as $o
  | ($p.tool_name // "") as $tool
  | ($p.tool_input // {}) as $ti
  | ($p.tool_use_id // "") as $tid
  | (($ti.command // $ti.file_path // $ti.pattern // $ti.url // $ti.query
      // $ti.description // $ti.prompt // "") | clean(160)) as $detail
  | ($o.state // "idle") as $was
  | ($o.pending // "") as $pending
  | (($o.kind // "") == "permission" and $was == "attention" and $pending != "") as $held

  # What the session is now, and why.
  | (if   $ev == "start"      then (if ($p.source // "") == "compact" then $was else "idle" end)
     elif $ev == "prompt"     then "busy"
     elif $ev == "compact"    then "busy"
     elif $ev == "pre"        then
       (if $tool == "AskUserQuestion" or $tool == "ExitPlanMode" then "attention"
        # A parallel batch: another tool starting must not hide a prompt that is still open.
        elif $held and $tid != $pending and ($now - ($o.since // 0)) < 3 then "attention"
        else "busy" end)
     elif $ev == "post"       then (if $held and $tid != "" and $tid != $pending then "attention" else "busy" end)
     elif $ev == "permission" then "attention"
     elif $ev == "notify"     then (if $was == "busy" or $was == "attention" then "attention" else $was end)
     elif $ev == "stop"       then "done"
     elif $ev == "stopfail"   then "error"
     else $was end) as $state

  | (if $state != "attention" then ""
     elif $ev == "pre" and $tool == "AskUserQuestion" then "question"
     elif $ev == "pre" and $tool == "ExitPlanMode"    then "plan"
     elif $ev == "permission" then "permission"
     elif $was == "attention" then ($o.kind // "permission")
     else "permission" end) as $kind

  | (if $state == "attention" and $was == "attention" and $ev != "permission" then true else false end) as $keep

  | {
      schema: 2,
      key: $key,
      term: $term,
      warp_uuid: $warp,
      bundle: (if $bundle != "" then $bundle else ($o.bundle // "") end),
      session_id: ($p.session_id // $o.session_id // ""),
      pid: (if $pid != "" then ($pid | tonumber) else ($o.pid // 0) end),
      cwd: ($p.cwd // $o.cwd // $pwd),
      project: (if $project != "" then $project
                elif ($o.project // "") != "" then $o.project
                else (($p.cwd // $pwd) | split("/") | last) end),
      branch: (if $project != "" then $branch else ($o.branch // "") end),
      transcript: ($p.transcript_path // $o.transcript // ""),
      started: ($o.started // $now),
      turns: (($o.turns // 0) + (if $ev == "prompt" then 1 else 0 end)),
      prompt: (if $ev == "prompt" then (($p.prompt // "") | clean(140)) else ($o.prompt // "") end),
      state: $state,
      kind: $kind,
      since: (if $state == $was then ($o.since // $now) else $now end),
      turn_since: (if $ev == "prompt" then $now else ($o.turn_since // $now) end),
      ts: $now,
      tool: (if $keep then ($o.tool // "")
             elif $ev == "pre" or $ev == "permission" then $tool
             elif $ev == "compact" then "compact"
             elif $ev == "notify" then ($o.tool // "")
             else "" end),
      detail: (if $keep then ($o.detail // "")
               elif $ev == "pre" or $ev == "permission" then $detail
               elif $ev == "notify" then ($o.detail // "")
               else "" end),
      pending: (if $ev == "permission" then $tid
                elif $state == "attention" then $pending
                else "" end),
      message: (if $ev == "notify" then (($p.message // "") | clean(140)) else "" end),
      # A single multiple-choice question can be answered from the keypad, so its wording and the
      # option labels travel with the state. Anything more involved is left to the terminal.
      question: (if $keep then ($o.question // "")
                 elif $ev == "pre" and $tool == "AskUserQuestion"
                   then (($ti.questions // []) | if length > 0 then (.[0].question // "" | clean(120)) else "" end)
                 else "" end),
      options: (if $keep then ($o.options // [])
                elif $ev == "pre" and $tool == "AskUserQuestion"
                  then (($ti.questions // [])
                        | if length == 1 and ((.[0].multiSelect // false) | not)
                          then [(.[0].options // [])[] | (.label // "" | clean(28))] else [] end)
                else [] end)
    }' > "$TMP" 2>/dev/null

if [ -s "$TMP" ]; then
  mv -f "$TMP" "$FILE" 2>/dev/null || rm -f "$TMP" 2>/dev/null
else
  rm -f "$TMP" 2>/dev/null
fi

exit 0
