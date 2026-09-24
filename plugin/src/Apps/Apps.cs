namespace Loupedeck.ClaudeDeckPlugin
{
    using System;
    using System.Linq;
    using System.Text.RegularExpressions;

    public static class Apps
    {
        private static readonly Regex BundleId = new(@"^[A-Za-z0-9][A-Za-z0-9.\-_]{2,150}$", RegexOptions.Compiled);

        // Through LaunchServices rather than NSRunningApplication.activate: macOS only lets the app
        // that is already in front hand focus over that way, and a background service never is.
        public static Boolean Activate(AppInfo app)
        {
            if (app == null)
            {
                return false;
            }

            PluginLog.Info($"switching to {app.Name}");
            if (BundleId.IsMatch(app.Bundle))
            {
                return Shell.Start("/usr/bin/open", "-b", app.Bundle);
            }

            return app.Path.StartsWith("/", StringComparison.Ordinal)
                && app.Path.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
                && Shell.Start("/usr/bin/open", app.Path);
        }

        // Launches (or activates) by bundle id or by name, for an app that may not be running.
        public static Boolean Open(String idOrName)
        {
            idOrName = (idOrName ?? "").Trim();
            if (idOrName.Length == 0 || idOrName.StartsWith("-", StringComparison.Ordinal))
            {
                return false;
            }

            var running = Resolve(idOrName);
            if (running != null)
            {
                return Activate(running);
            }

            return idOrName.Contains('.') && BundleId.IsMatch(idOrName)
                ? Shell.Start("/usr/bin/open", "-b", idOrName)
                : Shell.Start("/usr/bin/open", "-a", idOrName);
        }

        public static AppInfo Resolve(String idOrName) =>
            AppWatcher.Instance.Apps.FirstOrDefault(a =>
                String.Equals(a.Bundle, idOrName, StringComparison.OrdinalIgnoreCase)
                || String.Equals(a.Name, idOrName, StringComparison.OrdinalIgnoreCase));
    }
}
