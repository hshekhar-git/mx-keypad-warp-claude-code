namespace Loupedeck.ClaudeDeckPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;

    // The deck, in two layers.
    //
    // LIST   every session as a live tile. With the usage row on (the default) a page is five sessions
    //        over three keys of plan usage - session, weekly, pace - repeated on every page, so a
    //        sixth session starts page two and the numbers come with you. Off, it is eight sessions.
    //        Press a session to open it; hold one to interrupt it without going in.
    //
    // PAGE   one session: a way back (which also reports on everyone else - see below), its live tile (press = jump to its pane), its facts (context,
    //        branch, turns, age), its settings (model, effort, permission mode - tap to step), then
    //        your command keys. While it is blocked on a prompt the keypad can answer, the answers
    //        come first, straight after the tile.
    //
    // The host keeps the top-left key for its own Back, which leaves the folder entirely; hence the
    // page's own "sessions" key for going up one level. Eight names per page keeps both layers
    // aligned with the host's paging.
    public class SessionsFolder : PluginDynamicFolder
    {
        private const Int32 TilesPerPage = 8;
        private const Int32 FlashFrames = 2;
        private const String Notice = "notice";

        private readonly Timer _tick;
        private readonly SettingStepper _model = new(new ModelSetting());
        private readonly SettingStepper _effort = new(new EffortSetting());
        private readonly SettingStepper _mode = new(new ModeSetting());

        private volatile Boolean _open;
        private volatile Int32 _frame;

        // The session whose page is showing; null for the list.
        private volatile String _page;

        // What the page was last laid out with, so it is rebuilt when either changes.
        private volatile Int32 _answerCount;
        private volatile Boolean _limited;

        private volatile String _held;
        private volatile String _flash;
        private volatile Int32 _flashUntil;

        public SessionsFolder()
        {
            this.DisplayName = "Claude Sessions";
            this.GroupName = "Claude";
            this._tick = new Timer(_ => this.OnTick(), null, Timeout.Infinite, Timeout.Infinite);
            this._model.Changed += (_, _) => this.Repaint("p:model");
            this._effort.Changed += (_, _) => this.Repaint("p:effort");
            this._mode.Changed += (_, _) => this.Repaint("p:mode");
        }

        private static SessionStore Store => SessionStore.Instance;

        private SessionInfo Page => Store.Find(this._page);

        private List<SessionInfo> Others => Store.All.Where(s => s.Key != this._page).ToList();

        // Who most deserves you next: blocked longest, else errored, else finished longest ago.
        private SessionInfo Neediest()
        {
            var others = this.Others;
            foreach (var state in new[] { "attention", "error", "done" })
            {
                var hit = others.Where(s => s.State == state).OrderBy(s => s.Since).FirstOrDefault();
                if (hit != null)
                {
                    return hit;
                }
            }

            return null;
        }

        public override PluginDynamicFolderNavigation GetNavigationArea(DeviceType deviceType) =>
            PluginDynamicFolderNavigation.ButtonArea;

        public override Boolean Activate()
        {
            this._open = true;

            // Reopening the folder starts at the list - unless something is waiting on you and it is
            // the only thing that is, in which case its page is where you were going anyway.
            var waiting = Store.All.Where(s => s.State == "attention").ToList();
            this._page = waiting.Count == 1 ? waiting[0].Key : null;

            Store.Changed += this.OnChanged;
            UsageStore.Changed += this.OnRepaint;
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
            UsageStore.Changed -= this.OnRepaint;
            DeckConfig.Changed -= this.OnLayoutChanged;
            Deck.TargetChanged -= this.OnRepaint;
            AppWatcher.Instance.Changed -= this.OnRepaint;
            this._tick.Change(Timeout.Infinite, Timeout.Infinite);
            return base.Deactivate();
        }

        // ---- layout -------------------------------------------------------------------------

        private List<String> BuildParameters() => this._page != null ? this.BuildPage() : BuildList();

        private static List<String> BuildList()
        {
            var usage = DeckConfig.UsageRow;
            var perPage = usage ? TilesPerPage - 3 : TilesPerPage;

            // Each chunk is one keypad page: a run of sessions that belong together.
            var chunks = new List<(String Id, List<SessionInfo> Sessions)>();
            if (DeckConfig.SessionGrouping == "tab")
            {
                foreach (var g in Store.Groups)
                {
                    for (var i = 0; i < Math.Max(1, g.Sessions.Count); i += perPage)
                    {
                        chunks.Add(($"{g.Id}.{i}", g.Sessions.Skip(i).Take(perPage).ToList()));
                    }
                }
            }
            else
            {
                var all = Store.All;
                for (var i = 0; i < all.Count; i += perPage)
                {
                    chunks.Add(($"flat.{i}", all.Skip(i).Take(perPage).ToList()));
                }
            }

            var list = new List<String>();
            if (chunks.Count == 0)
            {
                // Nothing running is no reason to hide how much of the plan is left.
                list.Add(Notice);
                if (usage)
                {
                    list.AddRange(Enumerable.Range(1, perPage - 1).Select(i => $"x:none:{i}"));
                    list.AddRange(Enumerable.Range(0, 3).Select(i => $"u:{i}:none"));
                }

                return list;
            }

            for (var c = 0; c < chunks.Count; c++)
            {
                var (id, sessions) = chunks[c];
                list.AddRange(sessions.Select(s => $"s:{s.Key}"));

                // Pages before the last are padded so the usage row (or the next group) starts a new
                // one; the last page of a plain eight-up list is left short.
                if (usage || (DeckConfig.SessionGrouping == "tab" && c < chunks.Count - 1))
                {
                    list.AddRange(Enumerable.Range(sessions.Count, perPage - sessions.Count).Select(i => $"x:{id}:{i}"));
                }

                if (usage)
                {
                    list.AddRange(Enumerable.Range(0, 3).Select(i => $"u:{i}:{id}"));
                }
            }

            return list;
        }

        private IReadOnlyList<AnswerKey> PageAnswers() => Deck.AnswersFor(this.Page, TilesPerPage - 2);

        private List<String> BuildPage()
        {
            var answers = this.PageAnswers();
            this._answerCount = answers.Count;
            this._limited = this.Page?.IsLimited == true;

            var list = new List<String> { "p:back", "p:tile" };
            list.AddRange(answers.Select((_, i) => $"p:ans:{i}"));

            // Out of usage: the one thing worth doing about it, offered where the answers would be.
            if (this.Page?.IsLimited == true)
            {
                list.Add("p:lowpri");
            }
            list.AddRange(new[] { "p:info", "p:model", "p:effort", "p:mode" });
            list.AddRange(DeckConfig.Keys.Select((_, i) => $"p:k:{i}"));
            return list;
        }

        public override IEnumerable<String> GetButtonPressActionNames(DeviceType deviceType) =>
            this.BuildParameters().Select(p => this.CreateCommandName(p)).ToList();

        private void Show(String sessionKey)
        {
            this._page = sessionKey;
            this.ButtonActionNamesChanged();
            this.RepaintAll();
        }

        // ---- change handling ----------------------------------------------------------------

        private void OnChanged(Object sender, EventArgs e)
        {
            if (this._page != null)
            {
                // The session ended: there is no page to show any more.
                if (this.Page == null)
                {
                    this.Show(null);
                    return;
                }

                // Answers arriving or leaving change which keys exist, not just what they say.
                if (this.PageAnswers().Count != this._answerCount || this.Page.IsLimited != this._limited)
                {
                    this.ButtonActionNamesChanged();
                }
            }
            else if (e is LayoutChangedEventArgs)
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

        private void Repaint(String parameter)
        {
            if (this._open)
            {
                this.CommandImageChanged(parameter);
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

        // Only what is actually moving is repainted each tick; everything else gets a slow refresh
        // so "done 4:59" still becomes "done 5:00".
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

            if (this._page != null)
            {
                var s = this.Page;
                if (s != null && TileRenderer.Animates(s))
                {
                    this.CommandImageChanged("p:tile");
                }

                // Somebody else is blocked: the way back blinks until they are not.
                if (this.Others.Any(o => o.State == "attention"))
                {
                    this.CommandImageChanged("p:back");
                }

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

        private void Flash(String actionParameter)
        {
            this._flash = actionParameter;
            this._flashUntil = this._frame + FlashFrames;
            this.CommandImageChanged(actionParameter);
        }

        // ---- presses ------------------------------------------------------------------------

        // Holding a session - its tile in the list, or the tile on its page - interrupts it.
        public override Boolean ProcessButtonEvent2(String actionParameter, DeviceButtonEvent2 buttonEvent)
        {
            // Holding the way back skips the list and goes straight to whoever needs you most.
            if (actionParameter == "p:back")
            {
                switch (buttonEvent.EventType)
                {
                    case DeviceButtonEventType.LongPress:
                        this._held = actionParameter;
                        var next = this.Neediest();
                        if (next != null)
                        {
                            if (DeckConfig.FocusOnOpen)
                            {
                                Deck.Focus(next);
                            }

                            this.Show(next.Key);
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

            var session = this.SessionFor(actionParameter);
            if (session == null)
            {
                return false;
            }

            switch (buttonEvent.EventType)
            {
                case DeviceButtonEventType.LongPress:
                    this._held = actionParameter;
                    this.Flash(actionParameter);
                    if (Deck.Interrupt(session))
                    {
                        PluginLog.Info($"long press interrupted {session.Project}");
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

        private SessionInfo SessionFor(String actionParameter)
        {
            if (actionParameter == null)
            {
                return null;
            }

            if (actionParameter == "p:tile")
            {
                return this.Page;
            }

            return actionParameter.StartsWith("s:", StringComparison.Ordinal)
                ? Store.Find(actionParameter.Substring(2))
                : null;
        }

        public override void RunCommand(String actionParameter)
        {
            if (actionParameter == null)
            {
                return;
            }

            // List: open the session's page.
            if (actionParameter.StartsWith("s:", StringComparison.Ordinal))
            {
                var s = Store.Find(actionParameter.Substring(2));
                if (s == null)
                {
                    return;
                }

                if (DeckConfig.FocusOnOpen)
                {
                    Deck.Focus(s);
                }

                this.Show(s.Key);
                return;
            }

            if (!actionParameter.StartsWith("p:", StringComparison.Ordinal))
            {
                return;
            }

            var page = this.Page;
            this.Flash(actionParameter);
            switch (actionParameter)
            {
                case "p:back":
                    this.Show(null);
                    return;
                case "p:tile":
                case "p:info":
                    Deck.Focus(page);
                    return;
                case "p:lowpri":
                    Deck.RunSlash(page, "/low-priority");
                    return;
                case "p:model":
                    this._model.Tap(page);
                    return;
                case "p:effort":
                    this._effort.Tap(page);
                    return;
                case "p:mode":
                    this._mode.Tap(page);
                    return;
            }

            if (Index(actionParameter, "p:ans:") is { } a)
            {
                var answers = this.PageAnswers();
                if (a < answers.Count)
                {
                    Deck.Respond(page, answers[a]);
                }

                return;
            }

            if (Index(actionParameter, "p:k:") is { } k && k < DeckConfig.Keys.Count && page != null)
            {
                // A page's keys are about THAT session, so it is brought forward whatever is in front.
                Deck.Focus(page);
                Thread.Sleep(350);
                Deck.Send(DeckConfig.Keys[k], bringForward: true);
            }
        }

        private static Int32? Index(String actionParameter, String prefix) =>
            actionParameter.StartsWith(prefix, StringComparison.Ordinal)
            && Int32.TryParse(actionParameter.Substring(prefix.Length), out var i) && i >= 0
                ? i
                : null;

        // ---- drawing ------------------------------------------------------------------------

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

            if (actionParameter.StartsWith("u:", StringComparison.Ordinal))
            {
                return TileRenderer.UsageKey(actionParameter.Length > 2 ? actionParameter[2] - '0' : 0, imageSize);
            }

            if (actionParameter == Notice)
            {
                return HookStatus.IsWired
                    ? TileRenderer.Message("No sessions", "start claude in a terminal", imageSize)
                    : TileRenderer.Message("Not set up", "run ./install.sh", imageSize);
            }

            if (!actionParameter.StartsWith("p:", StringComparison.Ordinal))
            {
                return TileRenderer.Blank(imageSize);
            }

            var page = this.Page;
            if (actionParameter == "p:back")
            {
                return TileRenderer.Back(this.Others, flash, imageSize, this._frame);
            }

            if (page == null)
            {
                return TileRenderer.Blank(imageSize);
            }

            switch (actionParameter)
            {
                case "p:tile":
                    return TileRenderer.Session(page, Deck.Target?.Key == page.Key, flash, imageSize, this._frame);
                case "p:info":
                    return TileRenderer.Info(page, imageSize);
                case "p:lowpri":
                    return TileRenderer.Command("continue at low priority", "amber", flash, imageSize);
                case "p:model":
                    return this._model.Render(page, false, imageSize);
                case "p:effort":
                    return this._effort.Render(page, false, imageSize);
                case "p:mode":
                    return this._mode.Render(page, false, imageSize);
            }

            if (Index(actionParameter, "p:ans:") is { } a)
            {
                var answers = this.PageAnswers();
                return a < answers.Count
                    ? TileRenderer.Command(answers[a].Label, answers[a].Color, flash, imageSize)
                    : TileRenderer.Blank(imageSize);
            }

            if (Index(actionParameter, "p:k:") is { } k && k < DeckConfig.Keys.Count)
            {
                return TileRenderer.Command(DeckConfig.Keys[k].Label, DeckConfig.Keys[k].Color, flash, imageSize);
            }

            return TileRenderer.Blank(imageSize);
        }

        public override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) => "";
    }
}
