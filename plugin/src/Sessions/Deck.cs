namespace Loupedeck.ClaudeDeckPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;

    // One key's worth of answer to whatever a session is blocked on.
    public sealed class AnswerKey
    {
        public String Label { get; init; } = "";
        public String Color { get; init; } = "";

        // Typed as-is; empty means Escape.
        public String Keys { get; init; } = "";
    }

    // What the keys DO, shared by the folder and the home-page keys so that both act on the same
    // notion of "the session I am dealing with".
    public static class Deck
    {
        // Focusing is a window-server round trip, not a synchronous call; typing before it lands
        // goes to whatever was in front.
        private const Int32 FocusSettleMs = 350;

        private static volatile String _target;

        public static event EventHandler TargetChanged;

        // The session the keys act on: the pane you are actually in when a terminal is in front -
        // which keeps the keypad honest when you switch panes by hand - else the one you last
        // brought forward from the keypad.
        public static SessionInfo Target
        {
            get
            {
                var front = AppWatcher.Instance.Front;
                if (front.Length > 0)
                {
                    var here = SessionStore.Instance.All.FirstOrDefault(s => s.Here && s.Bundle == front);
                    if (here != null)
                    {
                        return here;
                    }
                }

                return SessionStore.Instance.Find(_target);
            }
        }

        // The answers a blocked session will accept, in the order its prompt lists them; empty when
        // it is not blocked on anything the keypad can answer, or when there is no room for them all
        // (offering three of four options would be worse than offering none).
        public static IReadOnlyList<AnswerKey> AnswersFor(SessionInfo s, Int32 room)
        {
            if (s == null || s.State != "attention" || room < 1)
            {
                return Array.Empty<AnswerKey>();
            }

            // Claude Code numbers a permission prompt 1 = yes, 2 = yes and don't ask again (where that
            // is offered), and Escape declines. With less room, the middle option is what goes.
            if (s.Kind == "permission")
            {
                var yes = new AnswerKey { Label = "yes", Color = "green", Keys = "1" };
                var always = new AnswerKey { Label = "always", Color = "amber", Keys = "2" };
                var no = new AnswerKey { Label = "no", Color = "red", Keys = "" };
                return room >= 3 ? new[] { yes, always, no } : room == 2 ? new[] { yes, no } : new[] { yes };
            }

            if (s.Kind == "question" && s.Options.Count > 0 && s.Options.Count <= Math.Min(room, 9))
            {
                return s.Options.Select((label, i) => new AnswerKey
                {
                    Label = $"{i + 1} {label}",
                    Color = "coral",
                    Keys = (i + 1).ToString(),
                }).ToList();
            }

            return Array.Empty<AnswerKey>();
        }

        public static void Shutdown() => TargetChanged = null;

        public static Boolean Focus(SessionInfo s)
        {
            if (s == null || !TermFocus.Focus(s))
            {
                return false;
            }

            if (_target != s.Key)
            {
                _target = s.Key;
                TargetChanged?.Invoke(null, EventArgs.Empty);
            }

            return true;
        }

        // Answers whatever a session is blocked on.
        //
        // The state is re-read at the last moment and must still be the kind of prompt the key was
        // drawn for, because a "1" that arrives after the prompt has gone is a "1" typed into the
        // message box.
        public static Boolean Respond(SessionInfo s, AnswerKey answer)
        {
            var kind = s?.Kind;
            s = SessionStore.Instance.Find(s?.Key);
            if (s == null || answer == null || s.State != "attention" || s.Kind != kind)
            {
                PluginLog.Info("nothing to answer: that session is no longer waiting on that prompt");
                return false;
            }

            if (!Focus(s))
            {
                return false;
            }

            Thread.Sleep(FocusSettleMs);
            PluginLog.Info($"answering \"{answer.Label}\" to {s.Kind} in {s.Project}");
            return answer.Keys.Length > 0
                ? TermInput.TypeText(s.Bundle, answer.Keys, false)
                : TermInput.SendEscape(s.Bundle);
        }

        // Which session a model switch should go to: the target, or the only one there is.
        public static SessionInfo ModelTarget
        {
            get
            {
                var target = Target;
                if (target != null)
                {
                    return target;
                }

                var all = SessionStore.Instance.All;
                return all.Count == 1 ? all[0] : null;
            }
        }

        // Why a slash command cannot be typed into a session right now, or null if it can. Mid-turn,
        // typed text is queued as a message to Claude; at a prompt, it lands in the dialog.
        public static String Busy(SessionInfo s) => s?.State switch
        {
            "busy" => "busy - wait",
            "attention" => "answer it first",
            _ => null,
        };

        // Types a slash command into one specific session and submits it.
        public static Boolean RunSlash(SessionInfo s, String command)
        {
            s = SessionStore.Instance.Find(s?.Key);
            if (s == null || String.IsNullOrWhiteSpace(command) || Busy(s) != null)
            {
                PluginLog.Info($"not sent: {command} ({s?.Project} is {s?.State})");
                return false;
            }

            if (!Focus(s))
            {
                return false;
            }

            Thread.Sleep(FocusSettleMs);
            PluginLog.Info($"sending \"{command}\" to {s.Project}");
            if (!TermInput.TypeText(s.Bundle, command, true))
            {
                return false;
            }

            // The result shows up in the transcript a moment later; look for it rather than waiting
            // out the usual re-read interval.
            var transcript = s.Transcript;
            System.Threading.Tasks.Task.Delay(1500).ContinueWith(_ =>
            {
                TranscriptStats.Refresh(transcript);
                SessionStore.Instance.Poke();
            });
            return true;
        }

        public static Boolean SwitchModel(SessionInfo s, ModelDef model) =>
            model != null && RunSlash(s, $"/model {model.Alias}");

        // Shift-Tab, n times: how Claude Code steps its permission mode. Works mid-turn, but not
        // while a dialog has the keyboard.
        public static Boolean CycleMode(SessionInfo s, Int32 steps)
        {
            s = SessionStore.Instance.Find(s?.Key);
            if (s == null || steps < 1 || s.State == "attention" || !Focus(s))
            {
                return false;
            }

            Thread.Sleep(FocusSettleMs);
            for (var i = 0; i < steps; i++)
            {
                if (!TermInput.SendShiftTab(s.Bundle))
                {
                    return false;
                }

                Thread.Sleep(120);
            }

            return true;
        }

        // Interrupts one specific session, wherever it is.
        public static Boolean Interrupt(SessionInfo s)
        {
            if (!Focus(s))
            {
                return false;
            }

            Thread.Sleep(FocusSettleMs);
            return TermInput.SendEscape(s.Bundle);
        }

        // A configured command key. With a terminal already in front it goes to the pane you are in,
        // which respects switching panes by hand; otherwise the target session is brought forward
        // first - unless bringForward is off, which is how home-page keys stay harmless.
        public static Boolean Send(KeyDef key, Boolean bringForward)
        {
            if (key == null)
            {
                return false;
            }

            var target = Target;
            var expected = "";
            var front = AppWatcher.Instance.Front is { Length: > 0 } known ? known : TermInput.Frontmost();
            var terminalInFront = front.Length > 0 && (front == target?.Bundle || front.StartsWith("dev.warp.", StringComparison.Ordinal));

            if (!terminalInFront && bringForward)
            {
                if (target == null)
                {
                    PluginLog.Warning("Nothing sent: no terminal is in front and no session has been selected yet.");
                    return false;
                }

                Focus(target);
                Thread.Sleep(FocusSettleMs);
                expected = target.Bundle;
            }

            return key.IsEscape
                ? TermInput.SendEscape(expected)
                : TermInput.TypeText(expected, key.Text, key.Submit);
        }
    }
}
