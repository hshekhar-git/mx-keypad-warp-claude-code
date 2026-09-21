namespace Loupedeck.ClaudeDeckPlugin
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;

    public sealed class PaneLocation
    {
        public Int32 WindowId { get; init; }
        public Int32 TabId { get; init; }
        public String TabTitle { get; init; } = "";

        // Position of the pane among its tab's panes, for a stable order of tiles.
        public Int32 Ordinal { get; init; }

        // Whether this is the pane with the keyboard: the focused leaf of the window's showing tab.
        public Boolean Focused { get; init; }
    }

    // Where each Warp pane sits. Warp persists its layout as a small SQLite database:
    //
    //   windows(id, active_tab_index)          tabs(id, window_id, custom_title)
    //   pane_nodes(id, tab_id, is_leaf)        pane_leaves(pane_node_id, is_focused)
    //   terminal_panes(id -> pane_nodes.id, uuid BLOB)
    //
    // The uuid is the same value a pane exports as WARP_TERMINAL_SESSION_UUID, which is what ties a
    // Claude session to a row here. The database is somebody else's private format, so it is only
    // ever opened read-only, and any failure - missing file, renamed column, locked database - just
    // means no locations: sessions then group by app instead of by tab and everything else carries on.
    public static class WarpTabs
    {
        // tabs carries no position, but Warp writes them in display order, so a tab's rank by id
        // within its window is its index - the thing windows.active_tab_index refers to.
        private const String Sql = @"
WITH ranked_tabs AS (
    SELECT id, window_id, custom_title,
           ROW_NUMBER() OVER (PARTITION BY window_id ORDER BY id) - 1 AS tab_index
    FROM tabs
)
SELECT lower(hex(p.uuid))                                          AS pane,
       t.window_id                                                 AS win,
       t.id                                                        AS tab,
       IFNULL(t.custom_title, '')                                  AS title,
       ROW_NUMBER() OVER (PARTITION BY t.id ORDER BY n.id) - 1     AS ordinal,
       (l.is_focused = 1 AND w.active_tab_index = t.tab_index)     AS focused
FROM ranked_tabs t
JOIN windows        w ON w.id = t.window_id
JOIN pane_nodes     n ON n.tab_id = t.id AND n.is_leaf = 1
JOIN pane_leaves    l ON l.pane_node_id = n.id
JOIN terminal_panes p ON p.id = n.id;";

        public static String DatabasePath
        {
            get
            {
                var support = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Group Containers", "2BBY89MBSN.dev.warp", "Library", "Application Support");

                // Stable first, then Preview; whichever exists.
                foreach (var channel in new[] { "dev.warp.Warp-Stable", "dev.warp.Warp-Preview" })
                {
                    var candidate = Path.Combine(support, channel, "warp.sqlite");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                return null;
            }
        }

        public static Dictionary<String, PaneLocation> Read()
        {
            var panes = new Dictionary<String, PaneLocation>(StringComparer.Ordinal);
            var database = DatabasePath;
            if (database == null)
            {
                return panes;
            }

            var run = Shell.Run("/usr/bin/sqlite3", 2000, "-readonly", "-json", database, Sql);
            if (!run.Ok || String.IsNullOrWhiteSpace(run.Output))
            {
                return panes;
            }

            try
            {
                using var rows = JsonDocument.Parse(run.Output);
                foreach (var row in rows.RootElement.EnumerateArray())
                {
                    var pane = row.GetProperty("pane").GetString();
                    if (String.IsNullOrEmpty(pane) || pane.Length != 32)
                    {
                        continue;
                    }

                    panes[pane] = new PaneLocation
                    {
                        WindowId = row.GetProperty("win").GetInt32(),
                        TabId = row.GetProperty("tab").GetInt32(),
                        TabTitle = row.GetProperty("title").GetString() ?? "",
                        Ordinal = row.GetProperty("ordinal").GetInt32(),
                        Focused = row.GetProperty("focused").ValueKind == JsonValueKind.Number
                            && row.GetProperty("focused").GetInt32() == 1,
                    };
                }
            }
            catch (Exception ex)
            {
                // A layout this does not understand is the same as no layout.
                PluginLog.Verbose($"Warp layout not understood: {ex.Message}");
                panes.Clear();
            }

            return panes;
        }
    }
}
