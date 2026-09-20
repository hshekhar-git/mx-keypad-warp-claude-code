namespace Loupedeck.ClaudeDeckPlugin
{
    using System;

    // One key, one app: switches straight to it (launching it if need be) without going through the
    // switcher. On a terminal it doubles as a status light - the badge shows what your Claude
    // sessions there are up to.
    public class OpenAppCommand : ActionEditorCommand
    {
        private const String AppName = "app";

        private EventHandler _onChanged;

        public OpenAppCommand()
            : base((DeviceType)DeviceTypeAliases.MxCreativeKeypad)
        {
            this.DisplayName = "Open App";
            this.Description = "Switches to one app, showing its icon and a Claude session badge";
            this.GroupName = "Apps";
            this.IsWidget = true;

            this.ActionEditor.AddControlEx(new ActionEditorTextbox(
                AppName, "App", "An app name (Warp) or bundle id (dev.warp.Warp-Stable)."));
        }

        protected override Boolean OnLoad()
        {
            this._onChanged = (_, _) => this.ActionImageChanged();
            AppWatcher.Instance.Changed += this._onChanged;
            SessionStore.Instance.Changed += this._onChanged;
            return true;
        }

        protected override Boolean RunCommand(ActionEditorActionParameters actionParameters) =>
            Apps.Open(actionParameters.GetString(AppName, "Warp"));

        protected override BitmapImage GetCommandImage(ActionEditorActionParameters actionParameters, Int32 imageWidth, Int32 imageHeight)
        {
            var name = actionParameters.GetString(AppName, "Warp").Trim();
            var app = Apps.Resolve(name);
            if (app == null)
            {
                return TileRenderer.AppPlaceholder(name.Length > 0 ? name : "app", imageWidth, imageHeight);
            }

            var (state, count) = Apps.Badge(app.Bundle);
            return TileRenderer.App(app, AppWatcher.Instance.Front == app.Bundle, state, count, false, PluginImageSize.Width116);
        }

        protected override String GetCommandDisplayName(ActionEditorActionParameters actionParameters) => "";
    }
}
