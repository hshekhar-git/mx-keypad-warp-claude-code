// dotnet fsi tools/steps.fsx — renders the tiles for every picture in docs/steps/, using the plugin's
// own renderer, so the guide shows exactly what the keypad shows. tools/make-steps.sh runs this and
// then turns docs/steps/steps.html into PNGs.
//
// Needs a Debug build (dotnet build plugin/src/ClaudeDeckPlugin.csproj -p:NoPluginLink=true) and
//   DYLD_LIBRARY_PATH=/Applications/Utilities/LogiPluginService.app/Contents/MonoBundle
#r "/Applications/Utilities/LogiPluginService.app/Contents/MonoBundle/.xamarin/osx-arm64/SkiaSharp.dll"
#r "/Applications/Utilities/LogiPluginService.app/Contents/MonoBundle/PluginApi.dll"
#r "../plugin/bin/Debug/bin/ClaudeDeckPlugin.dll"
open System
open System.IO
open Loupedeck
open Loupedeck.ClaudeDeckPlugin

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "docs", "steps", "tiles"))
if Directory.Exists root then Directory.Delete(root, true)
Directory.CreateDirectory root |> ignore

let size = PluginImageSize.Width116
let now = DateTimeOffset.UtcNow.ToUnixTimeSeconds()

// ---- a cast of sessions, with made-up names ---------------------------------------------------
let session project title state kind tool detail ctx sinceSecs =
    let s = SessionInfo(Key = project, Project = project, Branch = "main", State = state, Kind = kind, Tool = tool,
                        Detail = detail, Since = now - sinceSecs, TurnSince = now - 95L, Started = now - 7500L,
                        Turns = 12, Mode = "acceptEdits", Bundle = "dev.warp.Warp-Stable")
    s.Title <- title
    s.Selected <- ModelNames.FromDisplay "Fable 5.1 (1M context)"
    s.Effort <- "high"
    s.ContextTokens <- int64 (ctx * 1000000.0)
    s.ContextWindow <- 1000000
    s

let web     = session "web-app" "Checkout page redesign" "busy" "" "Edit" "" 0.32 40L
let api     = session "api" "Rate limiter for /search" "attention" "permission" "Bash" "npm run db:migrate" 0.18 25L
let docs    = session "docs" "Rewrite the quick start" "done" "" "" "" 0.09 200L
let infra   = session "infra" "Staging deploy pipeline" "busy" "" "Bash" "" 0.61 40L
let mobile  = session "mobile" "Push notification opt-in" "idle" "" "" "" 0.04 900L
let all     = ResizeArray [ web; api; docs; infra; mobile ]
let calm    = ResizeArray [ web; docs; infra; mobile ]            // nobody blocked

// ---- keys the host draws itself, approximated -------------------------------------------------
let plain (title: string) (note: string) =
    use b = new BitmapBuilder(size)
    b.Clear(BitmapColor(10, 10, 12))
    b.FillRectangle((b.Width / 2) - 12, 0, 24, 5, BitmapColor.White)
    b.DrawText(title, 3, 30, b.Width - 6, 50, Nullable BitmapColor.White, 15)
    b.DrawText(note, 3, 84, b.Width - 6, 22, Nullable(BitmapColor(120, 124, 130)), 10)
    b.ToImage()

let back () =
    use b = new BitmapBuilder(size)
    b.Clear(BitmapColor(28, 30, 34))
    b.DrawText("←", 3, 28, b.Width - 6, 46, Nullable BitmapColor.White, 26)
    b.DrawText("back", 3, 80, b.Width - 6, 22, Nullable(BitmapColor(150, 154, 160)), 11)
    b.ToImage()

let empty () =
    use b = new BitmapBuilder(size)
    b.Clear(BitmapColor(58, 60, 64))
    b.ToImage()

let save (scene: string) (keys: BitmapImage list) =
    keys |> List.iteri (fun i key -> File.WriteAllBytes(Path.Combine(root, sprintf "%s-%d.png" scene (i + 1)), key.ToArray()))

let tile (s: SessionInfo) selected frame = TileRenderer.Session(s, selected, false, size, frame)

// ---- plan usage, as the usage row shows it ----------------------------------------------------
let usageDir = Path.Combine(Path.GetTempPath(), "deck-steps-" + Guid.NewGuid().ToString "N")
Directory.CreateDirectory usageDir |> ignore
Environment.SetEnvironmentVariable("CLAUDE_DECK_ROOT", usageDir)
let nextWednesday =
    let d = DateTime.Now
    (d.Date.AddDays(float (((3 - int d.DayOfWeek) + 7) % 7 + (if d.DayOfWeek = DayOfWeek.Wednesday then 7 else 0)))).AddHours 21.5
File.WriteAllText(
    Path.Combine(usageDir, "usage.json"),
    sprintf """{"activity":%d,"ts":%d,"five_hour":{"used_percentage":62,"resets_at":%d},"seven_day":{"used_percentage":41,"resets_at":%d},"model_scoped":[{"display_name":"Fable","utilization":57,"resets_at":%d}]}"""
        now now (now + 2L * 3600L + 14L * 60L) (DateTimeOffset(nextWednesday).ToUnixTimeSeconds()) (DateTimeOffset(nextWednesday).ToUnixTimeSeconds()))
// Made-up numbers only: never let the guide ask the real account.
File.WriteAllText(Path.Combine(usageDir, "config.json"), """{ "usage": { "perModel": false } }""")
DeckConfig.Start()
Threading.Thread.Sleep 300
UsageStore.Start()
Threading.Thread.Sleep 400
let usage i = TileRenderer.UsageKey(i, size)

