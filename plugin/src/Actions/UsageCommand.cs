namespace Loupedeck.ClaudeDeckPlugin
{
    using System;
    using System.Threading;

    // The usage row's three keys, for the main page: how much of the five-hour session window and of
    // the week is gone, and whether the session window lasts until its reset at the rate you are going.
    public class UsageCommand : PluginDynamicCommand
    {
        private readonly Timer _minute;

        public UsageCommand()
            : base((DeviceType)DeviceTypeAliases.MxCreativeKeypad)
        {
            this.IsWidget = true;
            this.AddParameter("0", "Usage: session (5h)", "Usage");
            this.AddParameter("1", "Usage: weekly", "Usage");
            this.AddParameter("2", "Usage: pace / model", "Usage");

            // The numbers change when Claude Code reports; the countdowns change on their own.
            this._minute = new Timer(_ => this.ActionImageChanged(), null, Timeout.Infinite, Timeout.Infinite);
        }

        protected override Boolean OnLoad()
        {
            UsageStore.Changed += (_, _) => this.ActionImageChanged();
            this._minute.Change(60000, 60000);
            return true;
        }

        protected override Boolean OnUnload()
        {
            this._minute.Change(Timeout.Infinite, Timeout.Infinite);
            return true;
        }

        protected override void RunCommand(String actionParameter)
        {
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize) =>
            TileRenderer.UsageKey(Int32.TryParse(actionParameter, out var i) ? i : 0, imageSize);

        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) => "";
    }
}
