namespace Loupedeck.ClaudeDeckPlugin
{
    using System;
    using System.Collections.Concurrent;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.Json;

    public sealed class TranscriptInfo
    {
        public String Title { get; init; } = "";
        public String Slug { get; init; } = "";
        public String Model { get; init; } = "";

        // The display name from a /model switch, when that is more recent than the last reply;
        // empty otherwise. "Opus 5 (1M context)".
        public String SwitchedTo { get; init; } = "";

        // From the last /effort in this session: low | medium | high | xhigh | max | ultracode | auto. Empty when
        // the session never ran one, in which case it is on the default.
        public String Effort { get; init; } = "";

        // The permission mode, from the newest line that states it. Claude Code writes a
        // {"type":"permission-mode"} line the moment the mode changes and stamps the mode on every
        // message, so this is known for a resumed session before its first prompt, and it moves as
        // soon as Shift-Tab is pressed - neither of which is true of what the hooks report.
        public String Mode { get; init; } = "";

        // Set while the newest main-thread reply is a usage-limit error: what it said about when the
        // limit lifts ("4:40am"), and that moment, after which it stops counting.
        public String LimitLabel { get; init; } = "";
        public DateTime LimitResetsAt { get; init; } = DateTime.MinValue;

        public String Limit => this.LimitLabel.Length > 0 && DateTime.Now < this.LimitResetsAt ? this.LimitLabel : "";

        // Tokens in the context window as of the last main-thread assistant message.
        public Int64 ContextTokens { get; init; }
    }

    // Reads what the hook payload does not carry out of the session transcript: Claude's own title
    // for the session, and how full the context window is.
    //
    // Only the tail is read - transcripts reach tens of megabytes and everything wanted here is
    // repeated near the end - and a file is re-read at most once every few seconds, and only if it
    // has grown, so a deck of busy sessions does not turn into a disk benchmark.
    public static class TranscriptStats
    {
        private const Int32 TailBytes = 384 * 1024;
        private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(6);

        private static readonly ConcurrentDictionary<String, Entry> Cache = new(StringComparer.Ordinal);

        private sealed class Entry
        {
            public Int64 Length;
            public DateTime ReadAt;
            public TranscriptInfo Info;
        }

        public static TranscriptInfo Get(String path)
        {
            if (String.IsNullOrEmpty(path))
            {
                return null;
            }

            try
            {
                var file = new FileInfo(path);
                if (!file.Exists)
                {
                    return null;
                }

                Cache.TryGetValue(path, out var cached);
                if (cached != null
                    && (cached.Length == file.Length || DateTime.UtcNow - cached.ReadAt < MinInterval))
                {
                    return cached.Info;
                }

                var info = Parse(path, file.Length, cached?.Info);
                Cache[path] = new Entry { Length = file.Length, ReadAt = DateTime.UtcNow, Info = info };
                return info;
            }
            catch (Exception ex)
            {
                PluginLog.Verbose($"transcript read failed for {path}: {ex.Message}");
                return null;
            }
        }

        // Re-read on the next ask, keeping what is known so far (unlike Forget, which starts over).
        public static void Refresh(String path)
        {
            if (!String.IsNullOrEmpty(path) && Cache.TryGetValue(path, out var entry))
            {
                entry.Length = -1;
                entry.ReadAt = DateTime.MinValue;
            }
        }

        public static void Forget(String path)
        {
            if (!String.IsNullOrEmpty(path))
            {
                Cache.TryRemove(path, out _);
            }
        }

        public static void Clear() => Cache.Clear();

        private static TranscriptInfo Parse(String path, Int64 length, TranscriptInfo previous)
        {
            String tail;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var start = Math.Max(0, length - TailBytes);
                fs.Seek(start, SeekOrigin.Begin);
                var buffer = new Byte[(Int32)Math.Min(TailBytes, length - start)];
                var read = 0;
                while (read < buffer.Length)
                {
                    var n = fs.Read(buffer, read, buffer.Length - read);
                    if (n <= 0)
                    {
                        break;
                    }

                    read += n;
                }

                tail = Encoding.UTF8.GetString(buffer, 0, read);
            }

            var customTitle = "";
            var aiTitle = "";
            var slug = "";
            var model = "";
            var switchedTo = "";
            var modelSettled = false;
            var effort = "";
            var mode = "";
            var replySeen = false;
            var limitLabel = "";
            var limitResetsAt = DateTime.MinValue;
            Int64 tokens = -1;

            // Newest first, stopping as soon as everything has been seen once.
            var lines = tail.Split('\n');
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i];

