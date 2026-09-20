namespace Loupedeck.ClaudeDeckPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;

    public sealed class AppInfo
    {
        public Int32 Pid { get; init; }
        public String Bundle { get; init; } = "";
        public String Name { get; init; } = "";
        public String Path { get; init; } = "";
        public String Icon { get; init; } = "";
        public Boolean Hidden { get; init; }
        public Double Launched { get; init; }

        // What an action parameter refers to this app by: stable across relaunches when it can be.
        public String Id => this.Bundle.Length > 0 ? this.Bundle : $"pid-{this.Pid}";
    }

    // Runs the deck-apps helper and turns its output into "which apps are running, which is in front".
    //
    // The helper pushes a line whenever something changes, so nothing here polls. It is restarted if
    // it dies, and it exits by itself when our end of its stdin closes - which covers a plugin reload,
    // a service restart and a crash alike.
    public sealed class AppWatcher : IDisposable
    {
        private static readonly Object InstanceGate = new();
        private static AppWatcher _instance;

        public static AppWatcher Instance
        {
            get
            {
                lock (InstanceGate)
                {
                    return _instance ??= new AppWatcher();
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
        private readonly List<String> _recent = new();
        private Process _helper;
        private volatile Boolean _disposed;
        private Int32 _restarts;

        private volatile IReadOnlyList<AppInfo> _apps = Array.Empty<AppInfo>();
        private volatile String _front = "";

        public event EventHandler Changed;

        private AppWatcher() => this.StartHelper();

        // The host loads plugin assemblies from memory, so Assembly.Location is empty and the DLL
        // cannot find its own folder; the plugin tells us where it lives once it has been told.
        public static String HelperDir { get; set; }

        public static String HelperPath =>
            String.IsNullOrEmpty(HelperDir) ? null : System.IO.Path.Combine(HelperDir, "deck-apps");

        // Safe to call repeatedly; starts the helper the first time its location is known.
        public void EnsureStarted()
        {
            lock (this._gate)
            {
                if (this._helper == null)
                {
                    this.StartHelper();
                }
            }
        }

        public static String IconDir => System.IO.Path.Combine(DeckConfig.Root, "icons");

        public IReadOnlyList<AppInfo> Apps => this._apps;

        // Bundle id of the app in front, or "" until the helper has reported.
        public String Front => this._front;

        public AppInfo Find(String id) => this._apps.FirstOrDefault(a => a.Id == id);

        // Bundle ids, most recently in front first.
        public IReadOnlyList<String> Recent
        {
            get
            {
                lock (this._gate)
                {
                    return this._recent.ToList();
                }
            }
        }

        public Boolean Hide(AppInfo app)
        {
            var helper = HelperPath;
            return app != null && helper != null && Shell.Run(helper, 3000, "hide", app.Pid.ToString()).Ok;
        }

        private void StartHelper()
        {
            if (this._disposed)
            {
                return;
            }

            var helper = HelperPath;
            if (helper == null)
            {
                // Not told yet; EnsureStarted will be along.
                return;
            }

            if (!File.Exists(helper))
            {
                PluginLog.Warning($"App helper not found at {helper}; the app switcher will be empty.");
                return;
            }

            try
            {
                var psi = new ProcessStartInfo(helper)
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("watch");
                psi.ArgumentList.Add(IconDir);

                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                p.OutputDataReceived += (_, e) => this.OnLine(e.Data);
                p.Exited += (_, _) => this.OnExited();
                p.Start();
                p.BeginOutputReadLine();
                this._helper = p;
                PluginLog.Info($"app helper started (pid {p.Id})");
            }
            catch (Exception ex)
            {
                PluginLog.Warning($"Could not start the app helper: {ex.Message}");
            }
        }

        private void OnExited()
        {
            if (this._disposed)
            {
                return;
            }

            // Back off so a helper that cannot run does not become a fork loop.
            var delay = Math.Min(30000, 1000 * (1 << Math.Min(5, this._restarts++)));
            PluginLog.Warning($"app helper exited; restarting in {delay} ms");
            System.Threading.Tasks.Task.Delay(delay).ContinueWith(_ => this.StartHelper());
        }

        private void OnLine(String line)
        {
            if (this._disposed || String.IsNullOrEmpty(line))
            {
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var apps = new List<AppInfo>();
                if (root.TryGetProperty("apps", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in list.EnumerateArray())
                    {
                        apps.Add(new AppInfo
                        {
                            Pid = a.TryGetProperty("pid", out var pid) && pid.TryGetInt32(out var n) ? n : 0,
                            Bundle = Str(a, "bundle"),
                            Name = Str(a, "name"),
                            Path = Str(a, "path"),
                            Icon = Str(a, "icon"),
                            Hidden = a.TryGetProperty("hidden", out var h) && h.ValueKind == JsonValueKind.True,
                            Launched = a.TryGetProperty("launched", out var l) && l.TryGetDouble(out var d) ? d : 0,
                        });
                    }
                }

                var front = Str(root, "front");
                lock (this._gate)
                {
                    // First report: there is no history yet, so newest launch stands in for it.
                    if (this._recent.Count == 0)
                    {
                        this._recent.AddRange(apps.OrderByDescending(a => a.Launched).Select(a => a.Id));
                    }

                    if (front.Length > 0)
                    {
                        this._recent.Remove(front);
                        this._recent.Insert(0, front);
                    }

                    var running = new HashSet<String>(apps.Select(a => a.Id), StringComparer.Ordinal);
                    this._recent.RemoveAll(id => !running.Contains(id));
                }

                this._apps = apps;
                this._front = front;
                this._restarts = 0;
                this.Changed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                PluginLog.Verbose($"unreadable line from the app helper: {ex.Message}");
            }
        }

        private static String Str(JsonElement e, String name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        public void Dispose()
        {
            if (this._disposed)
            {
                return;
            }

            this._disposed = true;
            this.Changed = null;
            try
            {
                // Closing stdin is the polite way; the kill is for a helper that is not listening.
                this._helper?.StandardInput.Close();
                if (this._helper != null && !this._helper.WaitForExit(500))
                {
                    this._helper.Kill();
                }
            }
            catch
            {
            }

            this._helper?.Dispose();
        }
    }
}
