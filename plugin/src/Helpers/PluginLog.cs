namespace Loupedeck.ClaudeDeckPlugin
{
    using System;

    internal static class PluginLog
    {
        private static PluginLogFile _file;

        public static void Init(PluginLogFile file) => _file = file;

        public static void Verbose(String text) => _file?.Verbose(text);

        public static void Info(String text) => _file?.Info(text);

        public static void Warning(String text) => _file?.Warning(text);

        public static void Error(Exception ex, String text) => _file?.Error(ex, text);
    }
}
