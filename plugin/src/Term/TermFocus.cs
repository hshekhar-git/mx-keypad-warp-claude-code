namespace Loupedeck.ClaudeDeckPlugin
{
    using System;
    using System.Text.RegularExpressions;

    // Brings a session's terminal to the front. A Warp pane has a deep link that selects the exact
    // window, tab and split; anything else is activated by the bundle id its hook inherited, which
    // gets you to the right app if not the right tab.
    public static class TermFocus
    {
        private static readonly Regex HexUuid = new("^[0-9a-f]{32}$", RegexOptions.Compiled);
        private static readonly Regex BundleId = new(@"^[A-Za-z0-9][A-Za-z0-9.\-]{2,120}$", RegexOptions.Compiled);

        public static Boolean Focus(SessionInfo s)
        {
            if (s == null)
            {
                return false;
            }

            // Both values came out of a file and are about to be handed to /usr/bin/open, so both
            // are checked against exactly the shape they are allowed to have.
            if (HexUuid.IsMatch(s.WarpUuid ?? ""))
            {
                PluginLog.Info($"focus warp pane {s.WarpUuid} ({s.Project})");
                return Shell.Start("/usr/bin/open", $"warp://session/{s.WarpUuid}");
            }

            if (BundleId.IsMatch(s.Bundle ?? ""))
            {
                PluginLog.Info($"activate {s.Bundle} ({s.Project})");
                return Shell.Start("/usr/bin/open", "-b", s.Bundle);
            }

            PluginLog.Warning($"no way to focus session {s.Key}");
            return false;
        }
    }
}
