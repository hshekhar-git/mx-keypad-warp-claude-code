namespace Loupedeck.ClaudeDeckPlugin
{
    using System;

    // The plugin's log, reachable from static code. The host gives each plugin its own log file
    // (…/LogiPluginService/Logs/plugin_logs/ClaudeDeck.log); until the plugin has been handed it -
    // and in tools that load this assembly without a host at all - logging quietly does nothing.
    internal static class PluginLog
    {
        private static Action<String> _verbose = _ => { };
        private static Action<String> _info = _ => { };
        private static Action<String> _warning = _ => { };
        private static Action<Exception, String> _error = (_, _) => { };

        public static void Init(PluginLogFile file)
        {
            if (file == null)
            {
                return;
            }

            // The host's methods report success; nothing here has any use for that.
            _verbose = message => file.Verbose(message);
            _info = message => file.Info(message);
            _warning = message => file.Warning(message);
            _error = (exception, message) => file.Error(exception, message);
        }

        public static void Verbose(String message) => _verbose(message);

        public static void Info(String message) => _info(message);

        public static void Warning(String message) => _warning(message);

        public static void Error(Exception exception, String message) => _error(exception, message);
    }
}
