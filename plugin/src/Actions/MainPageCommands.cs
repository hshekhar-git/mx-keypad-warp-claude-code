namespace Loupedeck.ClaudeDeckPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;

    // Keys for the keypad's main page - the one you look at while doing something else. Two counts
    // tell you HOW MANY sessions want something; these tell you WHICH, and get you there.

    // Every session in one key. Pressing walks the ones that want you, most urgent first.
    public class OverviewCommand : SessionKeyCommand
    {
        public OverviewCommand()
            : base("Overview", "Every Claude session as a coloured square, with the most urgent thing as a headline; press to go to it")
        {
        }

        protected override Boolean Pulses => Urgency.Tiers()[0].Sessions.Count > 0;

        protected override IReadOnlyList<SessionInfo> Walk() => Urgency.Queue();

        protected override String Fingerprint() =>
            String.Join("|", SessionStore.Instance.All.Select(s => $"{s.Key}:{s.State}:{s.IsLimited}")) + "#" + Deck.Target?.Key;

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize) =>
            TileRenderer.Overview(SessionStore.Instance.All, Deck.Target?.Key, imageSize, this.Beat);
    }

    // The one session that most deserves you, as a full live tile: what it is, and what it wants.
    // Press to go there. When nothing wants you it says so, and what is still running.
    public class NextCommand : SessionKeyCommand
    {
        public NextCommand()
            : base("Next", "The Claude session that most needs you, shown in full; press to go to it")
        {
        }

        protected override Boolean Pulses => Urgency.Tiers()[0].Sessions.Count > 0;

        protected override IReadOnlyList<SessionInfo> Walk() => Urgency.Queue();

        protected override String Fingerprint()
        {
            var next = Urgency.Queue().FirstOrDefault();
            var all = SessionStore.Instance.All;
            return next == null
                ? $"clear:{all.Count(s => s.State == "busy")}:{all.Count}"
                : $"{next.Key}:{next.State}:{next.Kind}:{next.Detail}:{next.Question}:{next.Title}:{next.Limit}:{Urgency.Queue().Count}";
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            var queue = Urgency.Queue();
            if (queue.Count == 0)
            {
                var all = SessionStore.Instance.All;
                return TileRenderer.AllClear(all.Count(s => s.State == "busy"), all.Count, imageSize);
            }

            var header = queue.Count > 1 ? $"NEXT · 1 of {queue.Count}" : "NEXT";
            return TileRenderer.Session(queue[0], false, false, imageSize, this.Beat, header);
        }

        // Always the head of the queue, not a walk through it: dealing with it is what moves it on.
        protected override void RunCommand(String actionParameter) => Deck.Focus(Urgency.Queue().FirstOrDefault());
    }

    // Session slots: the deck itself, on the main page. "Slot 3" is the third session, in the same
    // stable order as the list (Warp window, tab, pane) - so a session keeps its key while it lives.
    // Press to jump to its pane (which also makes it the target of Model / Effort / Mode / Allow);
    // hold to interrupt it.
    public class SessionSlotCommand : PluginDynamicCommand
    {
        private const Int32 Slots = 8;

        private readonly Timer _tick;
        private EventHandler _onChanged;
        private volatile Int32 _frame;
        private volatile String _held;
        private volatile String _flash;
        private String _drawn = "";

        public SessionSlotCommand()
            : base((DeviceType)DeviceTypeAliases.MxCreativeKeypad)
        {
            this.IsWidget = true;
            for (var i = 1; i <= Slots; i++)
            {
                this.AddParameter(i.ToString(), $"Session slot {i}", "Session slots");
            }

            this._tick = new Timer(_ => this.OnTick(), null, Timeout.Infinite, Timeout.Infinite);
        }

        private static SessionInfo At(String actionParameter) =>
            Int32.TryParse(actionParameter, out var n) && n >= 1 && n <= SessionStore.Instance.All.Count
                ? SessionStore.Instance.All[n - 1]
                : null;

        protected override Boolean OnLoad()
        {
            this._onChanged = (_, _) => this.Refresh();
            SessionStore.Instance.Changed += this._onChanged;
            Deck.TargetChanged += this._onChanged;
            AppWatcher.Instance.Changed += this._onChanged;
            this.Refresh();
            return true;
        }

        protected override Boolean OnUnload()
        {
            this._tick.Change(Timeout.Infinite, Timeout.Infinite);
            return true;
        }

        private void Refresh()
        {
            var all = SessionStore.Instance.All;

            // The sweep and the blink only run while something is actually moving.
            var moving = all.Take(Slots).Any(TileRenderer.Animates);
            this._tick.Change(moving ? TileRenderer.FrameMs : Timeout.Infinite, moving ? TileRenderer.FrameMs : Timeout.Infinite);

            var signature = String.Join("|", all.Take(Slots).Select(s =>
                $"{s.Key}:{s.State}:{s.Kind}:{s.Tool}:{s.Detail}:{s.Title}:{s.Prompt}:{s.Limit}:{s.Selected.Short}:{(Int32)(s.ContextFill * 100)}"))
                + "#" + Deck.Target?.Key;
            if (signature != this._drawn)
            {
                this._drawn = signature;
                this.ActionImageChanged();
            }
        }

        private void OnTick()
        {
            this._frame++;
            var all = SessionStore.Instance.All;

            // A sparkle ends by the clock, not by an event, so it is here that the ticking notices
            // there is nothing left to animate - after one last repaint to show the settled tile.
            if (!all.Take(Slots).Any(TileRenderer.Animates))
            {
                this._tick.Change(Timeout.Infinite, Timeout.Infinite);
                this.ActionImageChanged();
                return;
            }

            for (var i = 0; i < Math.Min(Slots, all.Count); i++)
            {
                if (TileRenderer.Animates(all[i]))
                {
                    this.ActionImageChanged((i + 1).ToString());
                }
            }

            // Resting tiles count time too ("done 4:59"), just not four times a second.
            if (this._frame % 40 == 0)
            {
                this.ActionImageChanged();
            }
        }

        protected override void RunCommand(String actionParameter)
        {
            var s = At(actionParameter);
            if (s == null)
            {
                return;
            }

            this._flash = actionParameter;
            this.ActionImageChanged(actionParameter);
            Deck.Focus(s);
            System.Threading.Tasks.Task.Delay(450).ContinueWith(_ =>
            {
                this._flash = null;
                this.ActionImageChanged(actionParameter);
            });
        }

        protected override Boolean ProcessButtonEvent2(String actionParameter, DeviceButtonEvent2 buttonEvent)
        {
            switch (buttonEvent.EventType)
            {
                case DeviceButtonEventType.LongPress:
                    this._held = actionParameter;
                    var s = At(actionParameter);
                    if (s != null)
                    {
                        Deck.Interrupt(s);
                    }

                    return true;
                case DeviceButtonEventType.RepeatPress:
                    return true;
                case DeviceButtonEventType.Release when this._held == actionParameter:
                    this._held = null;
                    return true;
                default:
                    return false;
            }
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            var s = At(actionParameter);
            return s != null
                ? TileRenderer.Session(s, Deck.Target?.Key == s.Key, this._flash == actionParameter, imageSize, this._frame)
                : TileRenderer.EmptySlot(Int32.TryParse(actionParameter, out var n) ? n : 0, imageSize);
        }

        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) => "";
    }
}
