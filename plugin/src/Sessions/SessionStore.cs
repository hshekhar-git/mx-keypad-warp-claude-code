namespace Loupedeck.ClaudeDeckPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using System.Threading;

    public sealed class SessionInfo
    {
        public String Key { get; init; } = "";
        public String Term { get; init; } = "";
        public String WarpUuid { get; init; } = "";
        public String Bundle { get; init; } = "";
        public Int32 Pid { get; init; }
        public String Project { get; init; } = "";
        public String Branch { get; init; } = "";
        public String Transcript { get; init; } = "";
        public String Prompt { get; init; } = "";
        public Int64 Started { get; init; }

        // idle | busy | attention | done | error
        public String State { get; init; } = "idle";

        // For attention: permission | question | plan
        public String Kind { get; init; } = "";
        public Int64 Since { get; init; }
        public Int64 TurnSince { get; init; }
        public String Tool { get; init; } = "";
        public String Detail { get; init; } = "";

        // Permission mode as of the last hook event, and when that event was.
        public String Mode { get; init; } = "";
        public Int64 Ts { get; init; }
        public Int32 Turns { get; init; }

        // For a single multiple-choice AskUserQuestion: its wording and option labels.
        public String Question { get; init; } = "";
        public IReadOnlyList<String> Options { get; init; } = Array.Empty<String>();

        // From the transcript.
        public String Title { get; set; } = "";
        public String Slug { get; set; } = "";
        public String Model { get; set; } = "";

        // The model this session is set to right now, /model switches included.
        public ModelName Selected { get; set; } = ModelName.Unknown;

        // auto | low | medium | high | xhigh | max
        public String Effort { get; set; } = "auto";

        // When the usage limit this session has hit lifts ("4:40am"); empty when it has not hit one.
        public String Limit { get; set; } = "";

        // Stopped by the limit rather than working through it at low priority.
        public Boolean IsLimited => this.Limit.Length > 0 && this.State != "busy";
        public Int64 ContextTokens { get; set; }
        public Int32 ContextWindow { get; set; }

        public PaneLocation Location { get; set; }

        // True while this is the pane you are actually typing into.
        public Boolean Here => this.Location?.Focused == true;

        public Boolean NeedsPermission => this.State == "attention" && this.Kind == "permission";

        // 0..1, or -1 when unknown.
        public Double ContextFill =>
            this.ContextTokens > 0 && this.ContextWindow > 0
                ? Math.Min(1.0, this.ContextTokens / (Double)this.ContextWindow)
                : -1;
    }

    public sealed class SessionGroup
    {
        public String Id { get; init; } = "";
        public List<SessionInfo> Sessions { get; init; } = new();
    }

    // Raised instead of plain EventArgs when WHICH sessions exist (or where they sit) has changed,
    // as opposed to what one of them is doing.
    public sealed class LayoutChangedEventArgs : EventArgs
    {
    }

    public sealed class TransitionEventArgs : EventArgs
    {
        public SessionInfo Session { get; init; }
        public String From { get; init; } = "";
        public Int64 PreviousSince { get; init; }
    }

    // The single source of truth about sessions: the hook's files, joined with Warp's tab layout and
    // each transcript's title and token count. Watches the directory, and also polls, because a
    // session that dies without a SessionEnd leaves a file only a liveness check can clear.
    public sealed class SessionStore : IDisposable
    {
        private const Int32 DebounceMs = 120;
        private const Int32 PollMs = 2000;
        private const Int64 OrphanSeconds = 12 * 3600;

        private static readonly Object InstanceGate = new();
        private static SessionStore _instance;

        public static SessionStore Instance
        {
            get
            {
                lock (InstanceGate)
                {
                    return _instance ??= new SessionStore();
                }
            }
        }

        public static void Shutdown()
        {
            lock (InstanceGate)
            {
                _instance?.Dispose();
                _instance = null;
            }
        }

        private readonly Object _gate = new();
        private readonly FileSystemWatcher _watcher;
        private readonly Timer _debounce;
        private readonly Timer _poll;
        private readonly Dictionary<String, PaneLocation> _lastSeen = new(StringComparer.Ordinal);
        private Dictionary<String, PaneLocation> _locations = new(StringComparer.Ordinal);
        private DateTime _locationsReadAt = DateTime.MinValue;

        private volatile List<SessionGroup> _groups = new();
        private Dictionary<String, SessionInfo> _previous = new(StringComparer.Ordinal);
        private String _layoutSignature = "";
        private String _contentSignature = "";
        private Boolean _primed;
        private volatile Boolean _disposed;

        public event EventHandler Changed;

        public event EventHandler<TransitionEventArgs> Transition;

        private SessionStore()
        {
            var dir = DeckConfig.SessionsDir;
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                PluginLog.Warning($"Could not create {dir}: {ex.Message}");
            }

            this._debounce = new Timer(_ => this.Reload(), null, Timeout.Infinite, Timeout.Infinite);
            this._poll = new Timer(_ => this.Reload(), null, PollMs, PollMs);

            try
            {
                this._watcher = new FileSystemWatcher(dir, "*.json")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                    IncludeSubdirectories = false,
                };
                this._watcher.Created += this.OnFileEvent;
                this._watcher.Changed += this.OnFileEvent;
                this._watcher.Deleted += this.OnFileEvent;
                this._watcher.Renamed += this.OnFileEvent;
                this._watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                PluginLog.Warning($"File watching unavailable, polling only: {ex.Message}");
            }

            this.Reload();
        }

        public IReadOnlyList<SessionGroup> Groups => this._groups;

        public List<SessionInfo> All => this._groups.SelectMany(g => g.Sessions).ToList();

        public SessionInfo Find(String key)
        {
            if (String.IsNullOrEmpty(key))
            {
                return null;
            }

            foreach (var g in this._groups)
            {
                foreach (var s in g.Sessions)
                {
                    if (s.Key == key)
                    {
                        return s;
                    }
                }
            }

            return null;
        }

        // For callers that know something relevant just changed - the app in front, say.
        public void Poke() => this.OnFileEvent(null, null);

        private void OnFileEvent(Object sender, FileSystemEventArgs e)
        {
            if (this._disposed)
            {
                return;
            }

            try
            {
                this._debounce.Change(DebounceMs, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void Reload()
        {
            if (this._disposed || !Monitor.TryEnter(this._gate))
            {
                return;
            }

            try
            {
                var sessions = this.ReadSessions();
                this.Place(sessions);
                this.Enrich(sessions);
                var groups = Group(sessions);

                var layout = String.Join("|", groups.Select(g => g.Id + "=" + String.Join(",", g.Sessions.Select(s => s.Key))));
                var content = Signature(sessions);
                var transitions = this.Transitions(sessions);

                this._groups = groups;
                this._previous = sessions.ToDictionary(s => s.Key, s => s, StringComparer.Ordinal);

                var layoutChanged = layout != this._layoutSignature;
                var contentChanged = content != this._contentSignature;
                this._layoutSignature = layout;
                this._contentSignature = content;
                this._primed = true;

                if (this._disposed)
                {
                    return;
                }

                foreach (var t in transitions)
                {
                    this.Transition?.Invoke(this, t);
                }

                if (layoutChanged)
                {
                    this.Changed?.Invoke(this, new LayoutChangedEventArgs());
                }
                else if (contentChanged)
                {
                    this.Changed?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning($"session reload failed: {ex.Message}");
            }
            finally
            {
                Monitor.Exit(this._gate);
            }
        }

        private List<SessionInfo> ReadSessions()
        {
            var list = new List<SessionInfo>();
            var dir = DeckConfig.SessionsDir;
            if (!Directory.Exists(dir))
            {
                return list;
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                SessionInfo s;
                Int64 ts;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var r = doc.RootElement;
                    if (r.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    ts = Num(r, "ts");
                    s = new SessionInfo
                    {
                        Key = Path.GetFileNameWithoutExtension(file),
                        Term = Str(r, "term"),
                        WarpUuid = Str(r, "warp_uuid"),
                        Bundle = Str(r, "bundle"),
                        Pid = (Int32)Num(r, "pid"),
                        Project = Str(r, "project"),
                        Branch = Str(r, "branch"),
                        Transcript = Str(r, "transcript"),
                        Prompt = Str(r, "prompt"),
                        Started = Num(r, "started"),
                        State = Str(r, "state") is { Length: > 0 } st ? st : "idle",
                        Kind = Str(r, "kind"),
                        Since = Num(r, "since"),
                        TurnSince = Num(r, "turn_since"),
                        Tool = Str(r, "tool"),
                        Detail = Str(r, "detail"),
                        Mode = Str(r, "mode"),
                        Ts = ts,
                        Turns = (Int32)Num(r, "turns"),
                        Question = Str(r, "question"),
                        Options = r.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array
                            ? opts.EnumerateArray().Where(o => o.ValueKind == JsonValueKind.String).Select(o => o.GetString() ?? "").ToList()
                            : new List<String>(),
                    };
                }
                catch (Exception)
                {
                    // Mid-rename or not ours; the next pass will see it whole.
                    continue;
                }

                var dead = s.Pid > 0 ? !IsAlive(s.Pid) : now - ts > OrphanSeconds;
                if (dead)
                {
                    PluginLog.Info($"reaping {s.Key} ({s.Project}): its claude process is gone");
                    TranscriptStats.Forget(s.Transcript);
                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                    }

                    continue;
                }

                list.Add(s);
            }

            return list;
        }

        // Which Warp tab each pane is in. Warp persists its pane tree lazily, so a live pane can
        // briefly vanish from the database when a neighbour closes; trusting that blink would shuffle
        // every page, so a pane keeps the last place it was seen until the database says otherwise.
        private void Place(List<SessionInfo> sessions)
        {
            if (!sessions.Any(s => s.WarpUuid.Length > 0))
            {
                return;
            }

            if (DateTime.UtcNow - this._locationsReadAt > TimeSpan.FromMilliseconds(PollMs - 200))
            {
                this._locations = WarpTabs.Read();
                this._locationsReadAt = DateTime.UtcNow;
            }

            foreach (var s in sessions.Where(s => s.WarpUuid.Length > 0))
            {
                if (this._locations.TryGetValue(s.WarpUuid, out var loc))
                {
                    this._lastSeen[s.WarpUuid] = loc;
                    s.Location = loc;
                }
                else if (this._lastSeen.TryGetValue(s.WarpUuid, out var remembered))
                {
                    s.Location = remembered;
                }
            }

            var live = new HashSet<String>(sessions.Select(s => s.WarpUuid), StringComparer.Ordinal);
            foreach (var gone in this._lastSeen.Keys.Where(k => !live.Contains(k)).ToList())
            {
                this._lastSeen.Remove(gone);
            }
        }

        private void Enrich(List<SessionInfo> sessions)
        {
            foreach (var s in sessions)
            {
                var info = TranscriptStats.Get(s.Transcript);
                if (info == null)
                {
                    continue;
                }

                s.Title = info.Title;
                s.Slug = info.Slug;
                s.Model = info.Model;
                s.ContextTokens = info.ContextTokens;
                s.Selected = ModelNames.Resolve(info.SwitchedTo, info.Model);
                s.Effort = info.Effort.Length > 0 ? info.Effort : ModelNames.DefaultEffort;
                s.Limit = info.Limit;

                // More than 200k tokens in the window settles the question whatever the names say.
                if (!s.Selected.OneM && s.Selected.IsKnown && info.ContextTokens > 200_000)
                {
                    s.Selected = new ModelName { Name = s.Selected.Name, OneM = true };
                }
                s.ContextWindow = s.Selected.OneM
                    ? 1_000_000
                    : DeckConfig.ContextWindowFor(info.Model, info.ContextTokens);
            }
        }

        // One group per Warp tab, in window/tab order, then one per other terminal app.
        private static List<SessionGroup> Group(List<SessionInfo> sessions)
        {
            var groups = new List<SessionGroup>();

            foreach (var tab in sessions.Where(s => s.Location != null)
                .GroupBy(s => (s.Location.WindowId, s.Location.TabId))
                .OrderBy(g => g.Key.WindowId).ThenBy(g => g.Key.TabId))
            {
                groups.Add(new SessionGroup
                {
                    Id = $"w{tab.Key.WindowId}t{tab.Key.TabId}",
                    Sessions = tab.OrderBy(s => s.Location.Ordinal).ThenBy(s => s.Started).ToList(),
                });
            }

            foreach (var app in sessions.Where(s => s.Location == null)
                .GroupBy(s => s.WarpUuid.Length > 0 ? "warp" : (s.Bundle.Length > 0 ? s.Bundle : "other"))
                .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                groups.Add(new SessionGroup
                {
                    Id = $"app-{app.Key}",
                    Sessions = app.OrderBy(s => s.Started).ThenBy(s => s.Key, StringComparer.Ordinal).ToList(),
                });
            }

            return groups;
        }

        private List<TransitionEventArgs> Transitions(List<SessionInfo> sessions)
        {
            var list = new List<TransitionEventArgs>();

            // The first pass after a load describes history, not news.
            if (!this._primed)
            {
                return list;
            }

            foreach (var s in sessions)
            {
                this._previous.TryGetValue(s.Key, out var before);
                var from = before?.State ?? "";
                if (from != s.State)
                {
                    list.Add(new TransitionEventArgs
                    {
                        Session = s,
                        From = from,
                        PreviousSince = before?.TurnSince ?? 0,
                    });
                }
            }

            return list;
        }

        private static String Signature(List<SessionInfo> sessions)
        {
            var sb = new StringBuilder();
            foreach (var s in sessions.OrderBy(s => s.Key, StringComparer.Ordinal))
            {
                sb.Append(s.Key).Append('\u001f')
                    .Append(s.State).Append('\u001f')
                    .Append(s.Kind).Append('\u001f')
                    .Append(s.Since).Append('\u001f')
                    .Append(s.Tool).Append('\u001f')
                    .Append(s.Detail).Append('\u001f')
                    .Append(s.Project).Append('\u001f')
                    .Append(s.Title).Append('\u001f')
                    .Append(s.Prompt).Append('\u001f')
                    .Append(s.Here ? '1' : '0').Append('\u001f')
                    .Append(s.Selected.Name).Append(s.Selected.OneM ? "+" : "").Append('\u001f')
                    .Append(s.Limit).Append('\u001f')
                    .Append(s.Effort).Append('\u001f').Append(s.Mode).Append('\u001f').Append(s.Turns).Append('\u001f')
                    .Append(String.Join(",", s.Options)).Append('\u001f')
                    .Append((Int32)(s.ContextFill * 100)).Append('\u001e');
            }

            return sb.ToString();
        }

        private static Boolean IsAlive(Int32 pid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                return !p.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (Exception)
            {
                // Not allowed to look is not the same as not there.
                return true;
            }
        }

        private static String Str(JsonElement e, String name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        private static Int64 Num(JsonElement e, String name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

        public void Dispose()
        {
            if (this._disposed)
            {
                return;
            }

            this._disposed = true;
            this.Changed = null;
            this.Transition = null;
            try
            {
                if (this._watcher != null)
                {
                    this._watcher.EnableRaisingEvents = false;
                    this._watcher.Dispose();
                }
            }
            catch
            {
            }

            this._debounce.Dispose();
            this._poll.Dispose();
            TranscriptStats.Clear();
        }
    }
}
