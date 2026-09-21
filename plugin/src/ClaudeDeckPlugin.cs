namespace Loupedeck.ClaudeDeckPlugin
{
    using System;
    using System.Threading.Tasks;

    public class ClaudeDeckPlugin : Plugin
    {
        // Haptic events. The names must match package/events/*.yaml exactly.
        private const String EventAttention = "needsAttention";
        private const String EventDone = "turnDone";
        private const String EventError = "sessionError";

        public override Boolean UsesApplicationApiOnly => true;

        // A status deck is for watching sessions while working somewhere else, so it must not be
        // tied to an application profile that switches away when the terminal loses focus.
        public override Boolean HasNoApplication => true;

        public ClaudeDeckPlugin() => PluginLog.Init(this.Log);

        public override void Load()
        {
            this.PluginEvents.AddEvent(EventAttention, "Claude needs you", "A Claude Code session is blocked on a permission prompt, a question or a plan");
            this.PluginEvents.AddEvent(EventDone, "Claude finished", "A Claude Code session finished a long turn");
            this.PluginEvents.AddEvent(EventError, "Claude errored", "A Claude Code session stopped on an error");

            TermInput.AccessibilityDenied += (_, _) => this.OnPluginStatusChanged(
                Loupedeck.PluginStatus.Error,
                "macOS blocked the keystroke. Grant Logi Plugin Service access under System Settings > "
                + "Privacy & Security > Accessibility, then press the key again.",
                "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility",
                "Open Accessibility settings");

            DeckConfig.Start();
            UsageStore.Start();
            SessionStatus.Sweep();

            AppWatcher.HelperDir = System.IO.Path.GetDirectoryName(this.AssemblyFilePath);
            AppWatcher.Instance.EnsureStarted();

            // Load has a 10 second budget and the host drops a plugin that overruns it, so the store -
            // which shells out to sqlite3 and reads transcripts - is warmed off this thread.
            Task.Run(() =>
            {
                try
                {
                    var store = SessionStore.Instance;
                    store.Transition += this.OnTransition;

                    // Which pane is "here" depends on which app is in front, so a change of app is
                    // worth an immediate look rather than waiting for the next poll.
                    AppWatcher.Instance.Changed += (_, _) => store.Poke();
                    PluginLog.Info($"session store ready: {store.All.Count} session(s), hooks {(HookStatus.IsWired ? "wired" : "NOT wired")}");
                }
                catch (Exception ex)
                {
                    PluginLog.Error(ex, "Could not start the session store");
                }
            });
        }

        public override void Unload()
        {
            // Statics live per load context, not per process: without this every reload would leave
            // its predecessor's timers and file watcher running.
            DeckConfig.Shutdown();
            UsageStore.Shutdown();
            TermInput.Shutdown();
            Deck.Shutdown();
            AppWatcher.Shutdown();
            SessionStore.Shutdown();
        }

        // Turns state changes into haptic events, which Options+ maps to a buzz on an MX Master 4.
        private void OnTransition(Object sender, TransitionEventArgs e)
        {
            try
            {
                var s = e.Session;
                switch (s.State)
                {
                    case "attention" when DeckConfig.HapticAttention:
                        this.PluginEvents.RaiseEvent(EventAttention);
                        break;

                    case "error" when DeckConfig.HapticError:
                        this.PluginEvents.RaiseEvent(EventError);
                        break;

                    // A turn you are still watching finish does not need announcing.
                    case "done" when DeckConfig.HapticDone && e.From is "busy" or "attention":
                        var took = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - s.TurnSince;
                        if (s.TurnSince > 0 && took >= DeckConfig.HapticMinTurnSeconds)
                        {
                            this.PluginEvents.RaiseEvent(EventDone);
                        }

                        break;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning($"haptic event failed: {ex.Message}");
            }
        }
    }
}
