namespace Loupedeck.ClaudeDeckPlugin
{
    using System;
    using System.Linq;

    // Keystrokes into a terminal, through System Events.
    //
    // There is exactly one script, and it does its own gatekeeping: it is told which applications are
    // acceptable, looks at what is in front at the moment it runs, and sends nothing if that is
    // anything else. Checking first and typing afterwards from C# would leave a gap in which the
    // user can switch apps; inside the script the check and the keystroke are one step. Text travels
    // as an argument to the script, never as part of it, so nothing a session wrote can run as code.
    public static class TermInput
    {
        // Terminals a command key may type into when it is not aimed at one session in particular.
        private static readonly String[] Terminals =
        {
            "dev.warp.Warp-Stable", "dev.warp.Warp-Preview", "com.apple.Terminal", "com.googlecode.iterm2",
            "com.mitchellh.ghostty", "net.kovidgoyal.kitty", "com.github.wez.wezterm", "org.alacritty",
        };

        private const String Gatekeeper = @"
on run {allowedApps, whatToDo, theText}
    tell application ""System Events""
        set inFront to bundle identifier of (first application process whose frontmost is true)
        set AppleScript's text item delimiters to "",""
        if (text items of allowedApps) does not contain inFront then return ""refused "" & inFront

        if whatToDo is ""escape"" then
            key code 53
        else if whatToDo is ""backtab"" then
            key code 48 using shift down
        else
            if (count of theText) > 0 then keystroke theText
            if whatToDo is ""submit"" then
                -- Claude Code opens a menu as a slash command is typed; give it a moment to settle,
                -- or the Return can land before the command it is meant to run.
                if (count of theText) > 0 then delay 0.2
                key code 36 -- Return
            end if
        end if
    end tell
    return ""sent""
end run";

        // Raised once per denial, so the plugin can tell the user what macOS wants from them.
        public static event EventHandler AccessibilityDenied;

        private static Boolean _denialReported;

        public static void Shutdown() => AccessibilityDenied = null;

        public static String Frontmost()
        {
            var run = Shell.Run("/usr/bin/osascript", 4000, "-e",
                "tell application \"System Events\" to bundle identifier of (first application process whose frontmost is true)");
            return run.Ok ? run.Output.Trim() : "";
        }

        public static Boolean TypeText(String onlyInto, String text, Boolean submit)
        {
            text ??= "";
            return (text.Length > 0 || submit) && Perform(onlyInto, submit ? "submit" : "type", text);
        }

        public static Boolean SendEscape(String onlyInto) => Perform(onlyInto, "escape", "");

        // Shift-Tab: what Claude Code steps its permission mode with.
        public static Boolean SendShiftTab(String onlyInto) => Perform(onlyInto, "backtab", "");

        private static Boolean Perform(String onlyInto, String action, String text)
        {
            var allowed = String.IsNullOrEmpty(onlyInto) ? String.Join(",", Terminals) : onlyInto;
            var run = Shell.Run("/usr/bin/osascript", 5000, "-e", Gatekeeper, allowed, action, text);
            var said = (run.Ok ? run.Output : run.Error).Trim();

            if (said == "sent")
            {
                _denialReported = false;
                PluginLog.Info(action is "type" or "submit" ? $"{action}: \"{text}\"" : action);
                return true;
            }

            if (said.StartsWith("refused ", StringComparison.Ordinal))
            {
                PluginLog.Warning($"{action} not sent: {said.Substring(8)} is in front, which is not {(String.IsNullOrEmpty(onlyInto) ? "a terminal" : onlyInto)}");
                return false;
            }

            // macOS answers a process without Accessibility rights with error 1002 / -1719 / -25211.
            PluginLog.Warning($"{action} failed: {said}");
            var denied = new[] { "1002", "-1719", "-25211", "assistive", "not allowed" }
                .Any(marker => said.Contains(marker, StringComparison.OrdinalIgnoreCase));
            if (denied && !_denialReported)
            {
                _denialReported = true;
                AccessibilityDenied?.Invoke(null, EventArgs.Empty);
            }

            return false;
        }
    }
}
