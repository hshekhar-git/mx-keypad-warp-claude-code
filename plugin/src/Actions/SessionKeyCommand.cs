namespace Loupedeck.ClaudeDeckPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;

    // Shared machinery for the home-page keys: the ones that live outside the folder and answer one
    // question at a glance. Subscribed for the plugin's lifetime rather than while a folder is open,
    // and repainting only when their answer changes.
    public abstract class SessionKeyCommand : PluginDynamicCommand
    {
        private readonly Timer _blink;
        private EventHandler _onChanged;
        private String _signature = "";

        // The session the last press jumped to, so the next press carries on from it.
        private volatile String _cursor;

        protected volatile Int32 Frame;

        // The device type is not decoration: a command's image size is fixed at construction, and the
        // default resolves to an 80px image inside a 116px key. Naming the keypad gets edge to edge.
        protected SessionKeyCommand(String displayName, String description)
            : base(displayName, description, "Claude", (DeviceType)DeviceTypeAliases.MxCreativeKeypad)
        {
            // A widget owns the whole key face instead of sharing it with the action's name.
            this.IsWidget = true;
            this._blink = new Timer(_ => this.OnBlink(), null, Timeout.Infinite, Timeout.Infinite);
        }

        protected static List<SessionInfo> InState(String state) =>
            SessionStore.Instance.All.Where(s => s.State == state).OrderBy(s => s.Since).ToList();

        // Every session this key can jump to, in the order pressing it should walk them.
        protected abstract List<SessionInfo> Candidates();

        // Anything that changes what the key shows must change this.
        protected abstract String Signature();

        protected virtual Boolean Blinks => false;

        protected override Boolean OnLoad()
        {
            this._onChanged = (_, _) => this.Refresh();
            SessionStore.Instance.Changed += this._onChanged;
            this.Refresh();
            return true;
        }

        protected override Boolean OnUnload()
        {
            this._blink.Change(Timeout.Infinite, Timeout.Infinite);
            return true;
        }

        // Each press moves to the next candidate, wrapping. Position is remembered as a session, not
        // an index: the list is rebuilt from live state on every press, and -1 + 1 wrapping to the
        // first is exactly right once the one you dealt with has left the list.
        protected override void RunCommand(String actionParameter)
        {
            var candidates = this.Candidates();
            if (candidates.Count == 0)
            {
                return;
            }

            var at = candidates.FindIndex(s => s.Key == this._cursor);
            var next = candidates[(at + 1) % candidates.Count];
            this._cursor = next.Key;
            Deck.Focus(next);
        }

        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) => "";

        private void Refresh()
        {
            var signature = this.Signature();
            if (signature == this._signature)
            {
                return;
            }

            this._signature = signature;
            var period = this.Blinks ? TileRenderer.TickMs * 2 : Timeout.Infinite;
            this._blink.Change(period, period);
            this.ActionImageChanged();
        }

        private void OnBlink()
        {
            this.Frame += 2;
            this.ActionImageChanged();
        }
    }

    // How many sessions are waiting on you, in order of urgency: blocked on a prompt, then errored,
    // then merely finished. The number and the press always agree - it only counts what it can reach.
    public class NeedsMeCommand : SessionKeyCommand
    {
        public NeedsMeCommand()
            : base("Needs me", "How many Claude sessions are waiting for you; press to jump to the next one")
        {
        }

        protected override Boolean Blinks => InState("attention").Count > 0;

        protected override List<SessionInfo> Candidates()
        {
            foreach (var state in new[] { "attention", "error", "done" })
            {
                var list = InState(state);
                if (list.Count > 0)
                {
                    return list;
                }
            }

            return new List<SessionInfo>();
        }

        protected override String Signature()
        {
            var all = SessionStore.Instance.All;
            return String.Join(":", new[] { "attention", "error", "done", "busy" }.Select(st => all.Count(s => s.State == st))) + ":" + all.Count;
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            var all = SessionStore.Instance.All;
            return TileRenderer.NeedsMe(
                all.Count(s => s.State == "attention"), all.Count(s => s.State == "error"),
                all.Count(s => s.State == "done"), all.Count(s => s.State == "busy"), all.Count,
                imageSize, this.Frame);
        }
    }

    // Whether anything is still running before you walk away.
    public class WorkingCommand : SessionKeyCommand
    {
        public WorkingCommand()
            : base("Working", "How many Claude sessions are running; press to jump to the next one")
        {
        }

        protected override List<SessionInfo> Candidates() => InState("busy");

        protected override String Signature() => InState("busy").Count.ToString();

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize) =>
            TileRenderer.Working(InState("busy").Count, imageSize);
    }

    // The oldest open permission prompt, shown in full on the key. Press to allow it; hold to go and
    // look at it instead. It only ever answers the prompt it is displaying.
    public class AllowCommand : SessionKeyCommand
    {
        private volatile Boolean _held;

        public AllowCommand()
            : base("Allow", "Shows the permission prompt a Claude session is blocked on; press to allow, hold to inspect")
        {
        }

        protected override Boolean Blinks => this.Candidates().Count > 0;

        protected override List<SessionInfo> Candidates() =>
            InState("attention").Where(s => s.NeedsPermission).ToList();

        protected override String Signature() =>
            String.Join("|", this.Candidates().Select(s => $"{s.Key}:{s.Tool}:{s.Detail}"));

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            var waiting = this.Candidates();
            return TileRenderer.Allow(waiting.FirstOrDefault(), waiting.Count, imageSize, this.Frame);
        }

        protected override void RunCommand(String actionParameter)
        {
            var first = this.Candidates().FirstOrDefault();
            if (first != null)
            {
                Deck.Respond(first, Deck.AnswersFor(first, 1).FirstOrDefault());
            }
        }

        protected override Boolean ProcessButtonEvent2(String actionParameter, DeviceButtonEvent2 buttonEvent)
        {
            switch (buttonEvent.EventType)
            {
                case DeviceButtonEventType.LongPress:
                    this._held = true;
                    Deck.Focus(this.Candidates().FirstOrDefault());
                    return true;

                case DeviceButtonEventType.RepeatPress:
                    return true;

                // Swallow the release that ends a hold, so looking is never also allowing.
                case DeviceButtonEventType.Release when this._held:
                    this._held = false;
                    return true;

                default:
                    return false;
            }
        }
    }
}
