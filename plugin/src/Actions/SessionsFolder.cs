namespace Loupedeck.ClaudeDeckPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;

    // The deck: one keypad page per Warp tab (then one per other terminal app).
    //
    // The host chunks a dynamic folder's action list into pages and keeps the top-left key for Back,
    // which leaves EIGHT tiles per page on the MX Creative Keypad. Emitting exactly eight names per
    // group - session tiles, blanks, then the command row - makes page N land on group N for free.
    //
    // The keys around the tiles are contextual. Normally the bottom row is the keys from config.json;
    // while the target session is blocked on something the keypad can answer - a permission prompt, a
    // multiple-choice question - the last free keys on ITS page become the answers, spelled out.
    // "Free" means the command row plus any blank tiles before it, so a four-option question still
    // fits on a page with a three-key row.
    public class SessionsFolder : PluginDynamicFolder
    {
        private const Int32 TilesPerPage = 8;
        private const String Notice = "notice";

        private const Int32 FlashFrames = 2;

        private readonly Timer _tick;
        private volatile Boolean _open;
        private volatile Int32 _frame;

        // The tile whose long press has already been acted on, so its Release can be ignored.
        private volatile String _held;

        // The key pressed most recently, lit for a moment so a press that worked says so - focusing
        // the pane you are already in changes nothing on screen, and silence reads as "broken".
        private volatile String _flash;
        private volatile Int32 _flashUntil;

        public SessionsFolder()
        {
            this.DisplayName = "Claude Sessions";
            this.GroupName = "Claude";
            this._tick = new Timer(_ => this.OnTick(), null, Timeout.Infinite, Timeout.Infinite);
        }

        private static SessionStore Store => SessionStore.Instance;

        public override PluginDynamicFolderNavigation GetNavigationArea(DeviceType deviceType) =>
            PluginDynamicFolderNavigation.ButtonArea;

        public override Boolean Activate()
        {
            this._open = true;
            Store.Changed += this.OnChanged;
            DeckConfig.Changed += this.OnLayoutChanged;
            Deck.TargetChanged += this.OnRepaint;
            AppWatcher.Instance.Changed += this.OnRepaint;
            this._tick.Change(TileRenderer.TickMs, TileRenderer.TickMs);
            return base.Activate();
        }

        public override Boolean Deactivate()
        {
            this._open = false;
            Store.Changed -= this.OnChanged;
            DeckConfig.Changed -= this.OnLayoutChanged;
            Deck.TargetChanged -= this.OnRepaint;
            AppWatcher.Instance.Changed -= this.OnRepaint;
            this._tick.Change(Timeout.Infinite, Timeout.Infinite);
            return base.Deactivate();
        }

        private void OnChanged(Object sender, EventArgs e)
        {
            // Rebuilding the pages on every state change would be visible churn; only a change in
            // WHICH tiles exist needs it.
            if (e is LayoutChangedEventArgs)
            {
                this.ButtonActionNamesChanged();
            }

            this.RepaintAll();
        }

        private void OnLayoutChanged(Object sender, EventArgs e)
        {
            this.ButtonActionNamesChanged();
            this.RepaintAll();
        }

        private void OnRepaint(Object sender, EventArgs e) => this.RepaintAll();

        // Only tiles that are actually moving are repainted each tick; everything else gets a slow
        // refresh so "done 4:59" still becomes "done 5:00".
        private void OnTick()
        {
            if (!this._open)
            {
                return;
            }

            var frame = ++this._frame;
            var flashed = this._flash;
            if (flashed != null && frame >= this._flashUntil)
            {
                this._flash = null;
                this.CommandImageChanged(flashed);
            }

            if (frame % 20 == 0)
            {
                this.RepaintAll();
                return;
            }

            foreach (var s in Store.All)
            {
                if (TileRenderer.Animates(s))
                {
                    this.CommandImageChanged($"s:{s.Key}");
                }
            }
        }

        private void RepaintAll()
        {
            if (!this._open)
            {
                return;
            }

            foreach (var name in this.BuildParameters())
            {
                this.CommandImageChanged(name);
            }
        }

        private List<String> BuildParameters()
        {
            var groups = Store.Groups;
            if (groups.Count == 0)
            {
                return new List<String> { Notice };
            }

            // Snapshotted once: the config is re-read on a timer and must not change the split
            // halfway through a page.
            var keys = DeckConfig.Keys.Count;
            var perPage = Math.Max(1, TilesPerPage - keys);
            var list = new List<String>();

            foreach (var g in groups)
            {
                var pages = Math.Max(1, (g.Sessions.Count + perPage - 1) / perPage);
                for (var page = 0; page < pages; page++)
                {
                    for (var slot = 0; slot < perPage; slot++)
                    {
                        var index = (page * perPage) + slot;
                        list.Add(index < g.Sessions.Count
                            ? $"s:{g.Sessions[index].Key}"
                            : $"x:{g.Id}:{page}:{slot}");
                    }

                    // Parameters must be unique across the whole list or the host folds them into
                    // one tile, hence the group and page in the name.
                    for (var i = 0; i < keys; i++)
                    {
                        list.Add($"k:{i}:{g.Id}:{page}");
                    }
                }
            }

            return list;
        }

        public override IEnumerable<String> GetButtonPressActionNames(DeviceType deviceType)
        {
            var names = this.BuildParameters().Select(p => this.CreateCommandName(p)).ToList();
            PluginLog.Info($"pages built for {deviceType}: {Store.Groups.Count} group(s), {names.Count} tile(s)");
            return names;
        }

        // Which keys are answers right now, by action parameter.
        //
        // Only on the page holding the target session, and always the LAST free keys of that page in
        // prompt order, so the final answer sits bottom-right and the rest read left to right into it.
        private Dictionary<String, AnswerKey> AnswerSlots(out SessionInfo target)
        {
            var map = new Dictionary<String, AnswerKey>(StringComparer.Ordinal);
            var t = Deck.Target;
            target = t;
            if (t == null || t.State != "attention")
            {
                return map;
            }

            var keys = DeckConfig.Keys.Count;
            var perPage = Math.Max(1, TilesPerPage - keys);
            foreach (var g in Store.Groups)
            {
                var index = g.Sessions.FindIndex(x => x.Key == t.Key);
                if (index < 0)
                {
                    continue;
                }

                var page = index / perPage;
                var onPage = Math.Min(perPage, g.Sessions.Count - (page * perPage));
                var free = new List<String>();
                for (var slot = onPage; slot < perPage; slot++)
                {
                    free.Add($"x:{g.Id}:{page}:{slot}");
                }

                for (var i = 0; i < keys; i++)
                {
                    free.Add($"k:{i}:{g.Id}:{page}");
                }

                var answers = Deck.AnswersFor(t, free.Count);
                for (var i = 0; i < answers.Count; i++)
                {
                    map[free[free.Count - answers.Count + i]] = answers[i];
                }

                break;
            }

            return map;
        }

        private static KeyDef ConfiguredKey(String actionParameter)
        {
            var parts = actionParameter.Split(':');
            var keys = DeckConfig.Keys;
            return parts.Length >= 2 && Int32.TryParse(parts[1], out var i) && i >= 0 && i < keys.Count ? keys[i] : null;
        }

        private void Flash(String actionParameter)
        {
            this._flash = actionParameter;
            this._flashUntil = this._frame + FlashFrames;
            this.CommandImageChanged(actionParameter);
        }

        // Short press focuses; holding a tile interrupts THAT session, wherever it is.
        public override Boolean ProcessButtonEvent2(String actionParameter, DeviceButtonEvent2 buttonEvent)
        {
            if (actionParameter == null || !actionParameter.StartsWith("s:", StringComparison.Ordinal))
            {
                return false;
            }

            switch (buttonEvent.EventType)
            {
                case DeviceButtonEventType.LongPress:
                    this._held = actionParameter;
                    var s = Store.Find(actionParameter.Substring(2));
                    if (s != null && Deck.Interrupt(s))
                    {
                        PluginLog.Info($"long press interrupted {s.Project}");
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

        public override void RunCommand(String actionParameter)
        {
            if (actionParameter == null)
            {
                return;
            }

            if (actionParameter.StartsWith("s:", StringComparison.Ordinal))
            {
                this.Flash(actionParameter);
                Deck.Focus(Store.Find(actionParameter.Substring(2)));
                return;
            }

            if (this.AnswerSlots(out var target).TryGetValue(actionParameter, out var answer))
            {
                this.Flash(actionParameter);
                Deck.Respond(target, answer);
                return;
            }

            if (actionParameter.StartsWith("k:", StringComparison.Ordinal) && ConfiguredKey(actionParameter) is { } key)
            {
                this.Flash(actionParameter);
                Deck.Send(key, bringForward: true);
            }
        }

        public override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            if (actionParameter == null)
            {
                return TileRenderer.Blank(imageSize);
            }

            var flash = this._flash == actionParameter;

            if (actionParameter.StartsWith("s:", StringComparison.Ordinal))
            {
                var s = Store.Find(actionParameter.Substring(2));
                return s != null
                    ? TileRenderer.Session(s, Deck.Target?.Key == s.Key, flash, imageSize, this._frame)
                    : TileRenderer.Blank(imageSize);
            }

            if (this.AnswerSlots(out _).TryGetValue(actionParameter, out var answer))
            {
                return TileRenderer.Command(answer.Label, answer.Color, flash, imageSize);
            }

            if (actionParameter.StartsWith("k:", StringComparison.Ordinal))
            {
                var key = ConfiguredKey(actionParameter);
                return key != null ? TileRenderer.Command(key.Label, key.Color, flash, imageSize) : TileRenderer.Blank(imageSize);
            }

            if (actionParameter == Notice)
            {
                return HookStatus.IsWired
                    ? TileRenderer.Message("No sessions", "start claude in a terminal", imageSize)
                    : TileRenderer.Message("Not set up", "run hooks/ install-hooks.sh", imageSize);
            }

            return TileRenderer.Blank(imageSize);
        }

        public override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) => "";
    }
}
