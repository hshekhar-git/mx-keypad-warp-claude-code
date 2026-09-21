namespace Loupedeck.ClaudeDeckPlugin
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;

    public sealed class KeyDef
    {
        // Optional and yours to choose: a stable name for this key, so that Options+ keeps a placed
        // copy of it bound even if you later change what it types.
        public String Id { get; init; } = "";

        public String Label { get; init; } = "";
        public String Text { get; init; } = "";
        public Boolean Submit { get; init; }

        // "escape" interrupts instead of typing.
        public String Key { get; init; } = "";

        // Optional tile colour: "green", "amber", "red", or empty for neutral.
        public String Color { get; init; } = "";

        // "key": "escape" (or "esc") sends the Escape key instead of typing anything.
        public Boolean IsEscape => this.Key.Trim().ToLowerInvariant() is "escape" or "esc";
    }

    public sealed class ModelDef
    {
        // What the key shows.
        public String Label { get; init; } = "";

        // What is typed after "/model ": an alias (opus) or a full id (claude-opus-5[1m]).
        public String Alias { get; init; } = "";

        // Matched, case-insensitively, against the session's model name to tell which is current.
        public String Match { get; init; } = "";

        public String Color { get; init; } = "";

        public Boolean Is(ModelName current) =>
            current != null && current.IsKnown
            && current.Name.Contains(this.Match.Length > 0 ? this.Match : this.Alias, StringComparison.OrdinalIgnoreCase);
    }

    // ~/.claude/deck/config.json, re-read within a second of being saved. A file that fails to parse
    // keeps the previous settings rather than resetting the deck mid-edit.
    public static class DeckConfig
    {
        // A session's page flows onto further pages, so this is a sanity limit rather than a layout one.
        public const Int32 MaxKeys = 12;

        private static readonly Object Gate = new();
        private static Timer _poll;
        private static DateTime _stamp = DateTime.MinValue;
        private static Snapshot _current = Snapshot.Default();

        public static event EventHandler Changed;

        public static String Root =>
            Environment.GetEnvironmentVariable("CLAUDE_DECK_ROOT") is { Length: > 0 } custom
                ? custom
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "deck");

        public static String SessionsDir => Path.Combine(Root, "sessions");

        public static String ConfigPath => Path.Combine(Root, "config.json");

        // "title" (Claude's own session title, then the prompt), "prompt", or "slug".
        public static String Label => _current.Label;

        public static IReadOnlyList<KeyDef> Keys => _current.Keys;

        public static Boolean ShowContext => _current.ShowContext;

        // "ascii": character-frame animation and a little ASCII art. "plain": neither.
        public static Boolean Ascii => _current.Style != "plain";

        public static Boolean HapticAttention => _current.HapticAttention;

        public static Boolean HapticDone => _current.HapticDone;

        public static Boolean HapticError => _current.HapticError;

        // A turn shorter than this finishing is not worth a buzz: you are still looking at it.
        public static Int32 HapticMinTurnSeconds => _current.HapticMinTurnSeconds;

        // App switcher: bundle ids that always come first, in this order, so they never move.
        public static IReadOnlyList<String> PinnedApps => _current.PinnedApps;

        public static IReadOnlyList<String> HiddenApps => _current.HiddenApps;

        // "recent" (like Cmd-Tab), "launch" or "name" - for everything that is not pinned.
        public static String AppOrder => _current.AppOrder;

        // A switcher is a picker: once you have picked, it gets out of the way.
        public static Boolean CloseOnSwitch => _current.CloseOnSwitch;

        // "flat": every session in one list, eight to a page. "tab": one page per Warp tab.
        public static String SessionGrouping => _current.SessionGrouping;

        // The list keeps its bottom row for plan usage, leaving five session tiles a page.
        public static Boolean UsageRow => _current.UsageRow;

        // Opening a session's page also brings its pane to the front.
        public static Boolean FocusOnOpen => _current.FocusOnOpen;

        // The models the Model key steps through and the Models folder lists.
        public static IReadOnlyList<ModelDef> Models => _current.Models;

        public static Int32 ContextWindowFor(String model, Int64 observed)
        {
            var map = _current.ContextWindows;
            if (!String.IsNullOrEmpty(model))
            {
                foreach (var pair in map)
                {
                    if (model.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        return pair.Value;
                    }
                }
            }

            // Nothing configured: a session already past 200k can only be on a 1M window.
            return observed > 200_000 ? 1_000_000 : _current.DefaultContextWindow;
        }

        public static void Start()
        {
            lock (Gate)
            {
                Load();
                _poll ??= new Timer(_ => Poll(), null, 1000, 1000);
            }
        }

        public static void Shutdown()
        {
            lock (Gate)
            {
                _poll?.Dispose();
                _poll = null;
                Changed = null;
            }
        }

        private static void Poll()
        {
            try
            {
                var stamp = File.Exists(ConfigPath) ? File.GetLastWriteTimeUtc(ConfigPath) : DateTime.MinValue;
                if (stamp == _stamp)
                {
                    return;
                }

                lock (Gate)
                {
                    Load();
                }

                Changed?.Invoke(null, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                PluginLog.Warning($"config poll failed: {ex.Message}");
            }
        }

        private static void Load()
        {
            var path = ConfigPath;
            if (!File.Exists(path))
            {
                _stamp = DateTime.MinValue;
                _current = Snapshot.Default();
                return;
            }

            _stamp = File.GetLastWriteTimeUtc(path);
            try
            {
                var options = new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                };
                using var doc = JsonDocument.Parse(File.ReadAllText(path), options);
                _current = Snapshot.From(doc.RootElement);
                PluginLog.Info($"config loaded: label={_current.Label}, {_current.Keys.Count} command key(s)");
            }
            catch (Exception ex)
            {
                PluginLog.Warning($"config.json did not parse, keeping previous settings: {ex.Message}");
            }
        }

        private sealed class Snapshot
        {
            public String Label { get; private set; } = "title";
            public IReadOnlyList<KeyDef> Keys { get; private set; } = DefaultKeys();
            public Boolean ShowContext { get; private set; } = true;
            public String Style { get; private set; } = "ascii";
            public Boolean HapticAttention { get; private set; } = true;
            public Boolean HapticDone { get; private set; } = true;
            public Boolean HapticError { get; private set; } = true;
            public Int32 HapticMinTurnSeconds { get; private set; } = 20;
            public Int32 DefaultContextWindow { get; private set; } = 200_000;
            public Dictionary<String, Int32> ContextWindows { get; private set; } = new();
            public IReadOnlyList<String> PinnedApps { get; private set; } = new[] { "dev.warp.Warp-Stable" };
            public IReadOnlyList<String> HiddenApps { get; private set; } = Array.Empty<String>();
            public String AppOrder { get; private set; } = "recent";
            public Boolean CloseOnSwitch { get; private set; } = true;
            public String SessionGrouping { get; private set; } = "flat";
            public Boolean FocusOnOpen { get; private set; } = true;
            public Boolean UsageRow { get; private set; } = true;
            public IReadOnlyList<ModelDef> Models { get; private set; } = new List<ModelDef>
            {
                new() { Label = "Fable", Alias = "fable", Color = "violet" },
                new() { Label = "Opus", Alias = "opus", Color = "coral" },
                new() { Label = "Sonnet", Alias = "sonnet", Color = "blue" },
                new() { Label = "Haiku", Alias = "haiku", Color = "green" },
            };

            public static Snapshot Default() => new();

            private static List<KeyDef> DefaultKeys() => new()
            {
                new KeyDef { Label = "esc", Key = "escape" },
                new KeyDef { Label = "/clear", Text = "/clear", Submit = true },
                new KeyDef { Label = "/compact", Text = "/compact ", Submit = false },
            };

            public static Snapshot From(JsonElement root)
            {
                var s = new Snapshot();
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return s;
                }

                if (root.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.String)
                {
                    var v = label.GetString()?.Trim().ToLowerInvariant();
                    if (v is "title" or "prompt" or "slug")
                    {
                        s.Label = v;
                    }
                }

                if (Str(root, "style").ToLowerInvariant() is "ascii" or "plain")
                {
                    s.Style = Str(root, "style").ToLowerInvariant();
                }

                if (root.TryGetProperty("showContext", out var ctx) && ctx.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    s.ShowContext = ctx.GetBoolean();
                }

                if (root.TryGetProperty("contextWindow", out var win) && win.TryGetInt32(out var w) && w > 1000)
                {
                    s.DefaultContextWindow = w;
                }

                if (root.TryGetProperty("contextWindows", out var wins) && wins.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in wins.EnumerateObject())
                    {
                        if (p.Value.TryGetInt32(out var size) && size > 1000)
                        {
                            s.ContextWindows[p.Name] = size;
                        }
                    }
                }

                if (root.TryGetProperty("haptics", out var h) && h.ValueKind == JsonValueKind.Object)
                {
                    s.HapticAttention = Bool(h, "attention", true);
                    s.HapticDone = Bool(h, "done", true);
                    s.HapticError = Bool(h, "error", true);
                    if (h.TryGetProperty("minTurnSeconds", out var m) && m.TryGetInt32(out var secs) && secs >= 0)
                    {
                        s.HapticMinTurnSeconds = secs;
                    }
                }

                if (root.TryGetProperty("apps", out var apps) && apps.ValueKind == JsonValueKind.Object)
                {
                    s.CloseOnSwitch = Bool(apps, "closeOnSwitch", true);
                    if (Str(apps, "order").ToLowerInvariant() is "recent" or "launch" or "name")
                    {
                        s.AppOrder = Str(apps, "order").ToLowerInvariant();
                    }

                    if (apps.TryGetProperty("pinned", out var pinned) && pinned.ValueKind == JsonValueKind.Array)
                    {
                        s.PinnedApps = Strings(pinned);
                    }

                    if (apps.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.Array)
                    {
                        s.HiddenApps = Strings(hidden);
                    }
                }

                if (root.TryGetProperty("sessions", out var sess) && sess.ValueKind == JsonValueKind.Object)
                {
                    s.FocusOnOpen = Bool(sess, "focusOnOpen", true);
                    s.UsageRow = Bool(sess, "usageRow", true);
                    if (Str(sess, "group").ToLowerInvariant() is "flat" or "tab")
                    {
                        s.SessionGrouping = Str(sess, "group").ToLowerInvariant();
                    }
                }

                if (root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<ModelDef>();
                    foreach (var m in models.EnumerateArray())
                    {
                        // Typed into a terminal, so an alias is held to what a model name looks like.
                        var alias = m.ValueKind == JsonValueKind.Object ? Str(m, "alias") : "";
                        if (alias.Length == 0 || alias.Length > 80
                            || !System.Text.RegularExpressions.Regex.IsMatch(alias, @"^[A-Za-z0-9][A-Za-z0-9._\-\[\]]*$"))
                        {
                            continue;
                        }

                        list.Add(new ModelDef
                        {
                            Alias = alias,
                            Label = Str(m, "label") is { Length: > 0 } l ? l : alias,
                            Match = Str(m, "match"),
                            Color = Str(m, "color"),
                        });
                    }

                    if (list.Count > 0)
                    {
                        s.Models = list.Take(8).ToList();
                    }
                }

                // Absent keeps the built-in row; an empty array means "no command row at all".
                if (root.TryGetProperty("keys", out var keys) && keys.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<KeyDef>();
                    foreach (var k in keys.EnumerateArray())
                    {
                        if (k.ValueKind != JsonValueKind.Object || list.Count >= MaxKeys)
                        {
                            continue;
                        }

                        var text = Str(k, "text");
                        var key = Str(k, "key");
                        var submit = Bool(k, "submit", false);
                        if (text.Length == 0 && key.Length == 0 && !submit)
                        {
                            continue;
                        }

                        var lbl = Str(k, "label");
                        if (lbl.Length == 0)
                        {
                            lbl = text.Trim().Length > 0 ? text.Trim() : (key.Length > 0 ? key : "return");
                        }

                        list.Add(new KeyDef { Id = Str(k, "id"), Label = lbl, Text = text, Key = key, Submit = submit, Color = Str(k, "color") });
                    }

                    s.Keys = list;
                }

                return s;
            }

            private static List<String> Strings(JsonElement array)
            {
                var list = new List<String>();
                foreach (var item in array.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } v)
                    {
                        list.Add(v);
                    }
                }

                return list;
            }

            private static String Str(JsonElement e, String name) =>
                e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

            private static Boolean Bool(JsonElement e, String name, Boolean fallback) =>
                e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? v.GetBoolean()
                    : fallback;
        }
    }
}
