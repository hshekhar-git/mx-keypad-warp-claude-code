#!/bin/bash
# install.sh            check prerequisites, build the plugin, link it into Logi Plugin Service,
#                       wire the Claude Code hooks, and verify that it all came up.
# install.sh --check    only report what is missing; change nothing.
# install.sh --uninstall  remove the hooks and the plugin link (your config and the repo stay).
#
# Safe to re-run: every step is idempotent.

set -u

HERE="$(cd "$(dirname "$0")" && pwd)"
SERVICE_APP="/Applications/Utilities/LogiPluginService.app"
SERVICE_DIR="$HOME/Library/Application Support/Logi/LogiPluginService"
LINK="$SERVICE_DIR/Plugins/ClaudeDeckPlugin.link"
LOG="$SERVICE_DIR/Logs/plugin_logs/ClaudeDeck.log"
PROJECT="$HERE/plugin/src/ClaudeDeckPlugin.csproj"

bold() { printf '\033[1m%s\033[0m\n' "$*"; }
ok()   { printf '  \033[32m✓\033[0m %s\n' "$*"; }
warn() { printf '  \033[33m!\033[0m %s\n' "$*"; }
bad()  { printf '  \033[31m✗\033[0m %s\n' "$*"; FAILED=1; }
FAILED=0

# Homebrew's dotnet lives outside the default location and needs pointing at.
# shellcheck disable=SC1091
source "$HERE/env.sh"

restart_service() {
  osascript -e 'quit app id "com.logi.pluginservice"' >/dev/null 2>&1
  for _ in 1 2 3 4 5 6 7 8 9 10; do pgrep -x LogiPluginService >/dev/null || break; sleep 1; done
  open "$SERVICE_APP"
}

# ---------------------------------------------------------------------------------------------
if [ "${1:-}" = "--uninstall" ]; then
  bold "Uninstalling"
  "$HERE/hooks/install-hooks.sh" --uninstall && ok "hooks removed from ~/.claude/settings.json"
  if [ -f "$LINK" ]; then
    rm -f "$LINK" && ok "plugin link removed"
    restart_service && ok "Logi Plugin Service restarted"
  fi
  warn "left in place: ~/.claude/deck (config, icons) - delete it yourself if you want it gone"
  exit 0
fi

# ---------------------------------------------------------------------------------------------
bold "1/4  Checking prerequisites"

[ "$(uname -s)" = "Darwin" ] && ok "macOS" || bad "macOS is required (focus and typing use macOS APIs)"

if [ -d "$SERVICE_APP" ]; then
  V="$(defaults read "$SERVICE_APP/Contents/Info.plist" CFBundleShortVersionString 2>/dev/null)"
  MAJOR="${V%%.*}"; REST="${V#*.}"; MINOR="${REST%%.*}"
  if [ "${MAJOR:-0}" -gt 6 ] || { [ "${MAJOR:-0}" -eq 6 ] && [ "${MINOR:-0}" -ge 4 ]; }; then
    ok "Logi Plugin Service $V"
  else
    bad "Logi Plugin Service $V is too old - update Logi Options+ (6.4 or newer is needed)"
  fi
else
  bad "Logi Plugin Service not found - install Logi Options+ from https://www.logitech.com/software/logi-options-plus.html"
fi

if command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks 2>/dev/null | grep -q '^1[0-9]\.'; then
  ok ".NET SDK $(dotnet --version 2>/dev/null)"
else
  bad ".NET SDK 10 not found - run: brew install dotnet"
fi

if xcrun --find swiftc >/dev/null 2>&1; then
  ok "Swift compiler (Xcode Command Line Tools)"
else
  bad "swiftc not found - run: xcode-select --install"
fi

command -v jq >/dev/null 2>&1 && ok "jq" || bad "jq not found - run: brew install jq"
command -v claude >/dev/null 2>&1 && ok "Claude Code" || warn "claude is not on PATH - install Claude Code before expecting any tiles"
[ -d "/Applications/Warp.app" ] && ok "Warp" || warn "Warp not found - sessions in other terminals still get tiles, but without exact-pane focus"

if [ "$FAILED" -ne 0 ]; then
  echo; bold "Fix the items marked ✗ and run this again."; exit 1
fi
[ "${1:-}" = "--check" ] && { echo; bold "All prerequisites met."; exit 0; }

# ---------------------------------------------------------------------------------------------
echo; bold "2/4  Building the plugin"

mkdir -p "$SERVICE_DIR/Plugins"

# Noted before the build, because the build itself asks the service to reload: what the log says
# about this install is everything written after this point.
LOG_BEFORE="$(wc -c < "$LOG" 2>/dev/null | tr -d ' ')"; LOG_BEFORE="${LOG_BEFORE:-0}"
HELPER="$HERE/plugin/bin/Release/bin/deck-apps watch"
BEFORE="$(pgrep -f "$HELPER" | sort | tr '\n' ' ')"
PREVIOUS="$(cat "$LINK" 2>/dev/null | tr -d '\r\n')"

