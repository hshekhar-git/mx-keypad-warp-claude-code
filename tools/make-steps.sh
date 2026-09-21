#!/bin/bash
# make-steps.sh [scene ...] — regenerates the pictures in docs/steps/ that the README uses.
# With scene names (install place main calm list page answer question step apps) only those are
# re-taken, and the tiles are left as they are.
#
#   1. tools/steps.fsx draws every tile with the plugin's own renderer  -> docs/steps/tiles/
#   2. docs/steps/steps.html lays them out on a keypad, one scene per step
#   3. headless Chrome photographs each scene                           -> docs/steps/NN-<scene>.png
#
# Chrome runs with a throwaway profile, so it does not touch your own browser.

set -eu
HERE="$(cd "$(dirname "$0")/.." && pwd)"
source "$HERE/env.sh"

CHROME="/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"
[ -x "$CHROME" ] || { echo "Google Chrome is needed to take the pictures" >&2; exit 1; }

if [ $# -eq 0 ]; then
  dotnet build "$HERE/plugin/src/ClaudeDeckPlugin.csproj" -p:NoPluginLink=true -nologo -v q >/dev/null
  DYLD_LIBRARY_PATH=/Applications/Utilities/LogiPluginService.app/Contents/MonoBundle \
    dotnet fsi --quiet "$HERE/tools/steps.fsx"
  rm -f "$HERE"/docs/steps/[0-9][0-9]-*.png
fi

# One Chrome per picture, each with a throwaway profile and a time limit. The picture is written
# within a few seconds, but headless Chrome often stays running afterwards rather than exiting -
# and reliably so when given a profile an earlier run has used - so it is stopped once time is up.
shoot() {  # shoot <scene> <out.png>
  local profile; profile="$(mktemp -d)"
  # (in a subshell, so the shell's "Alarm clock" notice for the stopped Chrome goes nowhere)
  ( perl -e 'alarm 15; exec @ARGV' "$CHROME" --headless=new --disable-gpu --no-first-run \
    --no-default-browser-check --disable-extensions --user-data-dir="$profile" --hide-scrollbars \
    --force-device-scale-factor=2 --window-size=1040,585 --virtual-time-budget=2500 \
    --screenshot="$2" "file://$HERE/docs/steps/steps.html#$1" >/dev/null 2>&1 ) 2>/dev/null || true
  rm -rf "$profile"
  [ -s "$2" ]
}

N=0
for SCENE in install place main calm list page answer question step apps; do
  N=$((N + 1))
  if [ $# -gt 0 ] && ! printf ' %s ' "$@" | grep -q " $SCENE "; then continue; fi
  OUT="$HERE/docs/steps/$(printf '%02d' "$N")-$SCENE.png"
  if shoot "$SCENE" "$OUT" 2>/dev/null || shoot "$SCENE" "$OUT" 2>/dev/null; then echo "  $(basename "$OUT")"
  else echo "  FAILED: $SCENE" >&2; exit 1; fi
done
