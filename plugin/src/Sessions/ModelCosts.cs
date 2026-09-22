namespace Loupedeck.ClaudeDeckPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text.RegularExpressions;

    // How fast a session burns your plan, as a multiple of Opus 5 at its default effort (high).
    //
    // Two things set the rate: the model's price per token, and how much more it thinks at a higher
    // effort. Both come from Claude Code's own model table (2.1.278) - the price tier of each model,
    // and its effort_cost_index, which is what Claude Code itself uses to say "~1.4x" when you change
    // effort. Plan usage is charged in proportion to price, so the product is the multiple.
    //
    // A model not in the table gets no line, rather than a wrong one.
    public static class ModelCosts
    {
        private sealed class Cost
        {
            public Double Price { get; init; }               // input $/M tokens
            public Dictionary<String, Double> Effort { get; init; }   // level -> index, high = 1
            public String DefaultEffort { get; init; } = "high";
        }

        private static readonly Dictionary<String, Double> None = new();

        // id (without date suffix or [1m]) -> cost
        private static readonly Dictionary<String, Cost> Table = new(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-opus-5"] = new() { Price = 5, Effort = Index(0.67, 0.76, 1, 1.6, 1.7) },
            ["claude-fable-5-1"] = new() { Price = 10, Effort = Index(0.75, 0.86, 1, 1.38, 1.74) },
            ["claude-fable-5"] = new() { Price = 10, Effort = Index(0.6, 0.77, 1, 1.74, 1.91) },
            ["claude-mythos-5-1"] = new() { Price = 10, Effort = Index(0.75, 0.86, 1, 1.38, 1.74) },
            ["claude-mythos-5"] = new() { Price = 10, Effort = None },
            ["claude-sonnet-5"] = new() { Price = 2, Effort = Index(0.47, 0.74, 1, 2.41, 5.59) },
            ["claude-opus-4-8"] = new() { Price = 5, Effort = Index(0.72, 0.9, 1, 1.65, 1.88) },
            ["claude-opus-4-7"] = new() { Price = 5, Effort = None, DefaultEffort = "xhigh" },
            ["claude-opus-4-6"] = new() { Price = 5, Effort = None },
            ["claude-opus-4-5"] = new() { Price = 5, Effort = None },
            ["claude-opus-4-1"] = new() { Price = 15, Effort = None },
            ["claude-opus-4-0"] = new() { Price = 15, Effort = None },
            ["claude-sonnet-4-6"] = new() { Price = 3, Effort = None },
            ["claude-sonnet-4-5"] = new() { Price = 3, Effort = None },
            ["claude-sonnet-4-0"] = new() { Price = 3, Effort = None },
            ["claude-3-7-sonnet"] = new() { Price = 3, Effort = None },
            ["claude-haiku-4-5"] = new() { Price = 1, Effort = None },
            ["claude-3-5-haiku"] = new() { Price = 0.8, Effort = None },
        };

        private const String Baseline = "claude-opus-5";

        private static readonly Regex Dated = new(@"-\d{8}$", RegexOptions.Compiled);
        private static readonly Regex Bracketed = new(@"\[.*?\]", RegexOptions.Compiled);

        private static Dictionary<String, Double> Index(Double low, Double medium, Double high, Double xhigh, Double max) =>
            new() { ["low"] = low, ["medium"] = medium, ["high"] = high, ["xhigh"] = xhigh, ["max"] = max };

        // The multiple for this session at the given effort, or -1 when its model is not known here.
        public static Double Multiple(SessionInfo s, String effort)
        {
            var cost = Find(s);
            if (cost == null)
            {
                return -1;
            }

            var level = (effort ?? "").ToLowerInvariant() switch
            {
                "ultracode" => "xhigh",
                "" or "auto" => cost.DefaultEffort,
                var e => e,
            };
            var index = cost.Effort.TryGetValue(level, out var i) ? i : 1;
            var baseline = Table[Baseline];
            return cost.Price * index / (baseline.Price * baseline.Effort[baseline.DefaultEffort]);
        }

        // "~2.8x opus" - or "1x opus" for the baseline itself.
        public static String Label(SessionInfo s, String effort)
        {
            var m = Multiple(s, effort);
            if (m < 0)
            {
                return "";
            }

            var rounded = Math.Round(m, m < 1 ? 2 : 1);
            var text = Math.Abs(rounded - Math.Round(rounded)) < 0.01
                ? ((Int32)Math.Round(rounded)).ToString(CultureInfo.InvariantCulture)
                : rounded.ToString(m < 1 ? "0.##" : "0.#", CultureInfo.InvariantCulture);
            return Math.Abs(m - 1) < 0.01 ? "1x opus" : $"~{text}x opus";
        }

        private static Cost Find(SessionInfo s)
        {
            if (s == null)
            {
                return null;
            }

            // The API id, as the transcript has it; else rebuilt from the name Claude Code prints.
            var id = Bracketed.Replace(s.Model ?? "", "").Trim();
            id = Dated.Replace(id, "");
            if (id.Length == 0 && s.Selected.IsKnown)
            {
                id = "claude-" + s.Selected.Name.ToLowerInvariant().Replace('.', '-').Replace(' ', '-');
            }

            return Table.TryGetValue(id, out var cost) ? cost : null;
        }
    }
}
