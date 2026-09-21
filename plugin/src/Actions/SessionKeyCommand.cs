namespace Loupedeck.ClaudeDeckPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;

    // A group of sessions that want the same kind of thing from you, e.g. "blocked on a prompt".
    public sealed class Tier
    {
        public String Label { get; init; } = "";

        // The session state whose colour the tier is drawn in.
        public String Colour { get; init; } = "";

        public List<SessionInfo> Sessions { get; init; } = new();
    }

    // The one place that decides how urgent a session is. Everything that ranks sessions - the count
    // keys, Overview, Next, the hint on a session page - reads it from here, so they cannot disagree.
    public static class Urgency
    {
        // Most urgent first. Within a tier, whoever has been waiting longest comes first.
        public static List<Tier> Tiers()
        {
            var all = SessionStore.Instance.All;
            List<SessionInfo> Pick(Func<SessionInfo, Boolean> test) => all.Where(test).OrderBy(s => s.Since).ToList();

            return new List<Tier>
            {
                new() { Label = "Needs me", Colour = "attention", Sessions = Pick(s => s.State == "attention") },
                new() { Label = "Errored", Colour = "error", Sessions = Pick(s => s.State == "error" && !s.IsLimited) },
                new() { Label = "At limit", Colour = "limit", Sessions = Pick(s => s.IsLimited) },
                new() { Label = "Your turn", Colour = "done", Sessions = Pick(s => s.State == "done" && !s.IsLimited) },
            };
        }

        // Every session that wants something, in the order to deal with them.
        public static List<SessionInfo> Queue() => Tiers().SelectMany(t => t.Sessions).ToList();

        public static List<SessionInfo> Working() =>
            SessionStore.Instance.All.Where(s => s.State == "busy").OrderBy(s => s.Since).ToList();
    }

    // Walks a list that is rebuilt from live state between presses. The place is kept as the key of
    // the session last visited, not as a number, because the list under it changes: if that session
    // has left the list, the walk starts again from the top, which is where the most urgent one is.
    public sealed class Rotation
    {
        private String _lastVisited;

        public SessionInfo Advance(IReadOnlyList<SessionInfo> sessions)
        {
            if (sessions.Count == 0)
            {
                return null;
            }

            var position = -1;
            for (var i = 0; i < sessions.Count; i++)
            {
                if (sessions[i].Key == this._lastVisited)
                {
                    position = i;
                    break;
                }
            }

            var next = sessions[position + 1 < sessions.Count ? position + 1 : 0];
            this._lastVisited = next.Key;
            return next;
        }
    }

    // A key that lives on the main page and reports on the sessions while you work elsewhere.
    //
    // Such a key is drawn by the plugin alone (IsWidget: no icon-plus-caption layout from the host),
    // sized for the keypad it is built for, redrawn only when what it shows has changed, and pulsing
    // only while there is something to pulse about.
    public abstract class SessionKeyCommand : PluginDynamicCommand
    {
        private readonly Rotation _rotation = new();
        private readonly Timer _pulse;
        private String _shown = "";
        private Int32 _beat;

        protected SessionKeyCommand(String name, String description)
            : base(name, description, "Claude", (DeviceType)DeviceTypeAliases.MxCreativeKeypad)
        {
            this.IsWidget = true;
            this._pulse = new Timer(_ =>
            {
                Interlocked.Add(ref this._beat, 2);
                this.ActionImageChanged();
            }, null, Timeout.Infinite, Timeout.Infinite);
        }

        // Advances while the key is pulsing; feeds the renderer's blink.
        protected Int32 Beat => Volatile.Read(ref this._beat);

        // The sessions a press walks through.
        protected abstract IReadOnlyList<SessionInfo> Walk();

        // A string that differs whenever the key's picture would.
        protected abstract String Fingerprint();

        protected virtual Boolean Pulses => false;

        protected override Boolean OnLoad()
        {
            EventHandler redraw = (_, _) => this.RedrawIfChanged();
            SessionStore.Instance.Changed += redraw;
            Deck.TargetChanged += redraw;
            this.RedrawIfChanged();
            return true;
        }

        protected override Boolean OnUnload()
        {
            this._pulse.Change(Timeout.Infinite, Timeout.Infinite);
            return true;
        }

        private void RedrawIfChanged()
        {
            var now = this.Fingerprint();
            if (now == this._shown)
            {
                return;
            }

            this._shown = now;
            var every = this.Pulses ? 2 * TileRenderer.FrameMs : Timeout.Infinite;
            this._pulse.Change(every, every);
            this.ActionImageChanged();
        }

        protected override void RunCommand(String actionParameter) => Deck.Focus(this._rotation.Advance(this.Walk()));

        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) => "";
    }

    // How many sessions want you, as one number: the size of the most urgent tier that has anyone in
    // it, in that tier's colour and under its name. Pressing walks exactly the sessions counted.
    public class NeedsMeCommand : SessionKeyCommand
    {
        public NeedsMeCommand()
            : base("Needs me", "How many Claude sessions are waiting for you; press to go to the next one")
        {
        }

        private static Tier Top() => Urgency.Tiers().FirstOrDefault(t => t.Sessions.Count > 0);

        protected override Boolean Pulses => Top()?.Colour == "attention";

        protected override IReadOnlyList<SessionInfo> Walk() => Top()?.Sessions ?? new List<SessionInfo>();

        protected override String Fingerprint() =>
            String.Join(",", Urgency.Tiers().Select(t => t.Sessions.Count)) + "/" + Urgency.Working().Count + "/" + SessionStore.Instance.All.Count;

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            var top = Top();
            if (top != null)
            {
                return TileRenderer.Tally(top.Sessions.Count, top.Label, top.Colour, this.Beat, imageSize);
            }

            // Nobody wants you: say what is going on instead.
            var working = Urgency.Working().Count;
            var total = SessionStore.Instance.All.Count;
            return working > 0 ? TileRenderer.Tally(working, "Working", "busy", 0, imageSize)
                : total > 0 ? TileRenderer.Tally(total, "Idle", "idle", 0, imageSize)
                : TileRenderer.Tally(0, "Needs me", "", 0, imageSize);
        }
    }

    // Is anything still running?
    public class WorkingCommand : SessionKeyCommand
    {
        public WorkingCommand()
            : base("Working", "How many Claude sessions are running; press to go to the next one")
        {
        }

        protected override IReadOnlyList<SessionInfo> Walk() => Urgency.Working();

        protected override String Fingerprint() => Urgency.Working().Count.ToString();

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize) =>
            TileRenderer.Tally(Urgency.Working().Count, "Working", Urgency.Working().Count > 0 ? "busy" : "", 0, imageSize);
    }

    // The oldest open permission prompt, written out on the key - tool, command, project - so that
    // pressing it is an informed yes. A short press allows; a long one goes to look instead, and the
    // release that ends a long press is eaten so that looking can never turn into allowing.
    public class AllowCommand : SessionKeyCommand
    {
        private volatile Boolean _looking;

        public AllowCommand()
            : base("Allow", "The permission prompt a Claude session is blocked on; press to allow it, hold to look at it")
        {
        }

        private static List<SessionInfo> Prompts() =>
            Urgency.Tiers()[0].Sessions.Where(s => s.NeedsPermission).ToList();

        protected override Boolean Pulses => Prompts().Count > 0;

        protected override IReadOnlyList<SessionInfo> Walk() => Prompts();

        protected override String Fingerprint() => String.Join(";", Prompts().Select(s => s.Key + s.Tool + s.Detail));

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            var prompts = Prompts();
            return TileRenderer.Allow(prompts.FirstOrDefault(), prompts.Count, imageSize, this.Beat);
        }

        protected override void RunCommand(String actionParameter)
        {
            var oldest = Prompts().FirstOrDefault();
            if (oldest != null)
            {
                Deck.Respond(oldest, Deck.AnswersFor(oldest, 1).FirstOrDefault());
            }
        }

        protected override Boolean ProcessButtonEvent2(String actionParameter, DeviceButtonEvent2 buttonEvent)
        {
            if (buttonEvent.EventType == DeviceButtonEventType.LongPress)
            {
                this._looking = true;
                Deck.Focus(Prompts().FirstOrDefault());
                return true;
            }

            if (buttonEvent.EventType == DeviceButtonEventType.RepeatPress)
            {
                return true;
            }

            if (buttonEvent.EventType == DeviceButtonEventType.Release && this._looking)
            {
                this._looking = false;
                return true;
            }

            return false;
        }
    }
}