                // The first line of the tail is almost always cut in half.
                if (line.Length < 2 || line[0] != '{')
                {
                    continue;
                }

                var wantsUsage = tokens < 0 && line.Contains("\"usage\"", StringComparison.Ordinal);
                var wantsTitle = (customTitle.Length == 0 && line.Contains("\"customTitle\"", StringComparison.Ordinal))
                    || (aiTitle.Length == 0 && line.Contains("\"aiTitle\"", StringComparison.Ordinal));
                var wantsSlug = slug.Length == 0 && line.Contains("\"slug\"", StringComparison.Ordinal);
                var wantsSwitch = !modelSettled
                    && (line.Contains("Set model to", StringComparison.Ordinal) || line.Contains("Kept model as", StringComparison.Ordinal));
                var wantsEffort = effort.Length == 0 && IsEffortCandidate(line);
                var wantsReply = !replySeen && line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal);
                var wantsMode = mode.Length == 0 && line.Contains("\"permissionMode\"", StringComparison.Ordinal);
                if (!wantsUsage && !wantsTitle && !wantsSlug && !wantsSwitch && !wantsEffort && !wantsReply && !wantsMode)
                {
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (customTitle.Length == 0)
                    {
                        customTitle = Str(root, "customTitle");
                    }

                    if (aiTitle.Length == 0)
                    {
                        aiTitle = Str(root, "aiTitle");
                    }

                    if (slug.Length == 0)
                    {
                        slug = Str(root, "slug");
                    }

                    if (wantsEffort)
                    {
                        effort = EffortFrom(root);
                    }

                    if (wantsMode)
                    {
                        mode = Str(root, "permissionMode");
                    }

                    // Only the NEWEST main-thread reply says whether the session is rate-limited now:
                    // once Claude has answered again (at low priority, or after the reset) it is not.
                    if (wantsReply && Str(root, "type") == "assistant"
                        && !(root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True))
                    {
                        replySeen = true;
                        if (Str(root, "error") == "rate_limit"
                            && root.TryGetProperty("isApiErrorMessage", out var apiError) && apiError.ValueKind == JsonValueKind.True)
                        {
                            (limitLabel, limitResetsAt) = LimitFrom(root);
                        }
                    }

                    // Reading newest first, whichever of "a /model switch" and "a reply" turns up first
                    // is the last word on which model is selected. The match is on the whole message,
                    // not a substring, because a transcript can quote this very text.
                    if (wantsSwitch
                        && root.TryGetProperty("message", out var sm) && sm.ValueKind == JsonValueKind.Object
                        && sm.TryGetProperty("content", out var sc0) && sc0.ValueKind == JsonValueKind.String)
                    {
                        var text = sc0.GetString() ?? "";
                        foreach (var lead in new[] { "<local-command-stdout>Set model to ", "<local-command-stdout>Kept model as " })
                        {
                            if (text.StartsWith(lead, StringComparison.Ordinal))
                            {
                                var rest = text.Substring(lead.Length);
                                var end = rest.IndexOf(" and saved", StringComparison.Ordinal);
                                if (end < 0)
                                {
                                    end = rest.IndexOf("</local-command-stdout>", StringComparison.Ordinal);
                                }

                                switchedTo = (end >= 0 ? rest.Substring(0, end) : rest).Trim();
                                modelSettled = true;
                            }
                        }
                    }

                    // Subagent traffic shares the file but not the context window.
                    var sidechain = root.TryGetProperty("isSidechain", out var sc) && sc.ValueKind == JsonValueKind.True;
                    if (tokens < 0 && !sidechain
                        && root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object
                        && msg.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                    {
                        var sum = Num(usage, "input_tokens")
                            + Num(usage, "cache_read_input_tokens")
                            + Num(usage, "cache_creation_input_tokens");
                        if (sum > 0)
                        {
                            tokens = sum;
                            model = Str(msg, "model");
                            modelSettled = true;
                        }
                    }
                }
                catch (JsonException)
                {
                }

                if (tokens >= 0 && effort.Length > 0 && mode.Length > 0 && slug.Length > 0 && (customTitle.Length > 0 || aiTitle.Length > 0))
                {
                    break;
                }
            }

            // Unlike the model, effort is not restated by every reply: one /effort early in a long
            // session can be megabytes behind the tail. So the first time a transcript is seen, and
            // only then, the whole file is searched for it; after that the tail keeps it current.
            if (effort.Length == 0 && previous == null && length > TailBytes)
            {
                effort = ScanWholeFileForEffort(path);
            }

            var title = customTitle.Length > 0 ? customTitle : aiTitle;
            return new TranscriptInfo
            {
                Effort = effort.Length > 0 ? effort : previous?.Effort ?? "",
                Mode = mode.Length > 0 ? mode : previous?.Mode ?? "",
                LimitLabel = replySeen ? limitLabel : previous?.LimitLabel ?? "",
                LimitResetsAt = replySeen ? limitResetsAt : previous?.LimitResetsAt ?? DateTime.MinValue,
                // A tail that happens to hold no title line must not blank a tile that had one.
                Title = title.Length > 0 ? title : previous?.Title ?? "",
                Slug = slug.Length > 0 ? slug : previous?.Slug ?? "",
                Model = model.Length > 0 ? model : previous?.Model ?? "",

                // Settled by a reply means any earlier switch is history; unsettled means the tail
                // said nothing either way, so what was known before still stands.
                SwitchedTo = modelSettled ? switchedTo : previous?.SwitchedTo ?? "",
                ContextTokens = tokens >= 0 ? tokens : previous?.ContextTokens ?? 0,
            };
        }

        private static readonly System.Text.RegularExpressions.Regex ResetTime =
            new(@"resets\s+(\d{1,2})(?::(\d{2}))?\s*(am|pm)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        // "You've hit your session limit · resets 4:40am (Asia/Calcutta)", stamped with when it was said.
        private static (String Label, DateTime ResetsAt) LimitFrom(JsonElement root)
        {
            var text = "";
            if (root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object
                && m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in c.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.Object && Str(part, "text") is { Length: > 0 } t)
                    {
                        text = t;
                        break;
                    }
                }
            }

            var said = DateTime.TryParse(Str(root, "timestamp"), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var utc)
                ? utc.ToLocalTime()
                : DateTime.Now;

            // The named time is in the user's own zone; it means its first occurrence after the message.
            var match = ResetTime.Match(text);
            if (match.Success)
            {
                var hour = Int32.Parse(match.Groups[1].Value) % 12;
                if (match.Groups[3].Value.Equals("pm", StringComparison.OrdinalIgnoreCase))
                {
                    hour += 12;
                }

                var minute = match.Groups[2].Success ? Int32.Parse(match.Groups[2].Value) : 0;
                var at = said.Date.AddHours(hour).AddMinutes(minute);
                if (at <= said)
                {
                    at = at.AddDays(1);
                }

                var label = match.Groups[2].Success
                    ? $"{match.Groups[1].Value}:{match.Groups[2].Value}{match.Groups[3].Value.ToLowerInvariant()}"
                    : $"{match.Groups[1].Value}{match.Groups[3].Value.ToLowerInvariant()}";
                return (label, at);
            }

            // A wording this does not recognise (a weekly limit names a date): the limit is real, its
            // end unknown, so it is shown for one session window and then dropped.
            return ("soon", said.AddHours(5));
        }

        private const String EffortSet = "<local-command-stdout>Set effort level to ";
        private const String EffortAuto = "<local-command-stdout>Effort level set to auto";

        private static Boolean IsEffortCandidate(String line) =>
            line.Contains("Set effort level to ", StringComparison.Ordinal)
            || line.Contains("Effort level set to auto", StringComparison.Ordinal);

        // Matched on the whole message, not a substring: transcripts quote this text too.
        private static String EffortFrom(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("message", out var m) || m.ValueKind != JsonValueKind.Object
                || !m.TryGetProperty("content", out var c) || c.ValueKind != JsonValueKind.String)
            {
                return "";
            }

            var text = c.GetString() ?? "";
            if (text.StartsWith(EffortAuto, StringComparison.Ordinal))
            {
                return "auto";
            }

            if (!text.StartsWith(EffortSet, StringComparison.Ordinal))
            {
                return "";
            }

            var word = new String(text.Substring(EffortSet.Length).TakeWhile(Char.IsLetter).ToArray());
            return word.ToLowerInvariant();
        }

        private static String ScanWholeFileForEffort(String path)
        {
            var found = "";
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs, Encoding.UTF8);
                String line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (!IsEffortCandidate(line))
                    {
                        continue;
                    }

                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var value = EffortFrom(doc.RootElement);
                        if (value.Length > 0)
                        {
                            found = value;
                        }
                    }
                    catch (JsonException)
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Verbose($"effort scan failed for {path}: {ex.Message}");
            }

            return found;
        }

        private static String Str(JsonElement e, String name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        private static Int64 Num(JsonElement e, String name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
    }
}
