# mx-keypad-warp-claude-code

Every running [Claude Code](https://claude.com/claude-code) session as a live tile on a **Logitech MX
Creative Keypad** — jump to it, answer its permission prompts, interrupt it — and a buzz on an
**MX Master 4** when one needs you.

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

## The main page

Counts tell you *how many* sessions want something. These keys tell you *which*, and get you there -
so the page you look at while doing something else is the cockpit, not a menu.

```
┌──────────────┬──────────────┬──────────────┐
│   OVERVIEW   │     NEXT     │    slot 1    │
│ 1 needs you  │ web-app  │  live tile   │
│   ■ ■ ■ ■    │ allow Bash?  │              │
├──────────────┼──────────────┼──────────────┤
│    slot 2    │    slot 3    │    slot 4    │
│  live tile   │  live tile   │  live tile   │
├──────────────┼──────────────┼──────────────┤
│    Claude    │    Allow     │ App Switcher │
│   Sessions   │              │              │
└──────────────┴──────────────┴──────────────┘
```

| Key | Shows | Press |
|---|---|---|
| **Overview** | every session as a square in its state colour, under a headline for the most urgent thing: `1 needs you` → `2 errored` → `1 at limit` → `2 your turn` → `3 working`. The white-edged square is the session the keys act on | walks the sessions that want you, most urgent first |
| **Next** | the *one* session that most deserves you, as a full live tile - the command it wants to run, the question it asked, `limit · 4:40am` - headed `NEXT · 1 of 3`. When nothing wants you: `all clear · 2 working` | goes to it. Dealing with it is what moves the queue on |
| **Session slot 1-8** | your sessions *on the main page*: slot 3 is the third session, in the same stable order as the list (Warp window, tab, pane), so a session keeps its key for as long as it lives | jumps to its pane - which also makes it the target of **Model**, **Effort**, **Permission mode** and **Allow**. **Hold** to interrupt it |

The queue behind *Overview* and *Next*: blocked on you (longest first), then errored, then out of
usage, then finished (longest ago first). Working and idle sessions want nothing, so they are not in it.

Slots and the folder are the same sessions at two depths: a slot is one press to the pane; **Claude
Sessions** is where a session's own page lives (info, model, effort, mode, answers).

## Plan usage on the keypad

The list inside **Claude Sessions** keeps its bottom row for your plan:

```
┌───────────┬───────────┬───────────┐
│  ‹ Back   │ session 1 │ session 2 │   five sessions a page; a sixth starts page two,
├───────────┼───────────┼───────────┤   and the usage row comes with it
│ session 3 │ session 4 │ session 5 │
├───────────┼───────────┼───────────┤   session   the five-hour window: % used, resets in 2h 13m
│  session  │  weekly   │   pace    │   weekly    all models, this week: % used, resets Wed 9:30pm
│    88%    │    65%    │    22m    │   pace      what the usage page does not say - see below
└───────────┴───────────┴───────────┘
```

**Pace** answers "do I make it to the reset?". From how much of the five-hour window has gone and how
much you have used, it projects where you land: `~85% by the reset` in green if you make it, or
`22m until you run out` in amber/red if you do not. Blue below 75%, amber from 75%, red from 90%.

**Where the numbers come from.** Claude Code passes its status line a payload that includes your plan
usage, so the installer sets a status line command (`deck-statusline.sh`) that copies those numbers to
`~/.claude/deck/usage.json` - and, per session, the exact model, effort and context fill, which replace
what was otherwise inferred from transcripts. It reads **no credentials and makes no network calls**.

- If you had a status line already, it is kept: yours still runs on the same input and its output is
  what you see. With none, you get a small one: `Fable 5.1 · ctx 32% · 5h 88% · wk 65%`.
- Each session reports the figures from *its own last API response*, so a session idle since Tuesday
  reports Tuesday's. A report only counts if its session has been active more recently than the one
  on file; numbers older than 20 minutes are shown greyed with their age.
- The status line carries the five-hour and weekly (all models) windows only. The per-model weekly bar
  on the usage page (e.g. *Fable*) is not in it; if Claude Code adds it, the third key shows it
  instead of pace automatically.
- The same three keys exist for the main page under **Usage**. `"usageRow": false` gives the list all
  eight keys back.

## Two layers

**Claude Sessions** opens a list of every running session (five to a page above the usage row). **Press
one and you are on that session's own page:**

```
┌───────────┬───────────┬───────────┐
│  ‹ Back   │ ‹ sessions│  the tile │   ‹ sessions  up one level - and the other sessions' way of
│ (Options+)│  ● ● ●    │ Edit 2:31 │                 tapping you on the shoulder (see below)
├───────────┼───────────┼───────────┤   the tile    live; press = jump to its pane, hold = interrupt
│  32% ctx  │   model   │  effort   │   info        context % and tokens, branch, turns, age
│ 317k of 1M│ Fable 5.1 │   high    │   model       tap to step (see Model switch)
│ feat/hero │ 1M context│           │   effort      auto · low · medium · high · xhigh · max
├───────────┼───────────┼───────────┤   mode        ask · auto-edit · plan · [bypass] · [auto]
│   mode    │    esc    │ /compact  │   then your command keys; more on the next page ▶
│ auto-edit │           │           │
└───────────┴───────────┴───────────┘
```

While the session is **blocked**, the answers come first, straight after the tile: **yes / always / no**
for a permission prompt, the **actual option labels** for a multiple-choice question.

**While you are inside one session, the `‹ sessions` key watches the rest.** It blinks **red** with
the name - `web-app needs you` - when another session is blocked on you (`2 need you` for several),
turns **green** with `2 your turn` when others have finished, and otherwise just counts them. Along
its bottom edge is one dot per other session in its state colour, so the whole deck is readable
without leaving the page. **Press** it for the list; **hold** it to skip the list and land directly on
whichever session needs you most (blocked longest, else errored, else finished longest ago).

**Out of usage** - when a session hits your usage limit its tile turns **amber** and reads
`limit · 4:40am` (when it lifts), and its page gains a **continue at low priority** key, which sends
`/low-priority`. It is read from the transcript: the newest reply being a `rate_limit` error means
limited; any later reply, or the reset time passing, means not.

- **Everything that can be changed is changed the same way:** tap to step through the choices, ringed
  in white; it is sent once, a second and a half after your last tap. Tapping back round to the value
  in use sends nothing.
- **Effort** is read from the session's own `/effort` history (the whole transcript is searched once,
  since one early `/effort` can be megabytes back), else `effortLevel` in your settings, else `auto`.
