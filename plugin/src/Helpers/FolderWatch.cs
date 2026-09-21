namespace Loupedeck.ClaudeDeckPlugin
{
    using System;
    using System.IO;
    using System.Threading;

    // Tells you that a folder's contents have changed - once per burst of changes, and at a steady
    // interval besides.
    //
    // Two things file notifications alone do not give you. Bursts: a busy session rewrites its file
    // several times a second, and each write should not mean a full re-read, so notifications only
    // (re)start a short countdown and the callback runs when it expires. And things that happen
    // without any file changing - a process dying, a countdown on a tile - which is what the steady
    // interval is for. If the OS refuses to watch the folder at all, the interval still works.
    public sealed class FolderWatch : IDisposable
    {
        private readonly Action _onChange;
        private readonly Int32 _settleMs;
        private readonly Timer _settle;
        private readonly Timer _heartbeat;
        private readonly FileSystemWatcher _files;
        private Int32 _stopped;

        public FolderWatch(String folder, String pattern, Int32 settleMs, Int32 heartbeatMs, Action onChange)
        {
            this._onChange = onChange;
            this._settleMs = settleMs;
            this._settle = new Timer(_ => this.Fire(), null, Timeout.Infinite, Timeout.Infinite);
            this._heartbeat = new Timer(_ => this.Fire(), null, heartbeatMs, heartbeatMs);

            try
            {
                Directory.CreateDirectory(folder);
                this._files = new FileSystemWatcher(folder, pattern) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
                FileSystemEventHandler touched = (_, _) => this.Nudge();
                this._files.Created += touched;
                this._files.Changed += touched;
                this._files.Deleted += touched;
                this._files.Renamed += (_, _) => this.Nudge();
                this._files.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                PluginLog.Warning($"cannot watch {folder} ({ex.Message}); relying on the {heartbeatMs} ms check alone");
            }
        }

        // "Something changed - look again soon." Safe to call from anywhere, any number of times.
        public void Nudge()
        {
            if (Volatile.Read(ref this._stopped) != 0)
            {
                return;
            }

            try
            {
                this._settle.Change(this._settleMs, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
                // Lost a race with Dispose: there is nobody left to tell.
            }
        }

        private void Fire()
        {
            if (Volatile.Read(ref this._stopped) == 0)
            {
                this._onChange();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref this._stopped, 1) != 0)
            {
                return;
            }

            try
            {
                this._files?.Dispose();
            }
            catch
            {
            }

            this._settle.Dispose();
            this._heartbeat.Dispose();
        }
    }
}
