namespace Loupedeck.ClaudeDeckPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    // The explicit version of the Model key: every configured model as a key, the one in use marked.
    // Press one and it is sent to the target session, and the folder closes.
    public class ModelsFolder : PluginDynamicFolder
    {
        private volatile Boolean _open;
        private volatile String _flash;

        public ModelsFolder()
        {
            this.DisplayName = "Models";
            this.GroupName = "Claude";
        }

        public override PluginDynamicFolderNavigation GetNavigationArea(DeviceType deviceType) =>
            PluginDynamicFolderNavigation.ButtonArea;

        public override Boolean Activate()
        {
            this._open = true;
            SessionStore.Instance.Changed += this.OnChanged;
            DeckConfig.Changed += this.OnConfigChanged;
            return base.Activate();
        }

        public override Boolean Deactivate()
        {
            this._open = false;
            SessionStore.Instance.Changed -= this.OnChanged;
            DeckConfig.Changed -= this.OnConfigChanged;
            return base.Deactivate();
        }

        private void OnConfigChanged(Object sender, EventArgs e)
        {
            this.ButtonActionNamesChanged();
            this.OnChanged(sender, e);
        }

        private void OnChanged(Object sender, EventArgs e)
        {
            if (!this._open)
            {
                return;
            }

            this.CommandImageChanged("who");
            for (var i = 0; i < DeckConfig.Models.Count; i++)
            {
                this.CommandImageChanged($"m:{i}");
            }
        }

        // First key says whose model this is; the rest are the choices.
        public override IEnumerable<String> GetButtonPressActionNames(DeviceType deviceType) =>
            new[] { "who" }.Concat(DeckConfig.Models.Select((_, i) => $"m:{i}")).Select(p => this.CreateCommandName(p)).ToList();

        private static ModelDef Lookup(String actionParameter) =>
            actionParameter != null && actionParameter.StartsWith("m:", StringComparison.Ordinal)
            && Int32.TryParse(actionParameter.Substring(2), out var i) && i >= 0 && i < DeckConfig.Models.Count
                ? DeckConfig.Models[i]
                : null;

        public override void RunCommand(String actionParameter)
        {
            var session = Deck.ModelTarget;
            if (actionParameter == "who")
            {
                Deck.Focus(session);
                return;
            }

            var model = Lookup(actionParameter);
            if (model == null || session == null)
            {
                return;
            }

            this._flash = actionParameter;
            this.CommandImageChanged(actionParameter);
            var sent = model.Is(session.Selected) || Deck.SwitchModel(session, model);
            System.Threading.Tasks.Task.Delay(300).ContinueWith(_ =>
            {
                this._flash = null;
                this.CommandImageChanged(actionParameter);
                if (sent)
                {
                    try
                    {
                        this.Close();
                    }
                    catch (Exception ex)
                    {
                        PluginLog.Warning($"could not close the models folder: {ex.Message}");
                    }
                }
            });
        }

        public override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            var session = Deck.ModelTarget;
            if (actionParameter == "who")
            {
                if (session == null)
                {
                    return TileRenderer.Message("No session", "press a session tile first", imageSize);
                }

                var note = session.State is "busy" or "attention" ? $"{session.State} - wait" : "press to go there";
                return TileRenderer.Message(session.Project, note, imageSize);
            }

            var model = Lookup(actionParameter);
            return model == null
                ? TileRenderer.Blank(imageSize)
                : TileRenderer.ModelChoice(model, session != null && model.Is(session.Selected), this._flash == actionParameter, imageSize);
        }

        public override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) => "";
    }
}
