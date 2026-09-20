namespace Loupedeck.ClaudeDeckPlugin
{
    using System;

    // Typing into a terminal, via System Events. Every send names the application it expects to be
    // in front and refuses otherwise, inside the same script that types, so a mistimed press cannot
    // put "/clear" - or a "1" meant for a permission prompt - into your editor.
    public static class TermInput
    {
        public static event EventHandler AccessibilityDenied;

        private static Boolean _reported;

        // argv: expected bundle id ("" = any known terminal), text, "1" to press Return, "1" for Escape
        private const String Script = @"
on run argv
    tell application ""System Events""
        set frontId to bundle identifier of first application process whose frontmost is true
        set expected to item 1 of argv
        if expected is """" then
            if frontId is not in {""dev.warp.Warp-Stable"", ""dev.warp.Warp-Preview"", ""com.apple.Terminal"", ""com.googlecode.iterm2"", ""com.mitchellh.ghostty"", ""net.kovidgoyal.kitty"", ""com.github.wez.wezterm"", ""org.alacritty""} then return ""not-front:"" & frontId
        else
            if frontId is not expected then return ""not-front:"" & frontId
        end if
        if (item 4 of argv) is ""1"" then
            key code 53
        else
            set theText to item 2 of argv
            if theText is not """" then keystroke theText
            if (item 3 of argv) is ""1"" then key code 36
        end if
    end tell
    return ""ok""
end run";

        public static String Frontmost()
        {
            var run = Shell.Run("/usr/bin/osascript", 4000, "-e",
                "tell application \"System Events\" to get bundle identifier of first application process whose frontmost is true");
            return run.Ok ? run.Output.Trim() : "";
        }

        public static Boolean TypeText(String expectedBundle, String text, Boolean submit)
        {
            text ??= "";
            if (text.Length == 0 && !submit)
            {
                return false;
            }

            return Send(expectedBundle, text, submit, false, $"type \"{text}\"{(submit ? " + Return" : "")}");
        }

        public static Boolean SendEscape(String expectedBundle) => Send(expectedBundle, "", false, true, "send Escape");

        public static void Shutdown() => AccessibilityDenied = null;

        private static Boolean Send(String expectedBundle, String text, Boolean submit, Boolean escape, String what)
        {
            var run = Shell.Run("/usr/bin/osascript", 5000,
                "-e", Script, expectedBundle ?? "", text, submit ? "1" : "0", escape ? "1" : "0");
            var result = (run.Ok ? run.Output : run.Error).Trim();

            if (result == "ok")
            {
                _reported = false;
                PluginLog.Info($"did {what}");
                return true;
            }

            if (result.StartsWith("not-front:", StringComparison.Ordinal))
            {
                PluginLog.Warning($"Did not {what}: {result.Substring(10)} is in front, not {(String.IsNullOrEmpty(expectedBundle) ? "a terminal" : expectedBundle)}.");
                return false;
            }

            PluginLog.Warning($"Could not {what}: {result}");
            var denied = result.Contains("-1719", StringComparison.Ordinal)
                || result.Contains("-25211", StringComparison.Ordinal)
                || result.Contains("not allowed", StringComparison.OrdinalIgnoreCase);
            if (denied && !_reported)
            {
                _reported = true;
                AccessibilityDenied?.Invoke(null, EventArgs.Empty);
            }

            return false;
        }
    }
}
