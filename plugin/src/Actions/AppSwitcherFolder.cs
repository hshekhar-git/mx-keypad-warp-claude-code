namespace Loupedeck.ClaudeDeckPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;

    // A Cmd-Tab you can see: every running app as a key with its icon. Press one and it comes to the
    // front; hold one and it hides.
    //
    // Pinned apps come first and never move, because a physical key you have learned is worth more
    // than a perfectly sorted list. Everything else is most-recently-used - and that order is frozen
    // for as long as the folder is open, so tiles do not shuffle under your finger when the app you
    // just picked becomes the most recent.
    public class AppSwitcherFolder : PluginDynamicFolder
    {
        private const Int32 FlashMs = 450;

        private volatile Boolean _open;
        private volatile List<String> _frozenOrder;
        private volatile String _flash;
        private volatile String _held;
        private Timer _unflash;

        public AppSwitcherFolder()
        {
            this.DisplayName = "App Switcher";
            this.GroupName = "Apps";
        }

        private static AppWatcher Watcher => AppWatcher.Instance;

        public override PluginDynamicFolderNavigation GetNavigationArea(DeviceType deviceType) =>
            PluginDynamicFolderNavigation.ButtonArea;

        public override Boolean Activate()
        {
            this._open = true;
            this._frozenOrder = Order(Watcher.Apps, Watcher.Recent);
            Watcher.Changed += this.OnAppsChanged;
            SessionStore.Instance.Changed += this.OnSessionsChanged;
            return base.Activate();
        }

        public override Boolean Deactivate()
        {
            this._open = false;
            this._frozenOrder = null;
            Watcher.Changed -= this.OnAppsChanged;
            SessionStore.Instance.Changed -= this.OnSessionsChanged;
            return base.Deactivate();
        }

        // Pinned first, then by the configured order.
        private static List<String> Order(IReadOnlyList<AppInfo> apps, IReadOnlyList<String> recent)
        {
            var hidden = new HashSet<String>(DeckConfig.HiddenApps, StringComparer.OrdinalIgnoreCase);
            var visible = apps.Where(a => !hidden.Contains(a.Bundle)).ToList();
            var pinned = DeckConfig.PinnedApps
                .Select(id => visible.FirstOrDefault(a => String.Equals(a.Bundle, id, StringComparison.OrdinalIgnoreCase)))
                .Where(a => a != null)
                .ToList();
            var rest = visible.Except(pinned);

            rest = DeckConfig.AppOrder switch
            {
                "name" => rest.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
                "launch" => rest.OrderBy(a => a.Launched),
                _ => rest.OrderBy(a =>
                {
                    var i = -1;
                    for (var n = 0; n < recent.Count; n++)
                    {
                        if (recent[n] == a.Id)
                        {
                            i = n;
                            break;
                        }
                    }

                    // Never in front since we started watching: after everything that has been.
                    return i < 0 ? Int32.MaxValue : i;
                }).ThenByDescending(a => a.Launched),
            };

            return pinned.Concat(rest).Select(a => a.Id).ToList();
        }

        // The frozen order, minus apps that have quit, plus apps that have launched since.
        private List<String> CurrentIds()
        {
            var live = Order(Watcher.Apps, Watcher.Recent);
            var frozen = this._frozenOrder;
            if (frozen == null)
            {
                return live;
            }

            var running = new HashSet<String>(live, StringComparer.Ordinal);
            var kept = frozen.Where(running.Contains).ToList();
            kept.AddRange(live.Where(id => !kept.Contains(id)));
            return kept;
        }

        private void OnAppsChanged(Object sender, EventArgs e)
        {
            if (!this._open)
            {
                return;
            }

            // Cheap to ask for even when nothing structural changed: the host diffs the names.
            this.ButtonActionNamesChanged();
            this.RepaintAll();
        }

        private void OnSessionsChanged(Object sender, EventArgs e) => this.RepaintAll();

        private void RepaintAll()
        {
            if (!this._open)
            {
                return;
            }

            foreach (var id in this.CurrentIds())
            {
                this.CommandImageChanged($"a:{id}");
            }
        }

        public override IEnumerable<String> GetButtonPressActionNames(DeviceType deviceType)
        {
            var ids = this.CurrentIds();
            if (ids.Count == 0)
            {
                return new[] { this.CreateCommandName("none") };
            }

            return ids.Select(id => this.CreateCommandName($"a:{id}")).ToList();
        }

        public override Boolean ProcessButtonEvent2(String actionParameter, DeviceButtonEvent2 buttonEvent)
        {
            if (actionParameter == null || !actionParameter.StartsWith("a:", StringComparison.Ordinal))
            {
                return false;
            }

            switch (buttonEvent.EventType)
            {
                case DeviceButtonEventType.LongPress:
                    this._held = actionParameter;
                    var app = Watcher.Find(actionParameter.Substring(2));
                    if (app != null && Watcher.Hide(app))
                    {
                        PluginLog.Info($"hid {app.Name}");
                        this.Flash(actionParameter);
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
            if (actionParameter == null || !actionParameter.StartsWith("a:", StringComparison.Ordinal))
            {
                return;
            }

            var app = Watcher.Find(actionParameter.Substring(2));
            if (app == null)
            {
                return;
            }

            this.Flash(actionParameter);
            if (Apps.Activate(app) && DeckConfig.CloseOnSwitch)
            {
                // Long enough to see the flash, short enough to feel like one gesture.
                System.Threading.Tasks.Task.Delay(220).ContinueWith(_ =>
                {
                    try
                    {
                        this.Close();
                    }
                    catch (Exception ex)
                    {
                        PluginLog.Warning($"could not close the switcher: {ex.Message}");
                    }
                });
            }
        }

        private void Flash(String actionParameter)
        {
            this._flash = actionParameter;
            this.CommandImageChanged(actionParameter);
            this._unflash?.Dispose();
            this._unflash = new Timer(_ =>
            {
                this._flash = null;
                this.CommandImageChanged(actionParameter);
            }, null, FlashMs, Timeout.Infinite);
        }

        public override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            if (actionParameter == null || !actionParameter.StartsWith("a:", StringComparison.Ordinal))
            {
                return TileRenderer.Message("No apps", "the app helper is not running", imageSize);
            }

            var app = Watcher.Find(actionParameter.Substring(2));
            if (app == null)
            {
                return TileRenderer.Dark(imageSize);
            }

            var (state, count) = Apps.Badge(app.Bundle);
            return TileRenderer.App(app, Watcher.Front == app.Bundle, state, count, this._flash == actionParameter, imageSize);
        }

        public override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) => "";
    }
}
