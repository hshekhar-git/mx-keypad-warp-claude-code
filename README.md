# Claude Deck

Every running [Claude Code](https://claude.com/claude-code) session as a live tile on a **Logitech MX
Creative Keypad** — jump to it, answer its permission prompts, interrupt it — and a buzz on an
**MX Master 4** when one needs you.

Inspired by [pffan91/claudewarp-keypad-mx](https://github.com/pffan91/claudewarp-keypad-mx) (MIT),
which worked out the hard parts: Warp's `warp://session/<uuid>` deep link, its SQLite tab layout, and
the 8-tiles-per-page rule. This is a separate implementation that goes further.

## What a tile tells you

```
▓▓▓▓▓▓░░░░░░░░  ← context window fill (yellow past 80%)
  web-app   ← project (git top level)
 Landing page   ← Claude's own session title, else your last prompt
 hero rework
   Bash 2:31    ← what it is doing right now, and for how long
▁▁▁▁███▁▁▁▁▁▁▁  ← sweep while busy
```

| Colour | State | From |
|---|---|---|
| coral | **busy** — shows the running tool and turn time | `UserPromptSubmit`, `PreToolUse`, `PostToolUse`, `PreCompact` |
| green | **done** — your turn, with how long ago | `Stop` |
| red, blinking | **attention** — blocked on you. A permission prompt shows *the command it wants to run*; `AskUserQuestion` shows "asks you"; `ExitPlanMode` shows "plan ready" | `PermissionRequest`, `Notification`, `PreToolUse` |
| purple | **error** — the turn died | `StopFailure` |
| grey | **idle** | `SessionStart` |

One keypad page per Warp tab; sessions in other terminals get a page per app. ◀ ▶ walk the pages.

## App Switcher

A Cmd-Tab you can see. Put **App Switcher** on one key; press it and every running app is a key with
its real icon. Press one and it comes to the front, and the folder closes itself - one gesture.

- **Pinned apps come first and never move** (Warp by default), because a key you have learned beats a
  perfectly sorted list. Everything else is most-recently-used, like Cmd-Tab.
- The order is **frozen while the folder is open**, so tiles do not shuffle under your finger.
- The app in front has a white bar; hidden apps are dimmed; **hold** a key to hide that app.
- A terminal hosting Claude sessions wears a **badge in the deck's colours** - red 2 means two sessions
  there are blocked on you - so the switcher tells you *why* to go to Warp, not just how.
- **Open App** is the one-key version: set it to "Warp" in its Options+ form and it is a direct
  switch-to-Warp key with the same icon and badge.

Driven by a small native helper (`helper/deck-apps.swift`) that pushes a line when an app launches,
quits, activates or hides - nothing polls - and exits by itself when the plugin goes away.

## Keys

**Inside the *Claude Sessions* folder**

| Key | Does |
|---|---|
| a tile | focuses that exact Warp pane (window, tab, split). Every press **flashes** the key, so a press that changed nothing on screen still says it worked |
| side bars on a tile | **you are here**: the pane you are actually typing in (read from Warp), which is also the one the other keys act on. With no terminal in front, the tile you last pressed |
| a tile, **held** | interrupts *that* session (focus + Escape), wherever it is |
| bottom row | your command keys from `config.json` |
| **while that session is on a permission prompt** | the last keys of its page become **yes / always / no** |
| **while it is asking a multiple-choice question** | the tile shows the question and the keys become the **actual option labels** ("2 Supabase Auth"). Uses the command row plus any blank tiles, and only appears if every option fits |

**On your home page** (drag from Options+ → *Claude Deck*)

| Key | Shows | Press |
|---|---|---|
| **Needs me** | count of sessions blocked on you — else errored, else finished, else working | cycles through exactly the sessions it counts, longest-waiting first |
| **Working** | how many are still running | cycles through them |
| **Allow** | the oldest open permission prompt *spelled out*: tool, command, project | **allows it**. **Hold** to go and look instead |
| **Commands → …** | each key from `config.json` | types it, only if a terminal is already in front |
| **Send to Claude** | a label you choose | text + Return configured in the Options+ form |

**Haptics (MX Master 4)** — three events, remappable in Options+: *Claude needs you* (`knock`),
*Claude finished* (`completed`, only for turns longer than 20 s), *Claude errored* (`angry_alert`).

### Safety of the typing keys

- Every keystroke is sent by one AppleScript that first checks which app is in front and refuses if
  it is not the expected terminal — a mistimed press cannot type into your editor.
- An answer key re-reads the session's state at the last moment; if the prompt is already gone, no
  digit is typed.
- "always" sends `2`. On a prompt that only offers yes/no, `2` is *no* — it fails safe.
- Text is passed as an argument, never interpolated into a script or a shell.
- The hook only writes a file and exits 0. It never returns a permission decision, so it cannot
  approve, deny or slow anything, and it coexists with your other hooks.

## Install

```sh
brew install dotnet                 # .NET 10 SDK, no sudo (swiftc from the Xcode CLT builds the helper)
source env.sh
dotnet build plugin/src/ClaudeDeckPlugin.csproj -c Release   # builds, links into Logi Plugin Service, reloads
hooks/install-hooks.sh              # wires 11 events into ~/.claude/settings.json (backup kept)
```

Then in **Logi Options+** → your keypad → *Claude Deck*: drag **Claude Sessions**, **Needs me**,
**Working** and **Allow** onto keys. For haptics: MX Master 4 → *Haptic feedback* → enable Claude Deck.

The typing keys need **System Settings → Privacy & Security → Accessibility → Logi Plugin Service**.
Status, colours and focusing work without it.

Undo: `hooks/install-hooks.sh --uninstall` removes only its own entries;
`dotnet build … -t:Clean` removes the plugin link.

## Configuration

`~/.claude/deck/config.json`, re-read within a second. See [`config.example.json`](config.example.json)
— tile text source, context-window sizes per model, haptic toggles, and the command row.
Session tiles get `8 − keys` slots per page, so a shorter row means more sessions per page.

## How it works

```
 NSWorkspace events ─► deck-apps (Swift) ─ JSON lines ─► AppWatcher ─► App Switcher, "here", badges

claude ─ hooks ─► deck-hook.sh ─ one jq call, atomic write ─► ~/.claude/deck/sessions/<key>.json
                  key = Warp pane uuid, else Claude session id                │
                                                             FileSystemWatcher + 2 s poll
 warp.sqlite ─ read-only ─► tab grouping ─┐                                   ▼
 transcript tail ─► title, tokens, model ─┴──────────────────────────► SessionStore
                                                                  │            │
                                                          tiles + keys    state transitions
                                                                               ▼
                                                                  haptic events → MX Master 4
```

State lives in files, not a socket: it survives plugin reloads, needs no port, and a hook that
reaches nothing still exits 0. Sessions are reaped when their `claude` PID is gone.

## Developing

```sh
dotnet build plugin/src/ClaudeDeckPlugin.csproj              # Debug build + reload
DYLD_LIBRARY_PATH=/Applications/Utilities/LogiPluginService.app/Contents/MonoBundle \
  dotnet fsi tools/preview.fsx out/                          # render every tile to PNG, no keypad needed
tail -f ~/Library/Application\ Support/Logi/LogiPluginService/Logs/plugin_logs/ClaudeDeck.log
```

## Limits

- macOS only. Exact-pane focus is Warp only; other terminals are activated as an app.
- `warp.sqlite` is Warp's internal schema; if it changes, Warp sessions collapse onto one page —
  status and focus keep working.
- Sessions that were already running before the hooks were installed appear on their next event.

MIT.
