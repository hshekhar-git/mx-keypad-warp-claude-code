namespace Loupedeck.ClaudeDeckPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;

    // Every external program this plugin runs goes through here, with its arguments passed as a
    // list - never through a shell - so nothing read from a session file can be interpreted.
    internal static class Shell
    {
        public sealed class Result
        {
            public Int32 ExitCode { get; init; }
            public String Output { get; init; } = "";
            public String Error { get; init; } = "";
            public Boolean Ok => this.ExitCode == 0;
        }

        public static Result Run(String exe, Int32 timeoutMs, params String[] args) => RunIn(null, null, exe, timeoutMs, args);

        // The same, from a given directory and with extra environment variables.
        public static Result RunIn(String directory, IReadOnlyDictionary<String, String> environment, String exe, Int32 timeoutMs, params String[] args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                if (!String.IsNullOrEmpty(directory))
                {
                    psi.WorkingDirectory = directory;
                }

                foreach (var (name, value) in environment ?? new Dictionary<String, String>())
                {
                    psi.Environment[name] = value;
                }

                foreach (var a in args)
                {
                    psi.ArgumentList.Add(a);
                }

                using var p = Process.Start(psi);
                if (p == null)
                {
                    return new Result { ExitCode = -1, Error = "no-process" };
                }

                // Nothing run from here is interactive; a child that asks is told there is no one.
                p.StandardInput.Close();

                // Read stderr off-thread so a chatty child cannot deadlock against a full pipe.
                var errTask = p.StandardError.ReadToEndAsync();
                var output = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(timeoutMs))
                {
                    try
                    {
                        p.Kill(true);
                    }
                    catch
                    {
                    }

                    return new Result { ExitCode = -2, Error = "timeout" };
                }

                return new Result { ExitCode = p.ExitCode, Output = output, Error = errTask.Result };
            }
            catch (Exception ex)
            {
                return new Result { ExitCode = -3, Error = ex.Message };
            }
        }

        // Fire and forget, for `open`.
        public static Boolean Start(String exe, params String[] args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var a in args)
                {
                    psi.ArgumentList.Add(a);
                }

                using var p = Process.Start(psi);
                return p != null;
            }
            catch (Exception ex)
            {
                PluginLog.Warning($"Could not start {exe}: {ex.Message}");
                return false;
            }
        }
    }
}