- **Permission mode** is changed with Shift-Tab, so a change is sent as that many steps round Claude
  Code's own cycle. `bypass` and `auto` are only stops on that cycle if they are enabled for you, and
  nothing says whether they are - so each joins the key's choices once a session has been seen on it.
- The mode itself comes from Claude Code's hooks, which only report it when something happens. So
  right after a change the key shows what it just set, and the next event confirms or corrects it.
  Mode changes work mid-turn; model and effort are refused while the session is busy.
- Reopening the folder starts at the list - unless exactly one session is blocked on you, in which
  case it opens straight onto that one.
- **Model**, **Effort** and **Permission mode** also exist as home-page keys, acting on the target
  session (the pane you are in, else the last one you opened).

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
| amber | **limit** — out of usage until the time shown | the transcript's `rate_limit` reply |
| grey | **idle** | `SessionStart` |



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

## Model switch

**Model** is a live key: it shows which model the target session is set to - `Fable 5.1`, with
`1M context` underneath when it is on the big window - in that model's colour, and the project it
belongs to. Resting tiles carry the same thing as a short tag: `done 5:12 · F5.1`.

**Tap to change it.** Each tap steps to the next model in your list, ringed in white; the switch is
sent once, about a second and a half after your last tap. So Fable → Sonnet, passing Opus, is *one*
`/model`, not two - which matters, because Claude Code's `/model` also saves the choice as your
default for new sessions. Tapping all the way round to the model already in use sends nothing.

