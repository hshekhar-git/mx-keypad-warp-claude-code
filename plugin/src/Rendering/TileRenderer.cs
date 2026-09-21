namespace Loupedeck.ClaudeDeckPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    // Everything drawn on a key. A tile is filled with its state colour so the deck reads from across
    // the room; the text is for when you are close enough to care which session it is.
    public static class TileRenderer
    {
        // The palette. White text sits on every one of these, so each was checked for a contrast
        // ratio of at least 5:1 against white.
        private static readonly BitmapColor Busy = new(0xA8, 0x52, 0x1E);
        private static readonly BitmapColor Done = new(0x25, 0x78, 0x4A);
        private static readonly BitmapColor Attention = new(0xB3, 0x28, 0x2D);
        private static readonly BitmapColor Error = new(0x74, 0x39, 0x8C);
        private static readonly BitmapColor Idle = new(0x4E, 0x53, 0x59);
        private static readonly BitmapColor Neutral = new(0x36, 0x3A, 0x40);
        private static readonly BitmapColor Amber = new(0x8A, 0x60, 0x11);
        private static readonly BitmapColor Empty = new(0x12, 0x14, 0x17);
        private static readonly BitmapColor Warn = new(0xF4, 0xC5, 0x42);
        private static readonly BitmapColor Blue = new(0x2A, 0x6C, 0xB0);
        private static readonly BitmapColor Violet = new(0x67, 0x50, 0xB5);

        public const Int32 FrameMs = 250;

        public static BitmapColor ColourOf(String state) => state switch
        {
            "busy" => Busy,
            "done" => Done,
            "attention" => Attention,
            "error" => Error,
            _ => Idle,
        };

        // A blink is half a second bright, half a second dim: two ticks each way.
        private static Boolean IsDimBeat(Int32 frame) => (frame & 2) != 0;

        // How long a tile celebrates a finished turn before settling down.
        private const Int64 SparkleSeconds = 3;

        private static Boolean JustFinished(SessionInfo s) =>
            DeckConfig.Ascii && s.State == "done" && !s.IsLimited
            && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - s.Since < SparkleSeconds;

        // One second longer than the sparkle itself, so the last repaint is of the settled tile.
        public static Boolean Animates(SessionInfo s) =>
            s.State is "busy" or "attention"
            || (DeckConfig.Ascii && s.State == "done" && !s.IsLimited
                && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - s.Since <= SparkleSeconds);

        public static BitmapImage Session(SessionInfo s, Boolean selected, Boolean flash, PluginImageSize size, Int32 frame, String header = null)
        {
            // Out of usage is its own colour: it is neither an error to fix nor a turn to take.
            var bg = s.IsLimited ? Amber : ColourOf(s.State);
            if (s.State == "attention" && IsDimBeat(frame))
            {
                bg = Shade(bg, 0.5);
            }

            using var b = new BitmapBuilder(size);
            var w = b.Width;
            var h = b.Height;
            b.Clear(bg);

            var fg = BitmapColor.White;
            var soft = Tint(bg, 0.82);

            var top = DeckConfig.ShowContext ? DrawContext(b, s, bg) : 0;

            // Project, what it is about, what it is doing right now.
            Band(b, header ?? Middle(Or(s.Project, "—"), 15), 0.03, 0.17, 11, header != null ? fg : soft, top);
            Band(b, What(s), 0.20, 0.50, 13, fg, top);
            // The mark and the words are one string, so the mark sits beside the words and on their
            // baseline by construction - which also means it has to come from the key's main font.
            Band(b, DeckConfig.Ascii ? Ascii.Status(s, Status(s), frame, JustFinished(s)) : Status(s), 0.74, 0.18, 11, soft);

            if (s.State == "busy")
            {
                if (DeckConfig.Ascii)
                {
                    b.DrawText(Ascii.Wave(frame, 19), 0, (Int32)(h * 0.905), w, (Int32)(h * 0.11), Tint(bg, 0.55), 9);
                }
                else
                {
                    DrawWorking(b, bg, frame);
                }
            }

            // The session the command row will act on.
            if (selected)
            {
                var t = Math.Max(2, (Int32)(w * 0.025));
                var ring = Tint(bg, 0.9);
                b.FillRectangle(0, 0, t, h, ring);
                b.FillRectangle(w - t, 0, t, h, ring);
            }

            if (flash)
            {
                DrawFlash(b);
            }

            return b.ToImage();
        }

        // A thin gauge along the top edge: how full the context window is. Returns the space it used.
        private static Int32 DrawContext(BitmapBuilder b, SessionInfo s, BitmapColor bg)
        {
            var fill = s.ContextFill;
            if (fill < 0)
            {
                return 0;
            }

            var barH = Math.Max(4, (Int32)(b.Height * 0.045));
            b.FillRectangle(0, 0, b.Width, barH, Shade(bg, 0.4));
            var filled = Math.Max(2, (Int32)(b.Width * fill));
            b.FillRectangle(0, 0, filled, barH, fill >= 0.8 ? Warn : Tint(bg, 0.7));
            return barH;
        }

        // Working: three dots along the bottom edge, lit one after another like a typing indicator.
        // Quiet enough to live with for an hour, and unmistakable from across the room.
        private static void DrawWorking(BitmapBuilder b, BitmapColor bg, Int32 frame)
        {
            var radius = Math.Max(3, b.Width / 32);
            var spacing = radius * 4;
            var y = b.Height - radius - 3;
            var first = (b.Width / 2) - spacing;
            var lit = frame % 4;
            for (var dot = 0; dot < 3; dot++)
            {
                b.FillCircle(first + (dot * spacing), y, radius, dot == lit ? BitmapColor.White : Blend(bg, BitmapColor.Black, 0.35));
            }
        }

        // The main text. A session blocked on a permission prompt shows the thing it wants to do,
        // since that is what you need in order to answer it.
        private static String What(SessionInfo s)
        {
            if (s.NeedsPermission && s.Detail.Length > 0)
            {
                return End(Wrappable(s.Detail), 44);
            }

            // Likewise a question: the wording is what you need, not the session's name.
            if (s.State == "attention" && s.Kind == "question" && s.Question.Length > 0)
            {
                return End(s.Question, 44);
            }

            var slug = FromSlug(s.Slug);
            var text = DeckConfig.Label switch
            {
                "prompt" => Or(s.Prompt, s.Title, slug),
                "slug" => Or(slug, s.Title, s.Prompt),
                _ => Or(s.Title, s.Prompt, slug),
            };
            return End(Or(text, s.Branch), 44);
        }

        private static String Status(SessionInfo s)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (s.IsLimited)
            {
                return $"limit · {s.Limit}";
            }

            switch (s.State)
            {
                case "busy":
                    var turn = s.TurnSince > 0 ? s.TurnSince : s.Since;
                    return $"{Or(ToolName(s.Tool), "thinking")} {Elapsed(now - turn)}";
                case "attention":
                    return s.Kind switch
                    {
                        "question" => "asks you",
                        "plan" => "plan ready",
                        _ => $"allow {Or(ToolName(s.Tool), "it")}?",
                    };
                // A resting tile has room to say which model it is on; a working one does not.
                case "done":
                    return $"done {Elapsed(now - s.Since)}{ModelTag(s)}";
                case "error":
                    return $"error{ModelTag(s)}";
                default:
                    return $"idle{ModelTag(s)}";
            }
        }

        private static String ModelTag(SessionInfo s) => s.Selected.IsKnown ? $" · {s.Selected.Short}" : "";

        public static String ToolName(String tool)
        {
            if (String.IsNullOrEmpty(tool))
            {
                return "";
            }

            // mcp__server__do_thing -> do_thing
            var cut = tool.LastIndexOf("__", StringComparison.Ordinal);
            var name = cut >= 0 && cut + 2 < tool.Length ? tool.Substring(cut + 2) : tool;
            return name.Length > 11 ? name.Substring(0, 10) + "…" : name;
        }

        private static String Elapsed(Int64 seconds)
        {
            seconds = Math.Max(0, seconds);
            if (seconds < 60)
            {
                return $"{seconds}s";
            }

            return seconds < 3600
                ? $"{seconds / 60}:{seconds % 60:00}"
                : $"{seconds / 3600}h{seconds % 3600 / 60:00}";
        }

        // A session slug is kebab-case; as tile text it just reads better with spaces.
        private static String FromSlug(String slug) =>
            String.IsNullOrWhiteSpace(slug) ? "" : slug.Trim().Replace('-', ' ');

        // ---- command keys -------------------------------------------------------------------

        public static BitmapImage Command(String label, String color, Boolean flash, PluginImageSize size)
        {
            using var b = new BitmapBuilder(size);
            return Command(b, label, color, flash);
        }

        public static BitmapImage Command(String label, String color, Int32 width, Int32 height)
        {
            using var b = new BitmapBuilder(width, height);
            return Command(b, label, color, false);
        }

        private static BitmapImage Command(BitmapBuilder b, String label, String color, Boolean flash)
        {
            var bg = Named(color);
            b.Clear(bg);
            // Answer keys carry whole option labels, so long text wraps in a taller box at a smaller size.
            if (label.Length > 10)
            {
                Band(b, End(label, 40), 0.10, 0.80, 13, BitmapColor.White);
            }
            else
            {
                Band(b, label, 0.28, 0.44, label.Length > 7 ? 14 : 17, BitmapColor.White);
            }

            if (flash)
            {
                DrawFlash(b);
            }

            return b.ToImage();
        }

        private static BitmapColor Named(String color) => (color ?? "").ToLowerInvariant() switch
        {
            "green" => Done,
            "amber" => Amber,
            "red" => Attention,
            "coral" => Busy,
            "purple" => Error,
            "blue" => Blue,
            "violet" => Violet,
            _ => Neutral,
        };

        // ---- plan usage ---------------------------------------------------------------------

        private static readonly BitmapColor UsageCalm = new(0x4A, 0x8F, 0xE0);
        private static readonly BitmapColor UsageHigh = new(0xF2, 0xA3, 0x3A);
        private static readonly BitmapColor UsageFull = new(0xE8, 0x5D, 0x52);

        private static BitmapColor UsageColor(Double percent) =>
            percent >= 90 ? UsageFull : percent >= 75 ? UsageHigh : UsageCalm;

        // One usage window: how much is gone, as a number and a bar, and when it comes back.
        public static BitmapImage Usage(UsageWindow window, TimeSpan age, PluginImageSize size)
        {
            using var b = new BitmapBuilder(size);
            var w = b.Width;
            var h = b.Height;
            b.Clear(Empty);
            var soft = Tint(Empty, 0.5);
            Band(b, window.Title, 0.05, 0.18, 11, soft);

            if (!window.IsKnown)
            {
                Band(b, "–", 0.26, 0.34, 22, Tint(Empty, 0.3));
                Band(b, "no data yet", 0.74, 0.18, 11, Tint(Empty, 0.35));
                return b.ToImage();
            }

            // It only moves while a session is drawing its status line, so old numbers say so.
            var stale = age > TimeSpan.FromMinutes(20);
            var color = stale ? Tint(Empty, 0.45) : UsageColor(window.Percent);
            Band(b, $"{(Int32)Math.Round(window.Percent)}%", 0.20, 0.36, 22, color);

            var barX = (Int32)(w * 0.12);
            var barW = w - (2 * barX);
            var barH = Math.Max(5, (Int32)(h * 0.06));
            var barY = (Int32)(h * 0.60);
            b.FillRectangle(barX, barY, barW, barH, Tint(Empty, 0.14));
            b.FillRectangle(barX, barY, Math.Max(2, (Int32)(barW * Math.Min(1.0, window.Percent / 100.0))), barH, color);

            var bottom = stale ? $"{Span(age)} old" : window.ResetsAt > DateTime.Now ? ResetText(window.ResetsAt) : "";
            Band(b, bottom, 0.74, 0.18, 11, soft);
            return b.ToImage();
        }

        // What the usage page does not tell you: whether you make it to the reset.
        public static BitmapImage Pace(UsageWindow session, TimeSpan age, PluginImageSize size)
        {
            using var b = new BitmapBuilder(size);
            var w = b.Width;
            var h = b.Height;
            b.Clear(Empty);
            var soft = Tint(Empty, 0.5);
            Band(b, "pace", 0.05, 0.18, 11, soft);

            String big, bottom;
            BitmapColor color;
            var (projected, runsOut) = UsageStore.Pace(session, TimeSpan.FromHours(5));
            if (!session.IsKnown || age > TimeSpan.FromMinutes(20))
            {
                (big, bottom, color) = ("–", session.IsKnown ? "stale" : "no data yet", Tint(Empty, 0.3));
            }
            else if (session.Percent >= 100)
            {
                (big, bottom, color) = ("limit", session.ResetsAt > DateTime.Now ? ResetText(session.ResetsAt) : "", UsageFull);
            }
            else if (projected < 0)
            {
                (big, bottom, color) = ("–", "too early to say", Tint(Empty, 0.4));
            }
            else if (runsOut != DateTime.MinValue)
            {
                (big, bottom, color) = (Span(runsOut - DateTime.Now), "until you run out", runsOut - DateTime.Now < TimeSpan.FromMinutes(30) ? UsageFull : UsageHigh);
            }
            else
            {
                (big, bottom, color) = ($"~{(Int32)Math.Round(projected)}%", "by the reset", new BitmapColor(0x4C, 0xB8, 0x6E));
            }

            Band(b, big, 0.22, 0.40, big.Length > 4 ? 18 : 22, color);
            Band(b, bottom, 0.72, 0.20, 11, soft);
            return b.ToImage();
        }

        private static String Span(TimeSpan t)
        {
            if (t < TimeSpan.Zero)
            {
                t = TimeSpan.Zero;
            }

            return t.TotalHours >= 24 ? $"{(Int32)t.TotalDays}d {t.Hours}h"
                : t.TotalHours >= 1 ? $"{(Int32)t.TotalHours}h {t.Minutes}m"
                : $"{Math.Max(1, (Int32)t.TotalMinutes)}m";
        }

        // Soon: how long. Days away: which day, like the usage page says it.
        private static String ResetText(DateTime at)
        {
            var left = at - DateTime.Now;
            return left < TimeSpan.FromHours(20)
                ? $"resets {Span(left)}"
                : $"{at:ddd} {at:h:mm}{at.ToString("tt", System.Globalization.CultureInfo.InvariantCulture).ToLowerInvariant()}";
        }

        // The three keys of the usage row, left to right. The third is a per-model weekly bucket when
        // Claude Code reports one, and otherwise the pace of the session window.
        public static BitmapImage UsageKey(Int32 index, PluginImageSize size)
        {
            var u = UsageStore.Current;
            return index switch
            {
                0 => Usage(u.Session, u.Age, size),
                1 => Usage(u.Weekly, u.Age, size),
                _ => u.Models.Count > 0
                    ? Usage(new UsageWindow { Title = u.Models[0].Title.ToLowerInvariant(), Percent = u.Models[0].Percent, ResetsAt = u.Models[0].ResetsAt }, u.Age, size)
                    : Pace(u.Session, u.Age, size),
            };
        }

        // ---- the main page ------------------------------------------------------------------

        // Every session in one key: a headline for the most urgent thing going on, and a square per
        // session in its state colour. The square with a white edge is the one the keys act on.
        public static BitmapImage Overview(IReadOnlyList<SessionInfo> all, String targetKey, PluginImageSize size, Int32 frame)
        {
            using var b = new BitmapBuilder(size);
            var w = b.Width;
            var h = b.Height;
            b.Clear(Empty);

            var blocked = all.Count(s => s.State == "attention");
            var limited = all.Count(s => s.IsLimited);
            var errored = all.Count(s => s.State == "error" && !s.IsLimited);
            var finished = all.Count(s => s.State == "done" && !s.IsLimited);
            var working = all.Count(s => s.State == "busy");

            var (headline, color) =
                blocked > 0 ? (blocked == 1 ? "1 needs you" : $"{blocked} need you", Tint(Attention, 0.45))
                : errored > 0 ? ($"{errored} errored", Tint(Error, 0.5))
                : limited > 0 ? ($"{limited} at limit", Tint(Amber, 0.5))
                : finished > 0 ? ($"{finished} your turn", Tint(Done, 0.5))
                : working > 0 ? ($"{working} working", Tint(Busy, 0.4))
                : all.Count > 0 ? ("all idle", Tint(Empty, 0.55))
                : ("no sessions", Tint(Empty, 0.4));
            Band(b, headline, 0.04, 0.24, 14, color);

            var shown = all.Take(9).ToList();
            if (shown.Count == 0)
            {
                return b.ToImage();
            }

            // Up to 3x3, centred, sized to what there is: three sessions get three big squares.
            var cols = shown.Count <= 3 ? shown.Count : shown.Count == 4 ? 2 : 3;
            var rows = (shown.Count + cols - 1) / cols;
            var areaTop = (Int32)(h * 0.32);
            var areaH = h - areaTop - (Int32)(h * 0.08);
            var gap = Math.Max(4, (Int32)(w * 0.05));
            var cell = Math.Min((w - (Int32)(w * 0.16) - ((cols - 1) * gap)) / cols, (areaH - ((rows - 1) * gap)) / rows);
            var gridW = (cols * cell) + ((cols - 1) * gap);
            var gridH = (rows * cell) + ((rows - 1) * gap);
            var x0 = (w - gridW) / 2;
            var y0 = areaTop + ((areaH - gridH) / 2);

            for (var i = 0; i < shown.Count; i++)
            {
                var s = shown[i];
                var x = x0 + ((i % cols) * (cell + gap));
                var y = y0 + ((i / cols) * (cell + gap));
                var c = s.IsLimited ? Amber : ColourOf(s.State);
                if (s.State == "attention" && IsDimBeat(frame))
                {
                    c = Shade(c, 0.55);
                }

                if (s.Key == targetKey)
                {
                    b.FillRectangle(x - 2, y - 2, cell + 4, cell + 4, BitmapColor.White);
                }

                b.FillRectangle(x, y, cell, cell, c);
            }

            return b.ToImage();
        }

        // Nothing needs you: said calmly, with what is still going on.
        public static BitmapImage AllClear(Int32 working, Int32 total, PluginImageSize size)
        {
            using var b = new BitmapBuilder(size);
            var h = b.Height;
            b.Clear(Empty);
            Band(b, "NEXT", 0.05, 0.18, 11, Tint(Empty, 0.45));
            if (DeckConfig.Ascii)
            {
                Band(b, total == 0 ? Ascii.Asleep : Ascii.Happy, 0.24, 0.22, 14, Tint(Empty, 0.8));
                Band(b, total == 0 ? "no sessions" : "all clear", 0.48, 0.20, 12, Tint(Empty, 0.6));
            }
            else
            {
                Band(b, total == 0 ? "no sessions" : "all clear", 0.28, 0.34, 16, Tint(Empty, 0.75));
            }
            Band(b, working > 0 ? $"{working} working" : total > 0 ? "nothing running" : "start claude", 0.72, 0.18, 11, working > 0 ? Tint(Busy, 0.4) : Tint(Empty, 0.45));
            return b.ToImage();
        }

        public static BitmapImage EmptySlot(Int32 number, PluginImageSize size)
        {
            using var b = new BitmapBuilder(size);
            b.Clear(Empty);
            Band(b, DeckConfig.Ascii ? "[   ]" : "–", 0.26, 0.36, DeckConfig.Ascii ? 16 : 22, Tint(Empty, 0.25));
            Band(b, $"slot {number}", 0.72, 0.18, 11, Tint(Empty, 0.3));
            return b.ToImage();
        }

        // ---- a session's own page -----------------------------------------------------------

        // Everything about a session that is a fact rather than a setting: how full its context
        // window is, in tokens as well as the bar; its branch; how long and how many turns it has run.
        public static BitmapImage Info(SessionInfo s, PluginImageSize size)
        {
            using var b = new BitmapBuilder(size);
            var w = b.Width;
            var h = b.Height;
            b.Clear(Neutral);
            var soft = Tint(Neutral, 0.6);

            var fill = s.ContextFill;
            var barH = Math.Max(6, (Int32)(h * 0.07));
            b.FillRectangle(0, 0, w, barH, Shade(Neutral, 0.45));
            if (fill >= 0)
            {
                b.FillRectangle(0, 0, Math.Max(2, (Int32)(w * fill)), barH, fill >= 0.8 ? Warn : Tint(Neutral, 0.75));
            }

            var headline = fill >= 0 ? $"{(Int32)Math.Round(fill * 100)}% ctx" : "ctx ?";
            Band(b, headline, 0.10, 0.28, 17, fill >= 0.8 ? Warn : BitmapColor.White);
            b.DrawText(fill >= 0 ? $"{Tokens(s.ContextTokens)} of {Tokens(s.ContextWindow)}" : "no reply yet",
                2, (Int32)(h * 0.38), w - 4, (Int32)(h * 0.18), soft, 11);
            Band(b, Middle(Or(s.Branch, "no branch"), 16), 0.58, 0.18, 11, BitmapColor.White);

            var age = s.Started > 0 ? Elapsed(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - s.Started) : "";
            b.DrawText($"{s.Turns} turn{(s.Turns == 1 ? "" : "s")}{(age.Length > 0 ? " · " + age : "")}",
                2, (Int32)(h * 0.76), w - 4, (Int32)(h * 0.18), soft, 11);
            return b.ToImage();
        }

        private static String Tokens(Int64 n) =>
            n >= 1_000_000 ? $"{n / 1_000_000.0:0.#}M" : n >= 1000 ? $"{n / 1000}k" : n.ToString();

        // The way back up from a session's page - and, because it is the one key on that page that is
        // about everybody else, the place the other sessions get to tap you on the shoulder.
        //
        // Blocked on you beats finished beats merely there: red and blinking with the session's name,
        // green with a count, or quiet. Underneath, one dot per other session in its state colour, so
        // the whole deck is readable without leaving the page.
        public static BitmapImage Back(IReadOnlyList<SessionInfo> others, Boolean flash, PluginImageSize size, Int32 frame)
        {
            using var b = new BitmapBuilder(size);
            var w = b.Width;
            var h = b.Height;

            var waiting = others.Where(s => s.State == "attention").OrderBy(s => s.Since).ToList();
            var finished = others.Count(s => s.State is "done" or "error");

            BitmapColor bg;
            String note;
            if (waiting.Count > 0)
            {
                bg = IsDimBeat(frame) ? Shade(Attention, 0.5) : Attention;
                note = waiting.Count == 1 ? $"{Middle(Or(waiting[0].Project, "one"), 12)} needs you" : $"{waiting.Count} need you";
            }
            else if (finished > 0)
            {
                bg = Shade(Done, 0.35);
                note = $"{finished} your turn";
            }
            else
            {
                bg = Empty;
                note = others.Count == 0 ? "" : others.Count == 1 ? "1 other" : $"{others.Count} others";
            }

            b.Clear(bg);
            Band(b, "‹ sessions", 0.10, 0.28, 15, BitmapColor.White);
            Band(b, note, 0.38, 0.30, 11, waiting.Count > 0 ? BitmapColor.White : Tint(bg, 0.7));

            var shown = others.Take(8).ToList();
            if (shown.Count > 0)
            {
                var r = Math.Max(4, (Int32)(w * 0.04));
                var gap = r;
                var total = (shown.Count * 2 * r) + ((shown.Count - 1) * gap);
                var x = ((w - total) / 2) + r;
                var y = (Int32)(h * 0.85);

                // On a dark strip of their own: a red dot on a red key is no dot at all.
                var band = (Int32)(h * 0.30);
                b.FillRectangle(0, h - band, w, band, Empty);
                foreach (var o in shown)
                {
                    b.FillCircle(x, y, r, o.IsLimited ? Amber : ColourOf(o.State));
                    x += (2 * r) + gap;
                }
            }

            if (flash)
            {
                DrawFlash(b);
            }

            return b.ToImage();
        }

        // ---- models -------------------------------------------------------------------------

        // The Model key. At rest: the target session's model, filling the key in that model's colour.
        // While stepping: the model about to be chosen, ringed in white, until the taps stop.
        public static BitmapImage Model(String top, String name, String bottom, String color, Boolean ringed, Boolean dim, PluginImageSize size)
        {
            using var b = new BitmapBuilder(size);
            var h = b.Height;
            var bg = dim ? Empty : Named(color);
            b.Clear(bg);
            var soft = dim ? Tint(Empty, 0.5) : Tint(bg, 0.82);
            Band(b, Middle(top, 15), 0.05, 0.18, 11, soft);
            Band(b, name, 0.26, 0.42, name.Length > 9 ? 15 : 19, dim ? Tint(Empty, 0.6) : BitmapColor.White);
            Band(b, bottom, 0.74, 0.18, 11, soft);
            if (ringed)
            {
                DrawFlash(b);
            }

            return b.ToImage();
        }

        // One entry in the Models folder; the one in use is ringed and says so.
        public static BitmapImage ModelChoice(ModelDef model, Boolean current, Boolean flash, PluginImageSize size)
        {
            using var b = new BitmapBuilder(size);
            var h = b.Height;
            var bg = current ? Named(model.Color) : Shade(Named(model.Color), 0.55);
            b.Clear(bg);
            Band(b, model.Label, 0.26, 0.40, model.Label.Length > 9 ? 15 : 19, current ? BitmapColor.White : Tint(bg, 0.75));
            if (current)
            {
                Band(b, "in use", 0.72, 0.18, 11, Tint(bg, 0.85));
                var barH = Math.Max(4, (Int32)(h * 0.05));
                b.FillRectangle((Int32)(b.Width * 0.25), h - barH, (Int32)(b.Width * 0.5), barH, BitmapColor.White);
            }

            if (flash)
            {
                DrawFlash(b);
            }

            return b.ToImage();
        }

        // ---- home-page keys -----------------------------------------------------------------

        // A count in a state's colour with its name underneath; an empty colour means "nothing to
        // count", drawn dark with a dash so a quiet key does not pull the eye.
        public static BitmapImage Tally(Int32 count, String label, String colour, Int32 beat, PluginImageSize size)
        {
            if (String.IsNullOrEmpty(colour))
            {
                return BigNumberKey("–", label, Empty, size);
            }

            var bg = colour == "limit" ? Amber : ColourOf(colour);
            if (colour == "attention" && IsDimBeat(beat))
            {
                bg = Shade(bg, 0.5);
            }

            return BigNumberKey(count.ToString(), label, bg, size);
        }

        // The oldest open permission prompt, spelled out, so that pressing the key is an informed yes.
        public static BitmapImage Allow(SessionInfo s, Int32 waiting, PluginImageSize size, Int32 frame)
        {
            using var b = new BitmapBuilder(size);
            var w = b.Width;
            var h = b.Height;
            if (s == null)
            {
                b.Clear(Empty);
                DrawCentred(b, "✓", (Int32)(h * 0.42), (Int32)(h * 0.40), Tint(Empty, 0.35));
                DrawCentred(b, "Allow", (Int32)(h * 0.84), Math.Max(10, (Int32)(h * 0.125)), Tint(Empty, 0.5));
                return b.ToImage();
            }

            var bg = IsDimBeat(frame) ? Shade(Attention, 0.5) : Attention;
            b.Clear(bg);
            var soft = Tint(bg, 0.82);
            var head = waiting > 1 ? $"allow? (1/{waiting})" : "allow?";
            Band(b, head, 0.03, 0.17, 11, soft);
            var body = s.Detail.Length > 0 ? $"{ToolName(s.Tool)}: {Wrappable(s.Detail)}" : Or(ToolName(s.Tool), "tool");
            Band(b, End(body, 46), 0.20, 0.54, 13, BitmapColor.White);
            Band(b, Middle(Or(s.Project, "—"), 15), 0.78, 0.18, 11, soft);
            return b.ToImage();
        }

        // A number that fills the key, with a word under it saying what is being counted.
        private static BitmapImage BigNumberKey(String number, String caption, BitmapColor fill, PluginImageSize size)
        {
            using var b = new BitmapBuilder(size);
            b.Clear(fill);
            DrawCentred(b, number, (Int32)(b.Height * 0.43), (Int32)(b.Height * 0.45), BitmapColor.White);
            DrawCentred(b, caption, (Int32)(b.Height * 0.85), Math.Max(10, b.Height / 8), Blend(fill, BitmapColor.White, 0.8));
            return b.ToImage();
        }

        // Puts the middle of a line of text on a given row.
        //
        // DrawText does not centre the glyphs in the box it is given. Measured on rendered digits from
        // 14pt to 53pt in a key-high box: the BASELINE always lands about 5.5px below the middle of the
        // box, and a digit stands 0.74 x the font size above it. So the ink's middle is at
        //     box middle + 5.5 - (0.74 x size) / 2
        // and the box is slid up or down by however far that is from the row that was asked for.
        private const Double BaselineBelowMiddle = 5.5;
        private const Double DigitHeightPerPoint = 0.74;

        private static void DrawCentred(BitmapBuilder b, String text, Int32 row, Int32 fontSize, BitmapColor color)
        {
            var inkMiddle = (b.Height / 2.0) + BaselineBelowMiddle - (DigitHeightPerPoint * fontSize / 2.0);
            b.DrawText(text, 0, (Int32)Math.Round(row - inkMiddle), b.Width, b.Height, color, fontSize);
        }

        public static BitmapImage Dark(PluginImageSize size)
        {
            using var b = new BitmapBuilder(size);
            b.Clear(Empty);
            return b.ToImage();
        }

        public static BitmapImage Message(String line, String note, PluginImageSize size)
        {
            using var b = new BitmapBuilder(size);
            b.Clear(Neutral);
            Band(b, line, 0.18, 0.34, 14, BitmapColor.White);
            Band(b, note, 0.54, 0.36, 10, Tint(Neutral, 0.6));
            return b.ToImage();
        }

        // ---- app switcher -------------------------------------------------------------------

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<String, Byte[]> Icons = new();

        // An app: its icon, its name, a bar underneath when it is the one in front, dimmed when it is
        // hidden, and - for a terminal hosting Claude sessions - a badge in the deck's own colours.
        public static BitmapImage App(AppInfo app, Boolean front, String badgeState, Int32 badgeCount, Boolean flash, PluginImageSize size)
        {
            using var b = new BitmapBuilder(size);
            var w = b.Width;
            var h = b.Height;
            var bg = front ? Neutral : Empty;
            b.Clear(bg);

            var icon = IconBytes(app.Icon);
            var iconSize = (Int32)(w * 0.56);
            var iconX = (w - iconSize) / 2;
            var iconY = (Int32)(h * 0.07);
            if (icon != null)
            {
                b.DrawImage(icon, iconX, iconY, iconSize, iconSize, BitmapRotation.None);
            }
            else
            {
                b.FillRectangle(iconX, iconY, iconSize, iconSize, Tint(bg, 0.12));
                DrawCentred(b, Initial(app.Name), iconY + (iconSize / 2), (Int32)(iconSize * 0.6), BitmapColor.White);
            }

            if (app.Hidden && !front)
            {
                b.FillRectangle(0, 0, w, h, new BitmapColor(Empty.R, Empty.G, Empty.B, 150));
            }

            b.DrawText(Middle(app.Name, 15), 2, (Int32)(h * 0.66), w - 4, (Int32)(h * 0.22),
                app.Hidden && !front ? Tint(Empty, 0.45) : BitmapColor.White, 11);

            if (front)
            {
                var barH = Math.Max(4, (Int32)(h * 0.05));
                b.FillRectangle((Int32)(w * 0.25), h - barH, (Int32)(w * 0.5), barH, BitmapColor.White);
            }

            if (badgeCount > 0)
            {
                var r = (Int32)(w * 0.13);
                var cx = w - r - 4;
                var cy = r + 4;
                b.FillCircle(cx, cy, r + 2, bg);
                b.FillCircle(cx, cy, r, ColourOf(badgeState));
                var fs = (Int32)(r * 1.3);
                b.DrawText(badgeCount > 9 ? "9+" : badgeCount.ToString(), cx - r, cy - r + 1, r * 2, r * 2, BitmapColor.White, badgeCount > 9 ? fs - 3 : fs);
            }

            if (flash)
            {
                DrawFlash(b);
            }

            return b.ToImage();
        }

        // A label-only key for an app that is not running (or has never been seen, so has no icon).
        public static BitmapImage AppPlaceholder(String name, Int32 width, Int32 height)
        {
            using var b = new BitmapBuilder(width, height);
            b.Clear(Empty);
            b.DrawText(name, 2, (Int32)(height * 0.28), width - 4, (Int32)(height * 0.44), Tint(Empty, 0.6), 14);
            return b.ToImage();
        }

        // Drawn for a moment after a press, so a key that did its job says so.
        public static void DrawFlash(BitmapBuilder b)
        {
            var t = Math.Max(4, (Int32)(b.Width * 0.05));
            b.FillRectangle(0, 0, b.Width, t, BitmapColor.White);
            b.FillRectangle(0, b.Height - t, b.Width, t, BitmapColor.White);
            b.FillRectangle(0, 0, t, b.Height, BitmapColor.White);
            b.FillRectangle(b.Width - t, 0, t, b.Height, BitmapColor.White);
        }

        private static Byte[] IconBytes(String path)
        {
            if (String.IsNullOrEmpty(path))
            {
                return null;
            }

            if (Icons.TryGetValue(path, out var cached))
            {
                return cached;
            }

            try
            {
                var bytes = System.IO.File.ReadAllBytes(path);
                Icons[path] = bytes;
                return bytes;
            }
            catch
            {
                return null;
            }
        }

        private static String Initial(String name) =>
            String.IsNullOrWhiteSpace(name) ? "?" : name.Trim().Substring(0, 1).ToUpperInvariant();

        // ---- helpers ------------------------------------------------------------------------

        private static String Or(params String[] options)
        {
            foreach (var o in options)
            {
                if (!String.IsNullOrWhiteSpace(o))
                {
                    return o.Trim();
                }
            }

            return "";
        }

        // Character-frame animation. Every glyph here was rendered and looked at first, because the key
        // font is particular: half-circles, dots, diamonds, a tick, right-pointing arrows and the block
        // elements are in it; the middle dot, left-pointing triangles and solid stars are not.
        public static class Ascii
        {
            // A half-filled circle, turning. Like everything that shares a string with words, these come
            // from the key's main font: the renderer uses ONE typeface per string, and a glyph it has to
            // fetch from a symbol font (a star, braille) drags the whole string there - where there
            // are no letters, so the words come out as boxes.
            private static readonly String[] Turning = { "◐", "◓", "◑", "◒" };

            private static readonly String[] Twinkle = { "◇", "◆" };

            private static readonly String[] Blocks = { "▁", "▂", "▃", "▄", "▅", "▆", "▇", "█" };

            // The status line with its mark: a spinner while working, a twinkle and then a tick when
            // done, arrows closing in on whatever a blocked session is asking.
            public static String Status(SessionInfo s, String words, Int32 frame, Boolean justFinished)
            {
                if (s.IsLimited)
                {
                    return words;
                }

                switch (s.State)
                {
                    case "busy":
                        return $"{Turning[frame % Turning.Length]} {words}";

                    case "done":
                        return $"{(justFinished ? Twinkle[frame % Twinkle.Length] : "✓")} {words}";

                    case "error":
                        return $"x {words}";

                    case "attention":
                        // A long question has no room for the run-up; it keeps a plain mark instead.
                        if (words.Length > 12)
                        {
                            return $"! {words}";
                        }

                        return (frame % 3) switch
                        {
                            0 => $">  {words}  <",
                            1 => $"> {words} <",
                            _ => $">{words}<",
                        };

                    default:
                        return words;
                }
            }

            // Two sine waves of block characters sliding past each other along the bottom of a
            // working tile - an equaliser that says "busy" without a word.
            public static String Wave(Int32 frame, Int32 width)
            {
                var chars = new System.Text.StringBuilder(width);
                for (var i = 0; i < width; i++)
                {
                    var level = (Math.Sin((i * 0.75) - (frame * 0.9)) + Math.Sin((i * 0.31) + (frame * 0.45))) / 2.0;
                    chars.Append(Blocks[(Int32)Math.Round((level + 1) / 2.0 * (Blocks.Length - 1))]);
                }

                return chars.ToString();
            }

            // A bar that empties over the time a tap-to-step key waits before it commits. Spent cells
            // are drawn, not left blank: the key font is proportional and squeezes runs of spaces, so
            // a bar made with them would shrink instead of emptying.
            public static String Countdown(Double remaining, Int32 cells = 6)
            {
                var full = (Int32)Math.Ceiling(Math.Clamp(remaining, 0, 1) * cells);
                return "[" + new String('=', full) + new String('-', cells - full) + "]";
            }

            public const String Asleep = "(-_-) zzZ";
            public const String Happy = "\\(^_^)/";
            public const String Waiting = "( o_o)";
        }

        // Every tile is laid out as horizontal bands: a line of text occupies the stretch of the key
        // between two fractions of its height (0 = top edge, 1 = bottom), a few pixels in from the
        // sides. `nudge` moves a band down by whole pixels, for tiles with a gauge above their text.
        private static void Band(BitmapBuilder b, String text, Double from, Double height, Int32 fontSize, BitmapColor color, Int32 nudge = 0)
        {
            const Int32 Margin = 3;
            b.DrawText(text ?? "", Margin, nudge + (Int32)Math.Round(b.Height * from), b.Width - (2 * Margin),
                (Int32)Math.Round(b.Height * height), color, fontSize);
        }

        // All the colour arithmetic there is: a point part of the way from one colour to another.
        // Towards white lightens, towards black darkens.
        private static BitmapColor Blend(BitmapColor from, BitmapColor to, Double amount)
        {
            amount = Math.Clamp(amount, 0, 1);
            Byte Channel(Byte a, Byte z) => (Byte)Math.Round(a + ((z - a) * amount));
            return new BitmapColor(Channel(from.R, to.R), Channel(from.G, to.G), Channel(from.B, to.B));
        }

        private static BitmapColor Tint(BitmapColor c, Double amount) => Blend(c, BitmapColor.White, amount);

        private static BitmapColor Shade(BitmapColor c, Double amount) => Blend(c, BitmapColor.Black, amount);

        // DrawText wraps at spaces and nowhere else, so one long token - a path, a URL - runs off both
        // sides of the key and only its middle shows. Runs longer than a line are given somewhere to
        // break: after a separator if there is one in reach, else simply at the line length.
        private static String Wrappable(String text, Int32 line = 14)
        {
            if (String.IsNullOrEmpty(text))
            {
                return "";
            }

            var result = new System.Text.StringBuilder(text.Length + 8);
            var run = 0;
            foreach (var c in text)
            {
                if (c == ' ')
                {
                    run = 0;
                }
                else if (++run > line || (run > line / 2 && result.Length > 0 && result[^1] is '/' or '_' or '-' or '.' or '=' or ':'))
                {
                    result.Append(' ');
                    run = 1;
                }

                result.Append(c);
            }

            return result.ToString();
        }

        // Fits text to a length by dropping whole words from the end while that leaves something
        // worth reading, and only then cutting mid-word.
        private static String End(String value, Int32 max)
        {
            value = (value ?? "").Trim();
            if (value.Length <= max)
            {
                return value;
            }

            var room = max - 1;
            var kept = new System.Text.StringBuilder();
            foreach (var word in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (kept.Length + (kept.Length > 0 ? 1 : 0) + word.Length > room)
                {
                    break;
                }

                kept.Append(kept.Length > 0 ? " " : "").Append(word);
            }

            // Whole words only got us less than half way: the text is one long token, so cut it.
            var text = kept.Length >= room / 2 ? kept.ToString() : value.Substring(0, room);
            return text.TrimEnd('.', ',', ';', ':', '-', ' ') + "…";
        }

        // Fits a name to a length by taking the middle out, because names that share a prefix
        // (shop-web, shop-api) are told apart by how they end.
        private static String Middle(String value, Int32 max)
        {
            value = (value ?? "").Trim();
            if (value.Length <= max || max < 3)
            {
                return value;
            }

            var tail = (max - 1) / 2;
            var head = max - 1 - tail;
            return String.Concat(value.AsSpan(0, head), "…", value.AsSpan(value.Length - tail));
        }
    }
}
