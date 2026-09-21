// dotnet fsi tools/preview.fsx <outdir> — renders every kind of tile to PNG without a keypad.
#r "/Applications/Utilities/LogiPluginService.app/Contents/MonoBundle/.xamarin/osx-arm64/SkiaSharp.dll"
#r "/Applications/Utilities/LogiPluginService.app/Contents/MonoBundle/PluginApi.dll"
#r "../plugin/bin/Debug/bin/ClaudeDeckPlugin.dll"
open System
open System.IO
open Loupedeck
open Loupedeck.ClaudeDeckPlugin

let out = if fsi.CommandLineArgs.Length > 1 then fsi.CommandLineArgs.[1] else "preview"
Directory.CreateDirectory out |> ignore
let now = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
let size = PluginImageSize.Width116
let save (name: string) (img: BitmapImage) = File.WriteAllBytes(Path.Combine(out, name + ".png"), img.ToArray())

let mk state kind tool detail title fill =
    let s = SessionInfo(Key = "k", Project = "web-app", Branch = "main", State = state, Kind = kind,
                        Tool = tool, Detail = detail, Since = now - 312L, TurnSince = now - 151L,
                        Prompt = "fix the login redirect loop")
    s.Title <- title
    s.ContextTokens <- int64 (fill * 200000.0)
    s.ContextWindow <- 200000
    s

save "busy" (TileRenderer.Session(mk "busy" "" "Bash" "npm test" "Landing page hero rework" 0.42, false, false, size, 3))
save "busy-selected" (TileRenderer.Session(mk "busy" "" "" "" "Landing page hero rework" 0.86, true, true, size, 6))
save "attention" (TileRenderer.Session(mk "attention" "permission" "Bash" "rm -rf node_modules && npm install --legacy-peer-deps" "x" 0.3, false, false, size, 0))
save "question" (TileRenderer.Session(mk "attention" "question" "AskUserQuestion" "" "Pick an auth provider" 0.3, false, false, size, 0))
save "done" (TileRenderer.Session(mk "done" "" "" "" "Stripe webhook retries" 0.64, false, false, size, 0))
save "idle" (TileRenderer.Session(mk "idle" "" "" "" "" -1.0, false, false, size, 0))
save "error" (TileRenderer.Session(mk "error" "" "" "" "Migrate to pnpm workspace" 0.95, false, false, size, 0))
save "needsme" (TileRenderer.Tally(2, "Needs me", "attention", 0, size))
save "working" (TileRenderer.Tally(3, "Working", "busy", 0, size))
save "allow" (TileRenderer.Allow(mk "attention" "permission" "Bash" "git push origin main --force-with-lease" "" 0.3, 2, size, 0))
save "allow-none" (TileRenderer.Allow(null, 0, size, 0))
save "key-yes" (TileRenderer.Command("yes", "green", false, size))
save "key-always" (TileRenderer.Command("always", "amber", true, size))
save "key-no" (TileRenderer.Command("no", "red", false, size))
save "key-compact" (TileRenderer.Command("/compact", null, false, size))
save "key-option" (TileRenderer.Command("2 Supabase Auth with RLS", "coral", false, size))
let q = SessionInfo(Key = "q", Project = "web-app", State = "attention", Kind = "question", Since = now, Question = "Which auth provider should we use?", Options = [| "Clerk"; "Supabase Auth"; "Auth0" |])
save "question-text" (TileRenderer.Session(q, true, false, size, 0))
let icons = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude/deck/icons")
let app name bundle hidden = AppInfo(Pid = 1, Name = name, Bundle = bundle, Hidden = hidden, Icon = System.IO.Path.Combine(icons, bundle + ".png"))
save "app-warp-front" (TileRenderer.App(app "Warp" "dev.warp.Warp-Stable" false, true, "attention", 2, false, size))
save "app-chrome" (TileRenderer.App(app "Google Chrome" "com.google.Chrome" false, false, "", 0, false, size))
save "app-figma-hidden" (TileRenderer.App(app "Figma" "com.figma.Desktop" true, false, "", 0, false, size))
save "app-code-flash" (TileRenderer.App(app "Code" "com.microsoft.VSCode" false, false, "busy", 1, true, size))
save "app-noicon" (TileRenderer.App(app "Mystery" "x.y.z" false, false, "", 0, false, size))
printfn "ok"