**Models** is the same thing as a folder: every model as a key, the one in use marked, press to set.

- The status is read from the session's transcript: the most recent of "a `/model` switch" and "the
  model that wrote the last reply". A switch shows up about a second after it is made.
- A switch is refused - the key says `busy - wait` - while the session is mid-turn or blocked on a
  prompt, because text typed then would be queued as a message or land in a dialog.
- It acts on the **target session** (the pane you are in, else the tile you last pressed); with
  exactly one session running, that one.
- The list is `models` in `config.json`. The defaults type `fable`, `opus`, `sonnet`, `haiku`; put a
  full id such as `claude-opus-5[1m]` in `alias` if you want a specific version or window.

## Keys

**Inside the *Claude Sessions* folder** - see [Two layers](#two-layers). In the list, side bars mark
the pane you are actually in; every press flashes the key; **hold** a tile to interrupt that session
without opening it.

**On your home page** (drag from Options+ → *MX Keypad Warp Claude Code*)

| Key | Shows | Press |
|---|---|---|
| **Needs me** | count of sessions blocked on you — else errored, else finished, else working | cycles through exactly the sessions it counts, longest-waiting first |
| **Working** | how many are still running | cycles through them |
| **Allow** | the oldest open permission prompt *spelled out*: tool, command, project | **allows it**. **Hold** to go and look instead |
| **Model** | the target session's model, and whether it is on the 1M window | steps to the next model; commits when you stop tapping |
| **Effort** | its effort level | steps `auto → low → medium → high → xhigh → max` |
| **Permission mode** | `ask`, `auto-edit`, `plan`, and `bypass` / `auto` where enabled | steps it (Shift-Tab) |
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
3. **Wires 11 Claude Code hooks and the status line** into `~/.claude/settings.json`. Additive: your
   other hooks are not touched, an existing status line is kept and still runs, the pre-install file is kept as `settings.json.claudedeck.bak`, and re-running never
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
| **Overview**, **Next**, **Allow** | Claude | home page - see [The main page](#the-main-page) |
| **Session slot 1…8** | Session slots | home page - as many as you usually have sessions |
| *optional:* **Needs me**, **Working** | Claude | the plain counts, if you prefer numbers |
| *optional:* **Model**, **Effort**, **Permission mode** | Claude | home page - the same controls as on a session's page, for the target session |
| *optional:* **Models** | Claude | a folder: pick a model from a list instead of tapping through |
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

## Reaching another keypad profile

Options+ swaps keypad **profiles** by the app in front: a Warp profile while you are in Warp, your
default profile everywhere else. To get from one to the other by hand - say, to your app-launcher
profile while Warp is in front - use Options+'s own action, which needs no plugin:

1. Logi Options+ → MX Creative Keypad → pick the profile you want the button **on** (e.g. *Warp*).
2. In the actions panel search **profile**; the built-in action is described as *"Switches current
   device profile to selected one"*.
3. Drag it to a free key and choose the target, e.g. **Default General Profile**.

Alternatively put **App Switcher** (or an **Open App** key) on the Warp profile: this plugin's
actions work on every profile.

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
| **Model** shows `?` | No reply has been written in that session yet and no `/model` switch was made, so there is nothing to read. It fills in after the first turn |
| A model switch types `/model x` but Claude Code rejects it | That alias is not one your Claude Code version knows. Put the full model id in `alias` in `config.json` |
| Usage keys say `no data yet` | They fill in when a session next draws its status line - send any prompt. On an API key, Bedrock or Vertex there are no plan limits to show |
| Usage keys are grey with `35m old` | No session has talked to the API for that long; the numbers refresh on the next reply |
| Usage differs from the usage page | The keys show what Claude Code derives from the rate-limit headers of its latest API response; the page is computed server-side and can disagree. Compare with `/usage` inside Claude Code |
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
