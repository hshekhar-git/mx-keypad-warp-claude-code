namespace Loupedeck.ClaudeDeckPlugin
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Text.RegularExpressions;

    public sealed class ModelName
    {
        public static readonly ModelName Unknown = new();

        // "Opus 5", "Fable 5.1" - what a person calls it.
        public String Name { get; init; } = "";

        // On the 1M-token context window.
        public Boolean OneM { get; init; }

        public Boolean IsKnown => this.Name.Length > 0;

        // "F5.1" - small enough for a tile's status line.
        public String Short
        {
            get
            {
                var parts = this.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return parts.Length == 0 ? "" : parts[0].Substring(0, 1) + String.Join("", parts.Skip(1));
            }
        }
    }

    // Two spellings of a model reach us: the API id on every assistant message ("claude-opus-5",
    // "claude-haiku-4-5-20251001"), and the display name Claude Code prints when you run /model
    // ("Opus 5 (1M context)"). Both are reduced to the same thing here.
    public static class ModelNames
    {
        private static readonly Regex Dated = new(@"-\d{8}$", RegexOptions.Compiled);
        private static readonly Regex Ansi = new(@"\u001b\[[0-9;]*m", RegexOptions.Compiled);

        private static DateTime _defaultReadAt = DateTime.MinValue;
        private static String _default = "";

        public static ModelName FromId(String id)
        {
            if (String.IsNullOrWhiteSpace(id) || id.StartsWith("<", StringComparison.Ordinal))
            {
                return ModelName.Unknown;
            }

            var oneM = id.Contains("[1m]", StringComparison.OrdinalIgnoreCase);
            var bare = id;
            var bracket = bare.IndexOf('[');
            if (bracket >= 0)
            {
                bare = bare.Substring(0, bracket);
            }

            bare = Dated.Replace(bare.Trim(), "");
            if (bare.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
            {
                bare = bare.Substring(7);
            }

            var parts = bare.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return ModelName.Unknown;
            }

            var family = Char.ToUpperInvariant(parts[0][0]) + parts[0].Substring(1);
            var version = String.Join(".", parts.Skip(1).Where(p => p.All(Char.IsDigit)));
            return new ModelName { Name = version.Length > 0 ? $"{family} {version}" : family, OneM = oneM };
        }

        public static ModelName FromDisplay(String display)
        {
            display = Ansi.Replace(display ?? "", "").Trim().Trim('`').Trim();
            if (display.Length == 0)
            {
                return ModelName.Unknown;
            }

            var oneM = display.Contains("1M", StringComparison.OrdinalIgnoreCase);
            var paren = display.IndexOf('(');
            if (paren > 0)
            {
                display = display.Substring(0, paren).Trim();
            }

            return new ModelName { Name = display, OneM = oneM };
        }

        // The "model" in ~/.claude/settings.json: what a new session starts on. It is the only place
        // that says whether an unswitched session is on the 1M window, since the API id does not.
        public static String DefaultId
        {
            get
            {
                if (DateTime.UtcNow - _defaultReadAt < TimeSpan.FromSeconds(5))
                {
                    return _default;
                }

                _defaultReadAt = DateTime.UtcNow;
                try
                {
                    var path = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    _default = doc.RootElement.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
                        ? m.GetString() ?? ""
                        : "";
                }
                catch
                {
                    _default = "";
                }

                return _default;
            }
        }

        // What a session is on: the last /model switch if that is the newest word on the subject,
        // else the id of its last reply - borrowing the 1M flag from the default when they agree.
        public static ModelName Resolve(String switchedTo, String lastReplyId)
        {
            if (!String.IsNullOrEmpty(switchedTo))
            {
                return FromDisplay(switchedTo);
            }

            var fromReply = FromId(lastReplyId);
            var fallback = FromId(DefaultId);
            if (!fromReply.IsKnown)
            {
                return fallback;
            }

            return fromReply.Name == fallback.Name && fallback.OneM
                ? new ModelName { Name = fromReply.Name, OneM = true }
                : fromReply;
        }
    }
}
