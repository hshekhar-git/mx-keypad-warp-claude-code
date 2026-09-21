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
        public String SessionId { get; init; } = "";
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
        public String Mode { get; set; } = "";
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
        private const Int32 SettleMs = 120;
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
        private readonly FolderWatch _watch;
        private readonly Dictionary<String, (PaneLocation Where, DateTime SeenAt)> _placements = new(StringComparer.Ordinal);
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
            // A burst of hook writes settles into one reload; the steady check is what notices a
            // session whose process died without saying goodbye.
            this._watch = new FolderWatch(DeckConfig.SessionsDir, "*.json", SettleMs, PollMs, this.Reload);
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
        public void Poke() => this._watch.Nudge();

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
                ApplyStatus(sessions);
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
                    var key = Path.GetFileNameWithoutExtension(file);
                    var state = Str(r, "state") is { Length: > 0 } st ? st : "idle";
                    var kind = Str(r, "kind");

                    // A prompt answered from the keypad: working, whatever the file still says.
                    if (state == "attention" && Deck.WasAnswered(key, ts))
                    {
                        state = "busy";
                        kind = "";
                    }

                    s = new SessionInfo
                    {
                        Key = key,
                        Term = Str(r, "term"),
                        WarpUuid = Str(r, "warp_uuid"),
                        Bundle = Str(r, "bundle"),
                        SessionId = Str(r, "session_id"),
                        Pid = (Int32)Num(r, "pid"),
                        Project = Str(r, "project"),
                        Branch = Str(r, "branch"),
                        Transcript = Str(r, "transcript"),
                        Prompt = Str(r, "prompt"),
                        Started = Num(r, "started"),
                        State = state,
                        Kind = kind,
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
                    // Caught half-written, or not a session file at all. Either way: skip it this time.
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

        // Gives each Warp session its place in Warp's layout.
        //
        // The layout database is written by Warp on its own schedule, so for a second or two after a
        // pane opens, closes or moves, a pane that certainly exists can be missing from it. A session
        // whose pane cannot be found therefore keeps the place it last had - but only for a grace
        // period. After that the database is believed: the pane really has gone somewhere this
        // cannot see, and the session is listed under its app instead of under a tab it has left.
        private static readonly TimeSpan PlacementGrace = TimeSpan.FromSeconds(12);

        private void Place(List<SessionInfo> sessions)
        {
            var inWarp = sessions.Where(s => s.WarpUuid.Length > 0).ToList();
            if (inWarp.Count == 0)
            {
                this._placements.Clear();
                return;
            }

            // One sqlite3 run serves every reload inside the same poll interval.
            var now = DateTime.UtcNow;
            if (now - this._locationsReadAt > TimeSpan.FromMilliseconds(PollMs - 200))
            {
                this._locations = WarpTabs.Read();
                this._locationsReadAt = now;
            }

            foreach (var session in inWarp)
            {
                if (this._locations.TryGetValue(session.WarpUuid, out var found))
                {
                    this._placements[session.WarpUuid] = (found, now);
                    session.Location = found;
                }
                else if (this._placements.TryGetValue(session.WarpUuid, out var last) && now - last.SeenAt <= PlacementGrace)
                {
                    // Still within the grace period - but whatever it was, it no longer has the keyboard.
                    session.Location = new PaneLocation
                    {
                        WindowId = last.Where.WindowId,
                        TabId = last.Where.TabId,
                        TabTitle = last.Where.TabTitle,
                        Ordinal = last.Where.Ordinal,
                        Focused = false,
                    };
                }
            }

            var current = inWarp.Select(s => s.WarpUuid).ToHashSet(StringComparer.Ordinal);
            foreach (var pane in this._placements.Keys.Where(k => !current.Contains(k)).ToList())
            {
                this._placements.Remove(pane);
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

                // The transcript is told about a mode change when it happens; a hook only mentions the
                // mode in passing, on the next event. So the transcript wins whenever it has a view.
                if (info.Mode.Length > 0)
                {
                    s.Mode = info.Mode;
                }

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

        // What Claude Code itself said about each session, via its status line, beats what could be
        // worked out from the transcript: it is exact, and it is there the moment a setting changes.
        private static void ApplyStatus(List<SessionInfo> sessions)
        {
            foreach (var s in sessions)
            {
                var status = SessionStatus.Read(s.SessionId);
                if (status == null)
                {
                    continue;
                }

                var name = status.ModelName.Length > 0 ? ModelNames.FromDisplay(status.ModelName) : ModelNames.FromId(status.ModelId);
                if (name.IsKnown)
                {
                    var oneM = name.OneM || status.ContextWindow >= 1_000_000
                        || status.ModelId.Contains("[1m]", StringComparison.OrdinalIgnoreCase);
                    s.Selected = new ModelName { Name = name.Name, OneM = oneM };
                }

                if (status.Effort.Length > 0)
                {
                    s.Effort = status.Effort;
                }

                if (status.ContextWindow > 0 && status.ContextTokens > 0)
                {
                    s.ContextWindow = status.ContextWindow;
                    s.ContextTokens = status.ContextTokens;
                }
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

            // Whatever state the sessions are in when the plugin starts is not something that just
            // happened, so the first reading sets the baseline and announces nothing.
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
                // Being refused information about a process does not make it dead.
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
            this._watch.Dispose();
            TranscriptStats.Clear();
        }
    }
}
