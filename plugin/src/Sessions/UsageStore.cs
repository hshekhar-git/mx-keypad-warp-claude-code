namespace Loupedeck.ClaudeDeckPlugin
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;
    using System.Threading;

    public sealed class UsageWindow
    {
        public String Title { get; init; } = "";
        public Double Percent { get; init; } = -1;
        public DateTime ResetsAt { get; init; } = DateTime.MinValue;

        public Boolean IsKnown => this.Percent >= 0;
    }

    public sealed class UsageSnapshot
    {
        public static readonly UsageSnapshot None = new();

        public DateTime TakenAt { get; init; } = DateTime.MinValue;
        public UsageWindow Session { get; init; } = new() { Title = "session" };
        public UsageWindow Weekly { get; init; } = new() { Title = "weekly" };

        // Per-model weekly buckets ("Fable"), when Claude Code reports them.
        public IReadOnlyList<UsageWindow> Models { get; init; } = Array.Empty<UsageWindow>();

        public Boolean IsKnown => this.Session.IsKnown || this.Weekly.IsKnown;

        // It only updates while some session is drawing its status line.
        public TimeSpan Age => DateTime.Now - this.TakenAt;
    }

    // Plan usage, as written by deck-statusline.sh from Claude Code's own status-line payload, with
    // what UsageProbe reads off `claude -p /usage` laid over it. No credentials and no network here:
    // if Claude Code has not said, this does not know.
    public static class UsageStore
    {
        private static readonly Object Gate = new();
        private static Timer _poll;
        private static DateTime _stamp = DateTime.MinValue;
        private static volatile UsageSnapshot _current = UsageSnapshot.None;

        // What the status line last reported, before anything from the probe is laid over it.
        private static volatile UsageSnapshot _reported = UsageSnapshot.None;

        public static event EventHandler Changed;

        public static UsageSnapshot Current => _current;

        public static String Path => System.IO.Path.Combine(DeckConfig.Root, "usage.json");

        public static void Start()
        {
            lock (Gate)
            {
                _poll ??= new Timer(_ => Poll(), null, 0, 2000);
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
            UsageProbe.Tick();
            ReadFile();
        }

        // The status line is the better source for the two plan windows - it is fresh whenever a
        // session is working, for free. The probe fills in what the status line never carries (the
        // per-model weekly windows), and stands in for all of it once the status line has gone quiet.
        public static void Recompose()
        {
            var reported = _reported;
            var probe = UsageProbe.Latest;
            var probeIsNewer = probe.IsKnown && probe.TakenAt - reported.TakenAt > TimeSpan.FromMinutes(10);
            _current = new UsageSnapshot
            {
                TakenAt = probeIsNewer ? probe.TakenAt : reported.TakenAt,
                Session = probeIsNewer && probe.Session.IsKnown ? probe.Session : reported.Session,
                Weekly = probeIsNewer && probe.Weekly.IsKnown ? probe.Weekly : reported.Weekly,
                Models = reported.Models.Count > 0 ? reported.Models : probe.Models,
            };
            Changed?.Invoke(null, EventArgs.Empty);
        }

        // The weekly window that counts this session's model, if the plan keeps one for it.
        public static UsageWindow ModelWindow(SessionInfo s)
        {
            if (s == null)
            {
                return null;
            }

            foreach (var w in _current.Models)
            {
                if (w.Title.Length > 0
                    && (s.Selected.Name.Contains(w.Title, StringComparison.OrdinalIgnoreCase)
                        || s.Model.Contains(w.Title, StringComparison.OrdinalIgnoreCase)))
                {
                    return w;
                }
            }

            return null;
        }

        private static void ReadFile()
        {
            try
            {
                var path = Path;
                if (!File.Exists(path))
                {
                    return;
                }

                var stamp = File.GetLastWriteTimeUtc(path);
                if (stamp == _stamp)
                {
                    return;
                }

                _stamp = stamp;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                var models = new List<UsageWindow>();
                if (root.TryGetProperty("model_scoped", out var scoped) && scoped.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in scoped.EnumerateArray())
                    {
                        var name = m.TryGetProperty("display_name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : "";
                        var w = Window(m, name ?? "", "utilization");
                        if (w.IsKnown)
                        {
                            models.Add(w);
                        }
                    }
                }

                _reported = new UsageSnapshot
                {
                    // How old the NUMBERS are is when the reporting session last heard from the API,
                    // not when it last happened to redraw its status line.
                    TakenAt = root.TryGetProperty("activity", out var act) && act.TryGetInt64(out var a) && a > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(a).LocalDateTime
                        : root.TryGetProperty("ts", out var ts) && ts.TryGetInt64(out var t)
                            ? DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime
                            : stamp.ToLocalTime(),
                    Session = root.TryGetProperty("five_hour", out var five) ? Window(five, "session", "used_percentage") : new UsageWindow { Title = "session" },
                    Weekly = root.TryGetProperty("seven_day", out var seven) ? Window(seven, "weekly", "used_percentage") : new UsageWindow { Title = "weekly" },
                    Models = models,
                };
                Recompose();
            }
            catch (Exception ex)
            {
                PluginLog.Verbose($"usage read failed: {ex.Message}");
            }
        }

        private static UsageWindow Window(JsonElement e, String title, String percentField)
        {
            if (e.ValueKind != JsonValueKind.Object)
            {
                return new UsageWindow { Title = title };
            }

            var percent = e.TryGetProperty(percentField, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : -1;

            // A utilisation of 0..1 and a percentage of 0..100 both turn up under these names.
            if (percentField == "utilization" && percent >= 0 && percent <= 1)
            {
                percent *= 100;
            }

            var resets = DateTime.MinValue;
            if (e.TryGetProperty("resets_at", out var r))
            {
                if (r.ValueKind == JsonValueKind.Number && r.TryGetInt64(out var epoch))
                {
                    resets = DateTimeOffset.FromUnixTimeSeconds(epoch > 100_000_000_000 ? epoch / 1000 : epoch).LocalDateTime;
                }
                else if (r.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(r.GetString(), out var parsed))
                {
                    resets = parsed.LocalDateTime;
                }
            }

            return new UsageWindow { Title = title, Percent = percent, ResetsAt = resets };
        }

        // Where the session window lands if you keep going as you have been: the percentage it will
        // have reached at the reset, and - if that is past 100 - when it runs dry.
        public static (Double ProjectedPercent, DateTime RunsOutAt) Pace(UsageWindow session, TimeSpan window)
        {
            if (!session.IsKnown || session.ResetsAt == DateTime.MinValue)
            {
                return (-1, DateTime.MinValue);
            }

            var start = session.ResetsAt - window;
            var elapsed = DateTime.Now - start;

            // Too early in the window for a rate to mean anything.
            if (elapsed < TimeSpan.FromMinutes(10) || session.Percent <= 0)
            {
                return (-1, DateTime.MinValue);
            }

            var perMinute = session.Percent / elapsed.TotalMinutes;
            var projected = perMinute * window.TotalMinutes;
            var runsOut = projected > 100 ? DateTime.Now.AddMinutes((100 - session.Percent) / perMinute) : DateTime.MinValue;
            return (projected, runsOut);
        }
    }

    // What deck-statusline.sh recorded about one session: the exact model, effort and context fill,
    // which otherwise have to be inferred from its transcript.
    public sealed class SessionStatus
    {
        public String ModelName { get; init; } = "";
        public String ModelId { get; init; } = "";
        public String Effort { get; init; } = "";
        public Int64 ContextTokens { get; init; }
        public Int32 ContextWindow { get; init; }

        public static SessionStatus Read(String sessionId)
        {
            if (String.IsNullOrEmpty(sessionId) || sessionId.IndexOfAny(new[] { '/', '\\', '.' }) >= 0)
            {
                return null;
            }

            try
            {
                var path = System.IO.Path.Combine(DeckConfig.Root, "status", sessionId + ".json");
                if (!File.Exists(path))
                {
                    return null;
                }

                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var r = doc.RootElement;
                String Str(String name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                Int64 Num(String name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (Int64)v.GetDouble() : 0;
                return new SessionStatus
                {
                    ModelName = Str("model_name"),
                    ModelId = Str("model_id"),
                    Effort = Str("effort").ToLowerInvariant(),
                    ContextTokens = Num("ctx_tokens"),
                    ContextWindow = (Int32)Num("ctx_size"),
                };
            }
            catch
            {
                return null;
            }
        }

        // Status files outlive their sessions; nothing else clears them.
        public static void Sweep()
        {
            try
            {
                var dir = System.IO.Path.Combine(DeckConfig.Root, "status");
                if (!Directory.Exists(dir))
                {
                    return;
                }

                foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromDays(2))
                    {
                        File.Delete(file);
                    }
                }
            }
            catch
            {
            }
        }
    }
}
