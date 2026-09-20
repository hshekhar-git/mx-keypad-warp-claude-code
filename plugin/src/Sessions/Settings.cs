namespace Loupedeck.ClaudeDeckPlugin
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;

    public sealed class SettingOption
    {
        public String Label { get; init; } = "";
        public String Color { get; init; } = "";
    }

    // Something about a session that can be read and changed from a key: its model, its effort level,
    // its permission mode.
    public abstract class SessionSetting
    {
        public abstract String Title { get; }

        public abstract IReadOnlyList<SettingOption> Options { get; }

        // Which option the session is on, or -1 if it is on something not in the list.
        public abstract Int32 CurrentIndex(SessionInfo s);

        // What to show at rest - the real value, which may be richer than the option's label.
        public abstract String CurrentText(SessionInfo s);

        // Under the value: on a home-page key (which shows the project on top, so says what it is)...
        public virtual String Hint(SessionInfo s) => this.Title;

        // ...and on a session's own page (which shows the title on top, so has room for more).
        public virtual String Detail(SessionInfo s) => "tap to change";

        // Why it cannot be changed right now, or null.
        public virtual String Blocked(SessionInfo s) => Deck.Busy(s);

        public abstract Boolean Apply(SessionInfo s, Int32 from, Int32 to);
    }

    public sealed class ModelSetting : SessionSetting
    {
        public override String Title => "model";

        public override IReadOnlyList<SettingOption> Options =>
            DeckConfig.Models.Select(m => new SettingOption { Label = m.Label, Color = m.Color }).ToList();

        public override Int32 CurrentIndex(SessionInfo s)
        {
            var models = DeckConfig.Models;
            for (var i = 0; i < models.Count; i++)
            {
                if (models[i].Is(s.Selected))
                {
                    return i;
                }
            }

            return -1;
        }

        public override String CurrentText(SessionInfo s) => s.Selected.IsKnown ? s.Selected.Name : "?";

        public override String Hint(SessionInfo s) => s.Selected.OneM ? "model · 1M" : "model";

        public override String Detail(SessionInfo s) => s.Selected.OneM ? "1M context" : "tap to change";

        public override Boolean Apply(SessionInfo s, Int32 from, Int32 to) =>
            to >= 0 && to < DeckConfig.Models.Count && Deck.SwitchModel(s, DeckConfig.Models[to]);
    }

    public sealed class EffortSetting : SessionSetting
    {
        // The levels `claude --effort` and /effort accept, gentlest first.
        private static readonly SettingOption[] Levels =
        {
            new() { Label = "auto", Color = "" },
            new() { Label = "low", Color = "green" },
            new() { Label = "medium", Color = "blue" },
            new() { Label = "high", Color = "amber" },
            new() { Label = "xhigh", Color = "coral" },
            new() { Label = "max", Color = "red" },
        };

        public override String Title => "effort";

        public override IReadOnlyList<SettingOption> Options => Levels;

        public override Int32 CurrentIndex(SessionInfo s) => Array.FindIndex(Levels, l => l.Label == s.Effort);

        public override String CurrentText(SessionInfo s) => s.Effort.Length > 0 ? s.Effort : "auto";

        public override Boolean Apply(SessionInfo s, Int32 from, Int32 to) =>
            to >= 0 && to < Levels.Length && Deck.RunSlash(s, $"/effort {Levels[to].Label}");
    }

    // Permission mode has no "set" - only Shift-Tab, which steps it - so moving from one option to
    // another is sent as that many steps round the cycle.
    //
    // The cycle is Claude Code's own: default -> acceptEdits -> plan -> bypassPermissions -> auto ->
    // default, where the last two stops exist only if they are enabled for you. There is no way to
    // ask whether they are, so a stop is included once any session has been seen sitting on it.
    //
    // A hook only reports the mode when something happens, so right after a change the file still
    // says the old one. What was just set is remembered and shown until the session's next event
    // confirms or corrects it.
    public sealed class ModeSetting : SessionSetting
    {
        private static readonly (String Id, String Label, String Color, Boolean Optional)[] Cycle =
        {
            ("default", "ask", "", false),
            ("acceptEdits", "auto-edit", "amber", false),
            ("plan", "plan", "blue", false),
            ("bypassPermissions", "bypass", "red", true),
            ("auto", "auto", "violet", true),
        };

        private static readonly ConcurrentDictionary<String, Boolean> Seen = new();
        private static readonly ConcurrentDictionary<String, (String Mode, Int64 Ts)> Predicted = new();

        public override String Title => "mode";

        private static List<(String Id, String Label, String Color, Boolean Optional)> Available()
        {
            foreach (var s in SessionStore.Instance.All)
            {
                if (s.Mode.Length > 0)
                {
                    Seen[s.Mode] = true;
                }
            }

            return Cycle.Where(m => !m.Optional || Seen.ContainsKey(m.Id)).ToList();
        }

        public override IReadOnlyList<SettingOption> Options =>
            Available().Select(m => new SettingOption { Label = m.Label, Color = m.Color }).ToList();

        private static String Effective(SessionInfo s) =>
            Predicted.TryGetValue(s.Key, out var p) && p.Ts == s.Ts ? p.Mode : s.Mode;

        public override Int32 CurrentIndex(SessionInfo s) => Available().FindIndex(m => m.Id == Effective(s));

        public override String CurrentText(SessionInfo s)
        {
            var mode = Effective(s);
            var known = Cycle.FirstOrDefault(m => m.Id == mode);
            return known.Id != null ? known.Label : mode.Length > 0 ? mode : "?";
        }

        // Shift-Tab works mid-turn; it is only a dialog that gets in the way.
        public override String Blocked(SessionInfo s) => s?.State == "attention" ? "answer it first" : null;

        public override Boolean Apply(SessionInfo s, Int32 from, Int32 to)
        {
            var modes = Available();
            if (to < 0 || to >= modes.Count)
            {
                return false;
            }

            // From a mode that is not on the cycle (dontAsk, say) Claude Code goes to default, which
            // is one step; the rest of the way is counted from there.
            //
            // A mode that is simply not known yet (no hook event has carried one) is different:
            // there is nothing to count from, so it gets a single step and no claim about the result.
            var unknown = Effective(s).Length == 0;
            var steps = unknown ? 1 : from >= 0 ? ((to - from) + modes.Count) % modes.Count : 1 + to;
            if (steps == 0 || !Deck.CycleMode(s, steps))
            {
                return false;
            }

            if (!unknown)
            {
                Predicted[s.Key] = (modes[to].Id, s.Ts);
            }

            return true;
        }
    }

    // Tap-to-step, shared by every key that changes a setting.
    //
    // Each tap moves to the next option and nothing is sent until the taps stop. A plain cycle would
    // send a command for every option passed on the way - three /model switches to get from Fable to
    // Haiku, each also rewriting the default for new sessions. Coming back round to the option
    // already in use sends nothing.
    public sealed class SettingStepper : IDisposable
    {
        private const Int32 CommitMs = 1500;
        private const Int32 NoticeMs = 1500;

        private readonly Timer _commit;
        private readonly Timer _clearNotice;

        // Fixed at the first tap, so a change of focus mid-gesture cannot redirect the change.
        private volatile String _sessionKey;
        private volatile Int32 _from = -1;
        private volatile Int32 _pending = -1;
        private volatile String _notice;

        public SettingStepper(SessionSetting setting)
        {
            this.Setting = setting;
            this._commit = new Timer(_ => this.Commit(), null, Timeout.Infinite, Timeout.Infinite);
            this._clearNotice = new Timer(_ =>
            {
                this._notice = null;
                this.Changed?.Invoke(this, EventArgs.Empty);
            }, null, Timeout.Infinite, Timeout.Infinite);
        }

        public SessionSetting Setting { get; }

        public event EventHandler Changed;

        private void Notice(String text)
        {
            this._notice = text;
            this._clearNotice.Change(NoticeMs, Timeout.Infinite);
            this.Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Tap(SessionInfo target)
        {
            var session = this._pending >= 0 ? SessionStore.Instance.Find(this._sessionKey) : target;
            var options = this.Setting.Options;
            if (session == null || options.Count == 0)
            {
                this._pending = -1;
                this.Notice("pick a session");
                return;
            }

            var blocked = this.Setting.Blocked(session);
            if (blocked != null)
            {
                this._pending = -1;
                this._commit.Change(Timeout.Infinite, Timeout.Infinite);
                this.Notice(blocked);
                return;
            }

            if (this._pending < 0)
            {
                this._sessionKey = session.Key;
                this._from = this.Setting.CurrentIndex(session);
                this._pending = this._from;
            }

            this._pending = (this._pending + 1) % options.Count;
            this._commit.Change(CommitMs, Timeout.Infinite);
            this.Changed?.Invoke(this, EventArgs.Empty);
        }

        private void Commit()
        {
            var to = this._pending;
            var from = this._from;
            var session = SessionStore.Instance.Find(this._sessionKey);
            this._pending = -1;

            var options = this.Setting.Options;
            if (session == null || to < 0 || to >= options.Count || to == from)
            {
                this.Changed?.Invoke(this, EventArgs.Empty);
                return;
            }

            this.Notice(this.Setting.Apply(session, from, to) ? $"set: {options[to].Label}" : "not sent");
        }

        public BitmapImage Render(SessionInfo target, Boolean showProject, PluginImageSize size)
        {
            var options = this.Setting.Options;
            var pending = this._pending;
            if (pending >= 0 && pending < options.Count)
            {
                return TileRenderer.Model(this.Setting.Title, options[pending].Label, "tap: next", options[pending].Color, true, false, size);
            }

            if (target == null)
            {
                return TileRenderer.Model(this.Setting.Title, "–", this._notice ?? "no session", "", false, true, size);
            }

            var index = this.Setting.CurrentIndex(target);
            var color = index >= 0 && index < options.Count ? options[index].Color : "";
            var top = showProject ? target.Project : this.Setting.Title;
            var bottom = this._notice ?? (showProject ? this.Setting.Hint(target) : this.Setting.Detail(target));
            return TileRenderer.Model(top, this.Setting.CurrentText(target), bottom, color, false, false, size);
        }

        public void Dispose()
        {
            this.Changed = null;
            this._commit.Dispose();
            this._clearNotice.Dispose();
        }
    }
}
