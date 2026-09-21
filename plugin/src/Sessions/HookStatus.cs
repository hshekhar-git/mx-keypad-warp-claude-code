namespace Loupedeck.ClaudeDeckPlugin
{
    using System;
    using System.IO;

    // Whether the Claude Code hooks are installed. Read-only: this plugin never edits
    // ~/.claude/settings.json itself - hooks/install-hooks.sh does, when you run it.
    public static class HookStatus
    {
        private static DateTime _checkedAt = DateTime.MinValue;
        private static Boolean _installed;

        public static Boolean Installed
        {
            get
            {
                if (DateTime.UtcNow - _checkedAt < TimeSpan.FromSeconds(5))
                {
                    return _installed;
                }

                _checkedAt = DateTime.UtcNow;
                try
                {
                    var path = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");
                    _installed = File.Exists(path) && File.ReadAllText(path).Contains("deck-hook.sh", StringComparison.Ordinal);
                }
                catch
                {
                    _installed = false;
                }

                return _installed;
            }
        }
    }
}
