# mx-keypad-warp-claude-code

Every running [Claude Code](https://claude.com/claude-code) session as a live tile on a **Logitech MX
Creative Keypad** — jump to it, answer its permission prompts, interrupt it — and a buzz on an
**MX Master 4** when one needs you.

Inspired by [pffan91/claudewarp-keypad-mx](https://github.com/pffan91/claudewarp-keypad-mx) (MIT),
which worked out the hard parts: Warp's `warp://session/<uuid>` deep link, its SQLite tab layout, and
the 8-tiles-per-page rule. This is a separate implementation that goes further.

## Quick start

```sh
brew install dotnet
git clone https://github.com/hshekhar-git/mx-keypad-warp-claude-code.git
cd mx-keypad-warp-claude-code
./install.sh
```

Then drag the keys onto your keypad in Logi Options+ and grant Accessibility - the
[full walkthrough](#install) has every step, a way to check it works, and
[troubleshooting](#troubleshooting).

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

**On your home page** (drag from Options+ → *MX Keypad Warp Claude Code*)

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

### What you need

| | |
|---|---|
| **macOS** | Apple Silicon or Intel. Focus and typing use macOS APIs, so there is no Windows build |
| **Logitech MX Creative Keypad** | plugged in and showing up in Logi Options+ |
| **Logi Options+** with Logi Plugin Service **6.4 or newer** | 6.4 is the first release on .NET 10. Check: *Options+ → Settings → About*, or run `./install.sh --check` |
| **[Claude Code](https://claude.com/claude-code)** | the `claude` CLI |
| **[Warp](https://www.warp.dev)** | for exact-pane focus. Other terminals work, with app-level focus only |
| **Homebrew** | to install the two build tools below |
| *optional:* **MX Master 4** | for the haptic buzz |

### Step 1 — build tools (once)

```sh
brew install dotnet          # .NET 10 SDK - no sudo needed (the dotnet-sdk cask wants sudo; this does not)
xcode-select --install       # Swift compiler, for the app-switcher helper. Skip if Xcode or the CLT is installed
```

`jq` ships with macOS 15 and later. On anything older: `brew install jq`.

### Step 2 — get the code and install

```sh
git clone https://github.com/hshekhar-git/mx-keypad-warp-claude-code.git
cd mx-keypad-warp-claude-code
./install.sh
```

`install.sh` does four things and tells you which one failed if one does:

1. **Checks prerequisites** — macOS, Logi Plugin Service version, .NET 10, swiftc, jq, Claude Code, Warp.
2. **Builds** the plugin DLL and the native `deck-apps` helper, and writes a `.link` file into
   `~/Library/Application Support/Logi/LogiPluginService/Plugins/` pointing at this folder.
3. **Wires 11 Claude Code hooks** into `~/.claude/settings.json`. Additive: your other hooks are not
   touched, the pre-install file is kept as `settings.json.claudedeck.bak`, and re-running never
   stacks duplicates. It also copies the hook to `~/.claude/deck/deck-hook.sh` and seeds
   `~/.claude/deck/config.json`.
4. **Loads the plugin** and waits until it sees it running.

> **Keep the folder where you cloned it.** The plugin runs from here. If you move or rename the
> folder, run `./install.sh` again - it notices and restarts Logi Plugin Service for you.

### Step 3 — put the keys on your keypad

Open **Logi Options+ → MX Creative Keypad**. In the actions panel find the plugin
**MX Keypad Warp Claude Code** and drag these onto keys:

| Drag this | Group | Put it |
|---|---|---|
| **Claude Sessions** | Claude | any home-page key - it is a folder; pressing it opens the deck |
| **App Switcher** | Apps | any home-page key - also a folder |
| **Needs me**, **Working**, **Allow** | Claude | home page - they are live status keys |
| *optional:* **Open App** | Apps | a direct "go to Warp" key: type `Warp` in its form |
| *optional:* anything under **Commands**, or **Send to Claude** | Commands / Claude | home page |

### Step 4 — allow typing

Needed only by keys that type: `esc`, `/compact`, **yes / always / no**, **Allow**, holding a tile to
interrupt. Status, colours, the app switcher and jumping to a pane work without it.

**System Settings → Privacy & Security → Accessibility →** enable **Logi Plugin Service**.
(If it is not listed, press a typing key once - macOS adds the entry when the first keystroke is
blocked - or add `/Applications/Utilities/LogiPluginService.app` with the **+** button.)

### Step 5 — haptics (MX Master 4 only)

**Logi Options+ → MX Master 4 → Haptic feedback →** enable **MX Keypad Warp Claude Code**. The three
events (*Claude needs you*, *Claude finished*, *Claude errored*) can each be given a different
waveform there.

### Step 6 — check that it works

1. Open a **new** Warp tab and run `claude`. Hooks are read when a session starts, so sessions that
   were already running only show up on their next tool call.
2. Press **Claude Sessions** on the keypad. You should see a **grey** tile named after the folder.
3. Send a prompt. The tile turns **coral** with a moving bar and shows the tool in use.
4. When Claude finishes it turns **green**; *Needs me* on your home page shows `1`.
5. Click into another app, press **App Switcher**, press **Warp** - Warp comes forward and the folder
   closes.
6. Ask Claude to run something it needs permission for (`run ls in /tmp`). The tile blinks **red** and
   shows the command; the bottom keys become **yes / always / no**.

No tile? `ls ~/.claude/deck/sessions/` should hold one `.json` per running session. If it is empty
the hooks are not firing - see below.

### Updating

```sh
git pull && ./install.sh
```

### Uninstalling

```sh
./install.sh --uninstall     # removes the hooks and the plugin link, restarts Logi Plugin Service
rm -rf ~/.claude/deck        # optional: config, cached icons, session files
```

### Doing it by hand

`install.sh` is only these three commands plus checks:

```sh
source env.sh                                                  # points DOTNET_ROOT at Homebrew's dotnet
dotnet build plugin/src/ClaudeDeckPlugin.csproj -c Release     # build + link + reload
hooks/install-hooks.sh                                         # wire the hooks
```

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| The plugin is not in the Options+ action list | It did not load. Quit and reopen `/Applications/Utilities/LogiPluginService.app`, then look at `~/Library/Application Support/Logi/LogiPluginService/Logs/plugin_logs/ClaudeDeck.log` |
| Log says `Cannot load plugin ... because plugin 'ClaudeDeck' is already loaded` | Harmless on its own - the service enumerates plugins twice and stock plugins log the same line. It only matters right after **moving the folder**: the old copy is still in memory. Run `./install.sh` again, or restart Logi Plugin Service |
| Log says `Cannot load plugin from '<path>.dll'` | `PluginApi.dll` ended up in the build output. It must never ship; the project already sets `<Private>false</Private>`, so run `dotnet build ... -t:Clean` and build again |
| `dotnet: command not found`, or *"You must install .NET"* | `brew install dotnet`, and build through `./install.sh` (or `source env.sh` first): Homebrew's dotnet needs `DOTNET_ROOT` set |
| Build fails at `swiftc` | `xcode-select --install` |
| Folder opens but shows **Not set up** | The hooks are not in `~/.claude/settings.json`. Run `hooks/install-hooks.sh` |
| Folder shows **No sessions** while Claude is running | That session started before the hooks were installed - start a new one. Also confirm `jq` exists: the hook exits silently without it |
| Tiles work, but `esc` / `yes` / `/compact` do nothing | Accessibility permission (Step 4). After an Options+ update macOS sometimes drops it: toggle the entry off and on |
| A key types nothing and the log says `... is in front, not ...` | Working as designed: typing keys refuse unless the expected terminal is frontmost. Press the session tile first |
| App Switcher shows **No apps** | The helper is not running: `pgrep -fl deck-apps`. Rebuild with `./install.sh`; the log says why if it cannot start |
| All Warp sessions land on one page | Warp changed its internal database layout. Status and focus still work; only per-tab paging is lost. Please open an issue |
| No buzz on the MX Master 4 | Step 5, and check `"haptics"` in `~/.claude/deck/config.json`. *Claude finished* only fires for turns longer than `minTurnSeconds` (20) |

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

The plugin's internal id is `ClaudeDeck` (log file name, reload URL, `~/.claude/deck/`). It is kept
stable on purpose: Options+ binds the keys you have placed to that id.

MIT.
