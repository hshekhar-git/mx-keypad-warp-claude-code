namespace Loupedeck.ClaudeDeckPlugin
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading.Tasks;

    // The plugin itself: it owns the lifetime of the background pieces (config, sessions, usage, the
    // app watcher) and turns session state changes into haptic events. Everything a key does lives in
    // the Actions folder.
    public class ClaudeDeckPlugin : Plugin
    {
        // name -> (title, description). The names are repeated in package/events/*.yaml, which is
        // where Options+ learns which haptic waveform each one plays.
        private static readonly Dictionary<String, (String Title, String About)> Haptics = new()
        {
            ["needsAttention"] = ("Claude needs you", "A Claude Code session is blocked on a permission prompt, a question or a plan"),
            ["turnDone"] = ("Claude finished", "A Claude Code session finished a long turn"),
            ["sessionError"] = ("Claude errored", "A Claude Code session stopped on an error"),
        };

        public ClaudeDeckPlugin() => PluginLog.Init(this.Log);

        // The keypad shows these keys whatever app is in front, so the plugin is attached to none.
        public override Boolean HasNoApplication => true;

        public override Boolean UsesApplicationApiOnly => true;

        public override void Load()
        {
            foreach (var (name, (title, about)) in Haptics)
            {
                this.PluginEvents.AddEvent(name, title, about);
            }

            TermInput.AccessibilityDenied += this.OnAccessibilityDenied;

            // Cheap and needed by the first key that draws: done here.
            DeckConfig.Start();
            UsageStore.Start();
            AppWatcher.HelperDir = Path.GetDirectoryName(this.AssemblyFilePath);
            AppWatcher.Instance.EnsureStarted();

            // Not cheap - reading session files, querying Warp's database, tailing transcripts - and
            // the host unloads a plugin whose Load() takes too long. So the rest happens behind it.
            Task.Run(this.StartSessions);
        }

        private void StartSessions()
        {
            try
            {
                var sessions = SessionStore.Instance;
                sessions.Transition += this.OnTransition;

                // "Which pane am I in" depends on which app is in front; look again when that changes.
                AppWatcher.Instance.Changed += (_, _) => sessions.Poke();
                SessionStatus.Sweep();

                PluginLog.Info($"watching {sessions.All.Count} session(s); hooks are {(HookStatus.Installed ? "installed" : "NOT installed - run ./install.sh")}");
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "the session store did not start");
            }
        }

        // The host reloads a plugin into the same process, and static state survives that. Anything
        // with a timer, a watcher or a child process has to be stopped here or it runs twice.
        public override void Unload()
        {
            TermInput.AccessibilityDenied -= this.OnAccessibilityDenied;
            TermInput.Shutdown();
            Deck.Shutdown();
            AppWatcher.Shutdown();
            SessionStore.Shutdown();
            UsageStore.Shutdown();
            DeckConfig.Shutdown();
        }

        private void OnAccessibilityDenied(Object sender, EventArgs e) =>
            this.OnPluginStatusChanged(
                Loupedeck.PluginStatus.Error,
                "A keystroke was blocked by macOS. Allow \"Logi Plugin Service\" under System Settings > Privacy & Security > Accessibility, then press the key again.",
                "https://github.com/hshekhar-git/mx-keypad-warp-claude-code#step-4--allow-typing",
                "Show me how");

        // Which haptic event, if any, a change of state deserves.
        private static String HapticFor(TransitionEventArgs change)
        {
            var session = change.Session;
            switch (session.State)
            {
                case "attention":
                    return DeckConfig.HapticAttention ? "needsAttention" : null;

                case "error":
                    return DeckConfig.HapticError ? "sessionError" : null;

                case "done":
                    // Only a turn that ran long enough for you to have looked away is worth a buzz.
                    var ran = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - session.TurnSince;
                    var wasRunning = change.From is "busy" or "attention";
                    return DeckConfig.HapticDone && wasRunning && session.TurnSince > 0 && ran >= DeckConfig.HapticMinTurnSeconds
                        ? "turnDone"
                        : null;

                default:
                    return null;
            }
        }

        private void OnTransition(Object sender, TransitionEventArgs change)
        {
            try
            {
                var haptic = HapticFor(change);
                if (haptic != null)
                {
                    this.PluginEvents.RaiseEvent(haptic);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning($"haptic event not raised: {ex.Message}");
            }
        }
    }
}
