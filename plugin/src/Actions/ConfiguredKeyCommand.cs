namespace Loupedeck.ClaudeDeckPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Text;

    // Publishes every key in config.json as a draggable action under "Commands", so /compact can sit
    // on a home-page key. These never bring a terminal forward - they type only when one is already
    // in front - so a mistimed press cannot land anywhere else.
    public class ConfiguredKeyCommand : PluginDynamicCommand
    {
        private EventHandler _onConfigChanged;
        private volatile IReadOnlyDictionary<String, KeyDef> _published = new Dictionary<String, KeyDef>();

        public ConfiguredKeyCommand()
            : base((DeviceType)DeviceTypeAliases.MxCreativeKeypad)
        {
            this.IsWidget = true;
        }

        protected override Boolean OnLoad()
        {
            this._onConfigChanged = (_, _) => this.Publish();
            DeckConfig.Changed += this._onConfigChanged;
            this.Publish();
            return true;
        }

        protected override Boolean OnUnload()
        {
            DeckConfig.Changed -= this._onConfigChanged;
            return true;
        }

        // Named after the label rather than the position, so reordering the file cannot silently
        // re-point a key you have already placed.
        private void Publish()
        {
            var map = new Dictionary<String, KeyDef>(StringComparer.Ordinal);
            this.RemoveAllParameters();
            foreach (var key in DeckConfig.Keys)
            {
                var name = Slug(key.Label);
                for (var n = 2; map.ContainsKey(name); n++)
                {
                    name = $"{Slug(key.Label)}-{n}";
                }

                map[name] = key;
                this.AddParameter(name, key.Label, "Commands");
            }

            this._published = map;
            this.ParametersChanged();
        }

        protected override void RunCommand(String actionParameter)
        {
            if (actionParameter != null && this._published.TryGetValue(actionParameter, out var key))
            {
                Deck.Send(key, bringForward: false);
            }
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize) =>
            actionParameter != null && this._published.TryGetValue(actionParameter, out var key)
                ? TileRenderer.Command(key.Label, key.Color, false, imageSize)
                : TileRenderer.Blank(imageSize);

        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) => "";

        private static String Slug(String label)
        {
            var sb = new StringBuilder();
            foreach (var c in label ?? "")
            {
                sb.Append(Char.IsLetterOrDigit(c) ? Char.ToLowerInvariant(c) : '-');
            }

            var slug = sb.ToString().Trim('-');
            return slug.Length == 0 ? "key" : slug;
        }
    }
}
