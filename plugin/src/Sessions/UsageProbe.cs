namespace Loupedeck.ClaudeDeckPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;

    // The per-model weekly figure ("Current week (Fable)") is on Claude Code's /usage screen but in
    // nothing it hands to hooks or the status line. So, while a usage key is on show, this asks Claude
    // Code itself: `claude -p /usage` prints the screen as text - no model call, no session saved -
    // and these lines are read out of it:
    //
    //   Current session: 9% used · resets Sep 22 at 5:40am (Asia/Calcutta)
    //   Current week (all models): 12% used · resets Sep 23 at 9:30pm (Asia/Calcutta)
    //   Current week (Fable): 24% used · resets Sep 23 at 9:30pm (Asia/Calcutta)
    //
    // Claude Code signs in and talks to its own server, exactly as when you type /usage. The plugin
    // still reads no credentials and opens no connection of its own.
    public static class UsageProbe
    {
        private static readonly TimeSpan Gap = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan ForcedGap = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan Interest = TimeSpan.FromSeconds(90);
        private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(15);

        private static readonly Regex Line = new(
            @"^\s*Current (?<what>session|week \((?<name>[^)]+)\))\s*:\s*(?<pct>\d+(?:\.\d+)?)% used(?:\s*·\s*resets (?<resets>.+?))?\s*$",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private static Int32 _running;
        private static Int64 _wantedTicks;
        private static DateTime _triedAt = DateTime.MinValue;
        private static DateTime _notBefore = DateTime.MinValue;
        private static volatile Reading _latest = Reading.None;

        public sealed class Reading
        {
            public static readonly Reading None = new();

            public DateTime TakenAt { get; init; } = DateTime.MinValue;
            public UsageWindow Session { get; init; } = new() { Title = "session" };
            public UsageWindow Weekly { get; init; } = new() { Title = "weekly" };
            public IReadOnlyList<UsageWindow> Models { get; init; } = Array.Empty<UsageWindow>();

            public Boolean IsKnown => this.TakenAt != DateTime.MinValue;
        }

        public static Reading Latest => _latest;

        // Called whenever a usage key is drawn: the probe only runs while somebody is looking.
        public static void Wanted() => Interlocked.Exchange(ref _wantedTicks, DateTime.UtcNow.Ticks);

        // Called on UsageStore's poll; `force` is a press on a usage key.
        public static void Tick(Boolean force = false)
        {
            if (!DeckConfig.UsagePerModel)
            {
                return;
            }

            var now = DateTime.UtcNow;
            var wanted = now - new DateTime(Interlocked.Read(ref _wantedTicks), DateTimeKind.Utc) < Interest;
            var due = force ? now - _triedAt > ForcedGap : wanted && now - _triedAt > Gap && now > _notBefore;
            if (!due || Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            {
                return;
            }

            _triedAt = now;
            Task.Run(() =>
            {
                try
                {
                    Run();
                }
                catch (Exception ex)
                {
                    PluginLog.Verbose($"usage probe failed: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _running, 0);
                }
            });
        }

        private static void Run()
        {
            var claude = FindClaude();
            if (claude == null)
            {
                _notBefore = DateTime.UtcNow + RetryAfterFailure;
                PluginLog.Info("usage probe: no claude binary found; set usage.claudePath in config.json");
                return;
            }

            // Run from the deck's own folder, marked so deck-hook.sh ignores it: it must never turn
            // up as a session on the keypad it is feeding. The service starts with a bare PATH, and an
            // npm-installed claude needs to find node.
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var path = $"{Path.Combine(home, ".local", "bin")}:/opt/homebrew/bin:/usr/local/bin:{Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin"}";
            var result = Shell.RunIn(
                DeckConfig.Root,
                new Dictionary<String, String> { ["CLAUDE_DECK_PROBE"] = "1", ["PATH"] = path },
                claude, 45_000, "-p", "/usage", "--no-session-persistence");

            var reading = Parse(result.Output, DateTime.Now);
            if (reading == null)
            {
                _notBefore = DateTime.UtcNow + RetryAfterFailure;
                PluginLog.Info($"usage probe: nothing to read (exit {result.ExitCode}) {result.Error.Trim()}");
                return;
            }

            _latest = reading;
            PluginLog.Info($"usage probe: {String.Join(", ", reading.Models.Select(m => $"{m.Title} {m.Percent:0}%").DefaultIfEmpty("no per-model window"))}");
            UsageStore.Recompose();
        }

        public static Reading Parse(String text, DateTime now)
        {
            if (String.IsNullOrEmpty(text))
            {
                return null;
            }

            UsageWindow session = null, weekly = null;
            var models = new List<UsageWindow>();
            foreach (Match m in Line.Matches(text))
            {
                if (!Double.TryParse(m.Groups["pct"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
                {
                    continue;
                }

                var resets = ParseReset(m.Groups["resets"].Value, now);
                var name = m.Groups["name"].Value.Trim();
                if (m.Groups["what"].Value == "session")
                {
                    session = new UsageWindow { Title = "session", Percent = percent, ResetsAt = resets };
                }
                else if (name.Equals("all models", StringComparison.OrdinalIgnoreCase))
                {
                    weekly = new UsageWindow { Title = "weekly", Percent = percent, ResetsAt = resets };
                }
                else
                {
                    // "Sonnet only" -> "Sonnet"
                    name = Regex.Replace(name, @"\s+only$", "", RegexOptions.IgnoreCase);
                    models.Add(new UsageWindow { Title = name, Percent = percent, ResetsAt = resets });
                }
            }

            return session == null && weekly == null && models.Count == 0
                ? null
                : new Reading
                {
                    TakenAt = now,
                    Session = session ?? new UsageWindow { Title = "session" },
                    Weekly = weekly ?? new UsageWindow { Title = "weekly" },
                    Models = models,
                };
        }

        // "Sep 23 at 9:30pm (Asia/Calcutta)", "5:40am (Asia/Calcutta)", "Sep 23 at 9pm".
        private static DateTime ParseReset(String text, DateTime now)
        {
            text = Regex.Replace(text ?? "", @"\s*\(.*\)\s*$", "").Trim();
            text = Regex.Replace(text, @"\s*(am|pm)$", m => " " + m.Groups[1].Value.ToUpperInvariant(), RegexOptions.IgnoreCase);
            if (text.Length == 0)
            {
                return DateTime.MinValue;
            }

            var formats = new[] { "MMM d 'at' h:mm tt", "MMM d 'at' h tt", "MMM d, yyyy 'at' h:mm tt", "h:mm tt", "h tt" };
            if (!DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var at))
            {
                return DateTime.MinValue;
            }

            // A time alone is the next time the clock says so; a date alone is its next occurrence.
            if (!text.Contains(" at ", StringComparison.Ordinal))
            {
                at = now.Date + at.TimeOfDay;
                return at < now ? at.AddDays(1) : at;
            }

            return at < now.AddDays(-1) ? at.AddYears(1) : at;
        }

        private static String FindClaude()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var candidates = new[]
            {
                DeckConfig.ClaudePath,
                Path.Combine(home, ".local", "bin", "claude"),
                Path.Combine(home, ".claude", "local", "claude"),
                "/opt/homebrew/bin/claude",
                "/usr/local/bin/claude",
            };
            return candidates.FirstOrDefault(c => !String.IsNullOrEmpty(c) && File.Exists(c));
        }
    }
}
