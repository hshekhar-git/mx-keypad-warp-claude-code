namespace Loupedeck.ClaudeDeckPlugin
{
    using System;

    // Home-page keys for a session's settings. Each shows the target session's value and steps
    // through the choices when tapped; see SettingStepper for why a tap is not an immediate switch.
    public abstract class SettingKeyCommand : PluginDynamicCommand
    {
        private readonly SettingStepper _stepper;
        private EventHandler _onChanged;
        private String _drawn = "";

        protected SettingKeyCommand(String displayName, String description, SessionSetting setting)
            : base(displayName, description, "Claude", (DeviceType)DeviceTypeAliases.MxCreativeKeypad)
        {
            this.IsWidget = true;
            this._stepper = new SettingStepper(setting);
            this._stepper.Changed += (_, _) => this.ActionImageChanged();
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

        private void Refresh()
        {
            var s = Deck.ModelTarget;
            var signature = s == null ? "" : $"{s.Key}|{s.Project}|{s.State}|{this._stepper.Setting.CurrentText(s)}|{this._stepper.Setting.Hint(s)}";
            if (signature != this._drawn)
            {
                this._drawn = signature;
                this.ActionImageChanged();
            }
        }

        protected override void RunCommand(String actionParameter) => this._stepper.Tap(Deck.ModelTarget);

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize) =>
            this._stepper.Render(Deck.ModelTarget, true, imageSize);

        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) => "";
    }

    public class ModelCommand : SettingKeyCommand
    {
        public ModelCommand()
            : base("Model", "The target Claude session's model; tap to step to another", new ModelSetting())
        {
        }
    }

    public class EffortCommand : SettingKeyCommand
    {
        public EffortCommand()
            : base("Effort", "The target Claude session's effort level; tap to step to another", new EffortSetting())
        {
        }
    }

    public class PermissionModeCommand : SettingKeyCommand
    {
        public PermissionModeCommand()
            : base("Permission mode", "The target Claude session's permission mode (ask / auto-edit / plan); tap to step", new ModeSetting())
        {
        }
    }
}
