namespace Loupedeck.ClaudeDeckPlugin
{
    using System;
    using System.Linq;

    // Bringing a session's terminal forward.
    //
    // Warp registers a URL scheme, and warp://session/<pane uuid> selects that exact pane - window,
    // tab and split. No other terminal offers the like, so for those the application the hook was
    // running under is activated by its bundle id: the right app, if not the right tab.
    public static class TermFocus
    {
        public static Boolean Focus(SessionInfo session)
        {
            if (session == null)
            {
                return false;
            }

            // Both values were read from a file and are on their way to /usr/bin/open, so each has to
            // look exactly like what it claims to be before it goes anywhere.
            if (IsPaneId(session.WarpUuid))
            {
                PluginLog.Info($"to pane {session.WarpUuid} ({session.Project})");
                return Shell.Start("/usr/bin/open", "warp://session/" + session.WarpUuid);
            }

            if (IsBundleId(session.Bundle))
            {
                PluginLog.Info($"to app {session.Bundle} ({session.Project})");
                return Shell.Start("/usr/bin/open", "-b", session.Bundle);
            }

            PluginLog.Warning($"session {session.Key} has neither a Warp pane nor an app to go to");
            return false;
        }

        private static Boolean IsPaneId(String value) =>
            value is { Length: 32 } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

        private static Boolean IsBundleId(String value) =>
            value is { Length: >= 3 and <= 150 }
            && Char.IsLetterOrDigit(value[0])
            && value.All(c => Char.IsLetterOrDigit(c) || c is '.' or '-' or '_');
    }
}
