namespace Loupedeck.ClaudeDeckPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;

    // The tone your Mac sounds when something stops a session: it is blocked on you, its turn died,
    // or its claude process went away mid-turn. A set number of tones and then quiet - a tap on the
    // shoulder, not an alarm that rings until somebody comes.
    //
    // There is only ever one run of tones. A session stopping while another's are still sounding
    // starts the count again instead of ringing over it, and a session that gets going again takes
    // its tones with it: answer the prompt after the second tone and there is no third.
    public static class Beep
    {
        private const String Player = "/usr/bin/afplay";

        private static readonly Object Gate = new();

        // The stopped sessions the tones are for, and how many tones are still to come.
        private static readonly HashSet<String> Stopped = new(StringComparer.Ordinal);
        private static Int32 _left;
        private static Boolean _sounding;

        // A session changed state. A stop the config asks to hear about rings; any other change
        // means the session has moved on from whatever it was ringing for.
        public static void On(TransitionEventArgs change)
        {
            var session = change.Session;
            var rings = session.State switch
            {
                "attention" => DeckConfig.BeepAttention,
                "error" => DeckConfig.BeepError,

                // The haptic's rule: a short turn finishing is one you are still watching.
                "done" => DeckConfig.BeepDone && change.FinishedAfter(DeckConfig.HapticMinTurnSeconds),
                _ => false,
            };

            if (rings)
            {
                Ring(session, session.State);
                return;
            }

            lock (Gate)
            {
                Stopped.Remove(session.Key);
            }
        }

        // A session's claude process went away with its turn still running.
        public static void OnGone(SessionInfo session)
        {
            if (DeckConfig.BeepGone)
            {
                Ring(session, "gone");
            }
        }

        public static void Shutdown()
        {
            lock (Gate)
            {
                Stopped.Clear();
                _left = 0;
            }
        }

        private static void Ring(SessionInfo session, String why)
        {
            var times = DeckConfig.BeepTimes;
            if (times < 1)
            {
                return;
            }

            PluginLog.Info($"{session.Project} stopped ({why}): {times} tone(s)");
            lock (Gate)
            {
                Stopped.Add(session.Key);
                _left = times;
                if (_sounding)
                {
                    return;
                }

                _sounding = true;
            }

            Task.Run(Sound);
        }

        // Each tone is started and left to play: afplay takes a second or two to come back even from
        // a short sound, and waiting for it would turn four brisk tones into a slow march. The beat
        // is a timer's, not a pause after each start, so that starting the player - which takes a
        // moment of its own - does not stretch the interval.
        private static async Task Sound()
        {
            using var beat = new PeriodicTimer(TimeSpan.FromMilliseconds(DeckConfig.BeepEveryMs));
            while (Next())
            {
                var volume = DeckConfig.BeepVolume.ToString("0.##", CultureInfo.InvariantCulture);
                Shell.Start(Player, "-v", volume, DeckConfig.BeepSound);
                await beat.WaitForNextTickAsync();
            }
        }

        // Takes one tone off the count. False, with everything reset, when there is none to play:
        // the count has run out, or every session it was for is running again.
        private static Boolean Next()
        {
            lock (Gate)
            {
                if (_left > 0 && Stopped.Count > 0)
                {
                    _left--;
                    return true;
                }

                Stopped.Clear();
                _left = 0;
                _sounding = false;
                return false;
            }
        }
    }
}
