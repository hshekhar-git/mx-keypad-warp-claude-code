namespace Loupedeck.ClaudeDeckPlugin
{
    using System;
    using System.Linq;
    using System.Threading;

    // One key that answers "which model is this session on?" and changes it.
    //
    // Tapping steps through the configured models, and the switch is only sent once the taps stop. A
    // plain cycle would fire "/model" for every model you pass on the way to the one you want - and
    // each of those also rewrites your default for new sessions - so getting from Fable to Sonnet
    // past Opus is one switch, not two. Landing back on the model already in use sends nothing.
    public class ModelCommand : PluginDynamicCommand
    {
        private const Int32 CommitMs = 1500;
        private const Int32 NoticeMs = 1400;

        private readonly Timer _commit;
        private readonly Timer _clearNotice;
        private EventHandler _onChanged;
        private String _signature = "";

        // While stepping: the session being changed (fixed at the first tap, so a change of focus
        // mid-gesture cannot redirect it) and the model the taps have reached.
        private volatile String _sessionKey;
        private volatile Int32 _pending = -1;
        private volatile String _notice;

        public ModelCommand()
            : base("Model", "Shows the selected Claude session's model; tap to step to another", "Claude",
                (DeviceType)DeviceTypeAliases.MxCreativeKeypad)
        {
            this.IsWidget = true;
            this._commit = new Timer(_ => this.Commit(), null, Timeout.Infinite, Timeout.Infinite);
            this._clearNotice = new Timer(_ =>
            {
                this._notice = null;
                this.ActionImageChanged();
            }, null, Timeout.Infinite, Timeout.Infinite);
        }

        protected override Boolean OnLoad()
        {
            this._onChanged = (_, _) => this.Refresh();
            SessionStore.Instance.Changed += this._onChanged;
            AppWatcher.Instance.Changed += this._onChanged;
            Deck.TargetChanged += this._onChanged;
            DeckConfig.Changed += this._onChanged;
            return true;
        }

        protected override Boolean OnUnload()
        {
            this._commit.Change(Timeout.Infinite, Timeout.Infinite);
            return true;
        }

        private void Refresh()
        {
            var s = Deck.ModelTarget;
            var signature = $"{s?.Key}|{s?.Selected.Name}|{s?.Selected.OneM}|{s?.State}|{s?.Project}|{DeckConfig.Models.Count}";
            if (signature != this._signature)
            {
                this._signature = signature;
                this.ActionImageChanged();
            }
        }

        private void Notice(String text)
        {
            this._notice = text;
            this._clearNotice.Change(NoticeMs, Timeout.Infinite);
            this.ActionImageChanged();
        }

        protected override void RunCommand(String actionParameter)
        {
            var models = DeckConfig.Models;
            var session = this._pending >= 0 ? SessionStore.Instance.Find(this._sessionKey) : Deck.ModelTarget;
            if (session == null)
            {
                this._pending = -1;
                this.Notice("pick a session");
                return;
            }

            if (session.State is "busy" or "attention")
            {
                this._pending = -1;
                this._commit.Change(Timeout.Infinite, Timeout.Infinite);
                this.Notice(session.State == "busy" ? "busy - wait" : "answer it first");
                return;
            }

            var from = this._pending;
            if (from < 0)
            {
                this._sessionKey = session.Key;
                from = -1;
                for (var i = 0; i < models.Count; i++)
                {
                    if (models[i].Is(session.Selected))
                    {
                        from = i;
                        break;
                    }
                }
            }

            this._pending = (from + 1) % models.Count;
            this._commit.Change(CommitMs, Timeout.Infinite);
            this.ActionImageChanged();
        }

        private void Commit()
        {
            var index = this._pending;
            var session = SessionStore.Instance.Find(this._sessionKey);
            var models = DeckConfig.Models;
            this._pending = -1;

            if (session == null || index < 0 || index >= models.Count)
            {
                this.ActionImageChanged();
                return;
            }

            if (models[index].Is(session.Selected))
            {
                PluginLog.Info("model unchanged: the taps came back round to the one in use");
                this.ActionImageChanged();
                return;
            }

            this.Notice(Deck.SwitchModel(session, models[index]) ? $"set: {models[index].Label}" : "not sent");
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            var models = DeckConfig.Models;
            var pending = this._pending;
            if (pending >= 0 && pending < models.Count)
            {
                var session = SessionStore.Instance.Find(this._sessionKey);
                return TileRenderer.Model(session?.Project ?? "", models[pending].Label, "tap: next", models[pending].Color, true, false, imageSize);
            }

            var s = Deck.ModelTarget;
            if (s == null)
            {
                return TileRenderer.Model("Model", "–", this._notice ?? "no session", "", false, true, imageSize);
            }

            var current = models.FirstOrDefault(m => m.Is(s.Selected));
            var name = s.Selected.IsKnown ? s.Selected.Name : "?";
            var bottom = this._notice ?? (s.Selected.OneM ? "1M context" : "model");
            return TileRenderer.Model(s.Project, name, bottom, current?.Color ?? "", false, false, imageSize);
        }

        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) => "";
    }
}