if ! dotnet build "$PROJECT" -c Release -nologo -v q > "$HERE/plugin/build.log" 2>&1; then
  bad "build failed - see plugin/build.log"
  grep -E " error " "$HERE/plugin/build.log" | sort -u | head -5
  exit 1
fi
ok "built $(cd "$HERE" && ls plugin/bin/Release/bin | tr '\n' ' ')"
[ -x "$HERE/plugin/bin/Release/bin/deck-apps" ] || { bad "the app helper was not built"; exit 1; }
ok "linked into Logi Plugin Service"

# ---------------------------------------------------------------------------------------------
echo; bold "3/4  Wiring the Claude Code hooks"

"$HERE/hooks/install-hooks.sh" >/dev/null && ok "11 events wired in ~/.claude/settings.json (backup: settings.json.claudedeck.bak)" || { bad "hook install failed"; exit 1; }
if [ -s "$HOME/.claude/deck/statusline-original.cmd" ]; then
  ok "status line tapped for plan usage - your own status line still runs and is what you see"
else
  ok "status line set: it feeds plan usage to the keypad and shows model · ctx · 5h · wk in Claude Code"
fi
ok "config at ~/.claude/deck/config.json"

# ---------------------------------------------------------------------------------------------
echo; bold "4/4  Loading the plugin"

CURRENT="$(cat "$LINK" 2>/dev/null | tr -d '\r\n')"
if ! pgrep -x LogiPluginService >/dev/null; then
  open "$SERVICE_APP"; ok "started Logi Plugin Service"
elif [ -n "$PREVIOUS" ] && [ "$PREVIOUS" != "$CURRENT" ]; then
  # The service keeps a plugin loaded from wherever it first found it; after the folder has moved
  # only a restart makes it look again.
  restart_service; ok "restarted Logi Plugin Service (the plugin moved from $PREVIOUS)"
else
  open "loupedeck:plugin/ClaudeDeck/reload" 2>/dev/null; ok "asked the service to reload the plugin"
fi

# Two independent witnesses, because each can mislead on its own.
#   The service's log for this plugin says whether it ACCEPTED the build: "loaded from" on success,
#   "Cannot load plugin from '<…>.dll'" on refusal. (The similar "…because plugin is already loaded"
#   is harmless - the service scans its plugin folder twice.)
#   A helper process running from THIS folder, with a pid it did not have before, says the plugin's
#   Load() really ran here. It is not enough alone: the service runs Load() before it decides, so a
#   refused plugin starts a helper too.
new_log_lines() {
  SIZE="$(wc -c < "$LOG" 2>/dev/null | tr -d ' ')"
  if [ "${SIZE:-0}" -lt "${LOG_BEFORE:-0}" ]; then cat "$LOG" 2>/dev/null        # the log was rotated
  else tail -c "+$((LOG_BEFORE + 1))" "$LOG" 2>/dev/null; fi
}

VERDICT=unknown
for _ in $(seq 1 30); do
  LINES="$(new_log_lines)"
  if printf '%s' "$LINES" | grep -q "Cannot load plugin from .*\.dll'"; then VERDICT=refused; break; fi
  NOW="$(pgrep -f "$HELPER" | sort | tr '\n' ' ')"
  if printf '%s' "$LINES" | grep -q "loaded from" && [ -n "$NOW" ] && [ "$NOW" != "$BEFORE" ]; then VERDICT=loaded; break; fi
  sleep 1
done

case "$VERDICT" in
  loaded)
    ok "plugin is loaded and running" ;;
  refused)
    bad "Logi Plugin Service REFUSED the plugin. Its log says:"
    new_log_lines | grep -E "ERROR|WARN" | tail -4 | sed 's/^/      /'
    warn "after fixing the cause, restart the service - a refused plugin stays disabled until then:"
    warn "  quit and reopen $SERVICE_APP"
    exit 1 ;;
  *)
    warn "could not confirm the plugin loaded. Quit and reopen $SERVICE_APP, then check:"
    warn "  $LOG" ;;
esac

cat <<EOF

$(bold "Installed. Three things only you can do:")

  1. Put the keys on your keypad
     Logi Options+  ->  MX Creative Keypad  ->  find "MX Keypad Warp Claude Code" in the action list
     Drag onto keys:  Overview, Next, Session slot 1-4, Claude Sessions, Allow, App Switcher

  2. Allow typing (only needed for esc, /compact, yes / always / no ...)
     System Settings -> Privacy & Security -> Accessibility -> enable "Logi Plugin Service"

  3. Start a NEW Claude Code session in Warp ("claude"), send it a prompt, and watch its tile go
     coral then green. Sessions that were already running appear on their next tool call.

  MX Master 4 haptics:  Logi Options+ -> MX Master 4 -> Haptic feedback -> enable this plugin.
  Something off?        see "Troubleshooting" in README.md
EOF
