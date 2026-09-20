namespace Loupedeck.ClaudeDeckPlugin
{
    using System;
    using System.Collections.Concurrent;
    using System.IO;
    using System.Text;
    using System.Text.Json;

    public sealed class TranscriptInfo
    {
        public String Title { get; init; } = "";
        public String Slug { get; init; } = "";
        public String Model { get; init; } = "";

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
                if (!wantsUsage && !wantsTitle && !wantsSlug)
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
                        }
                    }
                }
                catch (JsonException)
                {
                }

                if (tokens >= 0 && slug.Length > 0 && (customTitle.Length > 0 || aiTitle.Length > 0))
                {
                    break;
                }
            }

            var title = customTitle.Length > 0 ? customTitle : aiTitle;
            return new TranscriptInfo
            {
                // A tail that happens to hold no title line must not blank a tile that had one.
                Title = title.Length > 0 ? title : previous?.Title ?? "",
                Slug = slug.Length > 0 ? slug : previous?.Slug ?? "",
                Model = model.Length > 0 ? model : previous?.Model ?? "",
                ContextTokens = tokens >= 0 ? tokens : previous?.ContextTokens ?? 0,
            };
        }

        private static String Str(JsonElement e, String name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        private static Int64 Num(JsonElement e, String name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
    }
}