// ---- the scenes -------------------------------------------------------------------------------
// 1  a bare profile, as Options+ shows it before anything is placed
save "empty" [ for _ in 1 .. 9 -> empty () ]

// 2  the suggested main page, in use: one session blocked
save "main" [
    TileRenderer.Overview(all, web.Key, size, 0)
    TileRenderer.Session(api, false, false, size, 0, "NEXT · 1 of 2")
    tile web true 1
    tile api false 0
    tile docs false 0
    tile infra false 2
    plain "Claude Sessions" "folder"
    TileRenderer.Allow(api, 1, size, 0)
    plain "App Switcher" "folder" ]

// 3  the same page with nobody waiting
save "calm" [
    TileRenderer.Overview(calm, web.Key, size, 0)
    TileRenderer.AllClear(2, 4, size)
    tile web true 3
    tile docs false 0
    tile infra false 1
    tile mobile false 0
    plain "Claude Sessions" "folder"
    TileRenderer.Allow(null, 0, size, 0)
    plain "App Switcher" "folder" ]

// 4  inside Claude Sessions: five sessions over the usage row
save "list" [ back (); tile web true 0; tile api false 0; tile docs false 0; tile infra false 3; tile mobile false 0; usage 0; usage 1; usage 2 ]

// 5  one session's page
let model  = new SettingStepper(ModelSetting())
let effort = new SettingStepper(EffortSetting())
let mode   = new SettingStepper(ModeSetting())
save "page" [
    back ()
    TileRenderer.Back(ResizeArray [ api; docs; infra; mobile ], false, size, 0)
    tile web true 2
    TileRenderer.Info(web, size)
    model.Render(web, false, size)
    effort.Render(web, false, size)
    mode.Render(web, false, size)
    TileRenderer.Command("esc", null, false, size)
    TileRenderer.Command("/compact", null, false, size) ]


// 6  a blocked session's page: the answers come first
save "answer" [
    back ()
    TileRenderer.Back(ResizeArray [ web; docs; infra; mobile ], false, size, 0)
    tile api true 0
    TileRenderer.Command("yes", "green", false, size)
    TileRenderer.Command("always", "amber", false, size)
    TileRenderer.Command("no", "red", false, size)
    TileRenderer.Info(api, size)
    model.Render(api, false, size)
    effort.Render(api, false, size) ]

// 7  a multiple-choice question
let asking = session "api" "Rate limiter for /search" "attention" "question" "AskUserQuestion" "" 0.18 25L
let question =
    SessionInfo(Key = "api", Project = "api", State = "attention", Kind = "question", Tool = "AskUserQuestion", Since = now - 20L,
                TurnSince = now - 95L, Started = now - 7500L, Turns = 12, Mode = "acceptEdits",
                Question = "Where should rate limit counters live?", Options = [| "Redis"; "Postgres"; "In memory" |])
question.Title <- asking.Title
question.Selected <- asking.Selected
question.ContextTokens <- 180000L
question.ContextWindow <- 1000000
save "question" [
    back ()
    TileRenderer.Back(ResizeArray [ web; docs; infra; mobile ], false, size, 0)
    tile question true 0
    TileRenderer.Command("1 Redis", "coral", false, size)
    TileRenderer.Command("2 Postgres", "coral", false, size)
    TileRenderer.Command("3 In memory", "coral", false, size)
    TileRenderer.Info(question, size)
    model.Render(question, false, size)
    effort.Render(question, false, size) ]

// 8  tap-to-step, mid-gesture
save "step" [
    back ()
    TileRenderer.Back(ResizeArray [ api; docs; infra; mobile ], false, size, 0)
    tile docs true 0
    TileRenderer.Info(docs, size)
    model.Render(docs, false, size)
    TileRenderer.Model("effort ~2.8x", "xhigh", TileRenderer.Ascii.Countdown 0.6, "coral", true, false, size)
    mode.Render(docs, false, size)
    TileRenderer.Command("esc", null, false, size)
    TileRenderer.Command("/compact", null, false, size) ]

// 9  the app switcher - icons come from the cache the plugin keeps; any that are missing fall back
//    to a lettered square, so this runs on a machine that has never run the plugin
let icons = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "deck", "icons")
let app name bundle hidden = AppInfo(Pid = 1, Name = name, Bundle = bundle, Hidden = hidden, Icon = Path.Combine(icons, bundle + ".png"))
save "apps" [
    back ()
    TileRenderer.App(app "Warp" "dev.warp.Warp-Stable" false, false, "attention", 1, false, size)
    TileRenderer.App(app "Code" "com.microsoft.VSCode" false, true, "", 0, false, size)
    TileRenderer.App(app "Google Chrome" "com.google.Chrome" false, false, "", 0, false, size)
    TileRenderer.App(app "Figma" "com.figma.Desktop" false, false, "", 0, false, size)
    TileRenderer.App(app "Finder" "com.apple.finder" false, false, "", 0, false, size)
    TileRenderer.App(app "Music" "com.apple.Music" true, false, "", 0, false, size)
    TileRenderer.App(app "Calendar" "com.apple.iCal" false, false, "", 0, false, size)
    TileRenderer.App(app "Preview" "com.apple.Preview" false, false, "", 0, false, size) ]

UsageStore.Shutdown()
DeckConfig.Shutdown()
Directory.Delete(usageDir, true)
printfn "tiles -> %s (%d files)" root (Directory.GetFiles(root).Length)
