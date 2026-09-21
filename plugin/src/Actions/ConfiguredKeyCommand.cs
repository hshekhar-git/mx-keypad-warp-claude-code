namespace Loupedeck.ClaudeDeckPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;

    // Puts every key from config.json into Options+ (under "Commands") so it can also sit on the
    // main page. A key placed there only ever types into a terminal that is already in front; it
    // does not bring one forward, because on the main page you may be looking at anything.
    //
    // What Options+ remembers about a placed key is its parameter name, so that name has to mean
    // "this key" for as long as the key does. It is the key's "id" from the config when it has one;
    // otherwise it is derived from what the key DOES - the text it types, whether it submits, the
    // special key it sends. Relabelling or reordering keys therefore changes nothing, and two keys
    // that do the same thing are, reasonably, the same key.
    public class ConfiguredKeyCommand : PluginDynamicCommand
    {
        private volatile IReadOnlyDictionary<String, KeyDef> _keys = new Dictionary<String, KeyDef>();

        public ConfiguredKeyCommand()
            : base((DeviceType)DeviceTypeAliases.MxCreativeKeypad) => this.IsWidget = true;

        protected override Boolean OnLoad()
        {
            DeckConfig.Changed += this.OnConfigChanged;
            this.Republish();
            return true;
        }

        protected override Boolean OnUnload()
        {
            DeckConfig.Changed -= this.OnConfigChanged;
            return true;
        }

        private void OnConfigChanged(Object sender, EventArgs e) => this.Republish();

        private static String Identity(KeyDef key)
        {
            if (key.Id.Length > 0)
            {
                return "id-" + new String(key.Id.Where(c => Char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
            }

            var does = $"{key.Key.ToLowerInvariant()}\n{key.Text}\n{key.Submit}";
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(does));
            return "key-" + Convert.ToHexString(digest, 0, 6).ToLowerInvariant();
        }

        private void Republish()
        {
            var keys = new Dictionary<String, KeyDef>(StringComparer.Ordinal);
            foreach (var key in DeckConfig.Keys)
            {
                keys.TryAdd(Identity(key), key);
            }

            this.RemoveAllParameters();
            foreach (var (name, key) in keys)
            {
                this.AddParameter(name, key.Label, "Commands");
            }

            this._keys = keys;
            this.ParametersChanged();
        }

        private KeyDef Find(String actionParameter) =>
            actionParameter != null && this._keys.TryGetValue(actionParameter, out var key) ? key : null;

        protected override void RunCommand(String actionParameter) => Deck.Send(this.Find(actionParameter), bringForward: false);

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            var key = this.Find(actionParameter);
            return key == null ? TileRenderer.Dark(imageSize) : TileRenderer.Command(key.Label, key.Color, false, imageSize);
        }

        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) => "";
    }
}
