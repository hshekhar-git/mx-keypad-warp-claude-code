namespace Loupedeck.ClaudeDeckPlugin
{
    using System;
    using System.Collections.Generic;
    using System.IO;

    public sealed class PaneLocation
    {
        public Int32 WindowId { get; init; }
        public Int32 TabId { get; init; }
        public String TabTitle { get; init; } = "";
        public Int32 Ordinal { get; init; }

        // The pane with keyboard focus, in the tab that is showing, of its window.
        public Boolean Focused { get; init; }
    }

    // Warp keeps its window/tab/pane tree in SQLite. Read-only, through /usr/bin/sqlite3, and only to
    // decide which keypad page a session belongs on: if the schema ever changes the query fails, the
    // result is empty, and every Warp session simply lands on one page.
    public static class WarpTabs
    {
        private static readonly String[] Channels = { "dev.warp.Warp-Stable", "dev.warp.Warp-Preview" };

        private const String Query =
            "SELECT lower(hex(tp.uuid)), w.id, t.id, COALESCE(t.custom_title,''), " +
            // tabs has no position column; rows are rewritten in display order, so rank by id.
            "(pl.is_focused AND w.active_tab_index = " +
            "(SELECT COUNT(*) FROM tabs t2 WHERE t2.window_id = w.id AND t2.id < t.id)) " +
            "FROM terminal_panes tp " +
            "JOIN pane_leaves pl ON pl.pane_node_id = tp.id " +
            "JOIN pane_nodes  pn ON pn.id = tp.id " +
            "JOIN tabs        t  ON t.id  = pn.tab_id " +
            "JOIN windows     w  ON w.id  = t.window_id " +
            "ORDER BY w.id, t.id, tp.id;";

        public static String DatabasePath
        {
            get
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                foreach (var channel in Channels)
                {
                    var path = Path.Combine(
                        home, "Library/Group Containers/2BBY89MBSN.dev.warp/Library/Application Support", channel, "warp.sqlite");
                    if (File.Exists(path))
                    {
                        return path;
                    }
                }

                return null;
            }
        }

        public static Dictionary<String, PaneLocation> Read()
        {
            var result = new Dictionary<String, PaneLocation>(StringComparer.Ordinal);
            var db = DatabasePath;
            if (db == null)
            {
                return result;
            }

            var run = Shell.Run("/usr/bin/sqlite3", 2000, "-readonly", "-noheader", "-separator", "\u001f", db, Query);
            if (!run.Ok)
            {
                return result;
            }

            var lastWindow = Int32.MinValue;
            var lastTab = Int32.MinValue;
            var ordinal = 0;
            foreach (var line in run.Output.Split('\n'))
            {
                var parts = line.Split('\u001f');
                if (parts.Length < 5
                    || parts[0].Length != 32
                    || !Int32.TryParse(parts[1], out var windowId)
                    || !Int32.TryParse(parts[2], out var tabId))
                {
                    continue;
                }

                if (windowId != lastWindow || tabId != lastTab)
                {
                    lastWindow = windowId;
                    lastTab = tabId;
                    ordinal = 0;
                }

                result[parts[0]] = new PaneLocation
                {
                    WindowId = windowId,
                    TabId = tabId,
                    TabTitle = parts[3].Trim('\r'),
                    Ordinal = ordinal++,
                    Focused = parts[4].Trim('\r') == "1",
                };
            }

            return result;
        }
    }
}
