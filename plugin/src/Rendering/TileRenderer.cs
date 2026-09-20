namespace Loupedeck.ClaudeDeckPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    // Everything drawn on a key. A tile is filled with its state colour so the deck reads from across
    // the room; the text is for when you are close enough to care which session it is.
    public static class TileRenderer
    {
        // Every state colour clears WCAG 4.5:1 against white text.
        private static readonly BitmapColor Busy = new(0xB8, 0x55, 0x35);
        private static readonly BitmapColor Done = new(0x2F, 0x7D, 0x4A);
        private static readonly BitmapColor Attention = new(0xB8, 0x2F, 0x2F);
        private static readonly BitmapColor Error = new(0x7A, 0x3B, 0x8F);
        private static readonly BitmapColor Idle = new(0x55, 0x58, 0x5C);
        private static readonly BitmapColor Neutral = new(0x3A, 0x3D, 0x42);
        private static readonly BitmapColor Amber = new(0x8F, 0x62, 0x10);
        private static readonly BitmapColor Empty = new(0x14, 0x16, 0x18);
        private static readonly BitmapColor Warn = new(0xF2, 0xC9, 0x4C);
        private static readonly BitmapColor Blue = new(0x2D, 0x6F, 0xB5);
        private static readonly BitmapColor Violet = new(0x6B, 0x4F, 0xBB);

        public const Int32 TickMs = 250;
        private const Int32 SweepFrames = 10;
        private const Int32 BlinkFrames = 2;

        public static BitmapColor StateColor(String state) => state switch
        {
            "busy" => Busy,
            "done" => Done,
            "attention" => Attention,
            "error" => Error,
            _ => Idle,
        };

        public static Boolean Animates(SessionInfo s) => s.State is "busy" or "attention";

        public static BitmapImage Session(SessionInfo s, Boolean selected, Boolean flash, PluginImageSize size, Int32 frame)
        {
            // Out of usage is its own colour: it is neither an error to fix nor a turn to take.
            var bg = s.IsLimited ? Amber : StateColor(s.State);
            if (s.State == "attention" && (frame / BlinkFrames) % 2 == 1)
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
            b.DrawText(Middle(Or(s.Project, "—"), 15), 2, top + (Int32)(h * 0.03), w - 4, (Int32)(h * 0.17), soft, 11);
            b.DrawText(What(s), 3, top + (Int32)(h * 0.20), w - 6, (Int32)(h * 0.50), fg, 13);
            b.DrawText(Status(s), 2, (Int32)(h * 0.74), w - 4, (Int32)(h * 0.18), soft, 11);

            if (s.State == "busy")
            {
                DrawSweep(b, bg, frame);
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

        private static void DrawSweep(BitmapBuilder b, BitmapColor bg, Int32 frame)
        {
            var w = b.Width;
            var barH = Math.Max(4, (Int32)(b.Height * 0.055));
            var y = b.Height - barH;
            b.FillRectangle(0, y, w, barH, Shade(bg, 0.35));

            var segW = Math.Max(12, (Int32)(w * 0.30));
            var x = (Int32)(((frame % SweepFrames) / (Double)SweepFrames) * (w + segW)) - segW;
            var x0 = Math.Max(0, x);
            var x1 = Math.Min(w, x + segW);
            if (x1 > x0)
            {
                b.FillRectangle(x0, y, x1 - x0, barH, Tint(bg, 0.75));
            }
        }

        // The main text. A session blocked on a permission prompt shows the thing it wants to do,
        // since that is what you need in order to answer it.
        private static String What(SessionInfo s)
        {
            if (s.NeedsPermission && s.Detail.Length > 0)
            {
                return End(s.Detail, 44);
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

        // Claude Code's slugs end in a random adjective-noun pair; drop it when there is enough left.
        private static String FromSlug(String slug)
        {
            if (String.IsNullOrWhiteSpace(slug))
            {
                return "";
            }

            var words = slug.Split('-', StringSplitOptions.RemoveEmptyEntries);
            var end = words.Length > 3 ? words.Length - 2 : words.Length;
            return String.Join(" ", words, 0, end);
        }

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
                b.DrawText(End(label, 40), 3, (Int32)(b.Height * 0.10), b.Width - 6, (Int32)(b.Height * 0.80), BitmapColor.White, 13);
            }
            else
            {
                b.DrawText(label, 2, (Int32)(b.Height * 0.28), b.Width - 4, (Int32)(b.Height * 0.44), BitmapColor.White, label.Length > 7 ? 14 : 17);
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
            b.DrawText(headline, 2, (Int32)(h * 0.10), w - 4, (Int32)(h * 0.28), fill >= 0.8 ? Warn : BitmapColor.White, 17);
            b.DrawText(fill >= 0 ? $"{Tokens(s.ContextTokens)} of {Tokens(s.ContextWindow)}" : "no reply yet",
                2, (Int32)(h * 0.38), w - 4, (Int32)(h * 0.18), soft, 11);
            b.DrawText(Middle(Or(s.Branch, "no branch"), 16), 2, (Int32)(h * 0.58), w - 4, (Int32)(h * 0.18), BitmapColor.White, 11);

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
                bg = (frame / BlinkFrames) % 2 == 1 ? Shade(Attention, 0.5) : Attention;
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
            b.DrawText("‹ sessions", 2, (Int32)(h * 0.10), w - 4, (Int32)(h * 0.28), BitmapColor.White, 15);
            b.DrawText(note, 2, (Int32)(h * 0.38), w - 4, (Int32)(h * 0.30), waiting.Count > 0 ? BitmapColor.White : Tint(bg, 0.7), 11);

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
                    b.FillCircle(x, y, r, o.IsLimited ? Amber : StateColor(o.State));
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
            b.DrawText(Middle(top, 15), 2, (Int32)(h * 0.05), b.Width - 4, (Int32)(h * 0.18), soft, 11);
            b.DrawText(name, 2, (Int32)(h * 0.26), b.Width - 4, (Int32)(h * 0.42), dim ? Tint(Empty, 0.6) : BitmapColor.White, name.Length > 9 ? 15 : 19);
            b.DrawText(bottom, 2, (Int32)(h * 0.74), b.Width - 4, (Int32)(h * 0.18), soft, 11);
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
            b.DrawText(model.Label, 2, (Int32)(h * 0.26), b.Width - 4, (Int32)(h * 0.40), current ? BitmapColor.White : Tint(bg, 0.75), model.Label.Length > 9 ? 15 : 19);
            if (current)
            {
                b.DrawText("in use", 2, (Int32)(h * 0.72), b.Width - 4, (Int32)(h * 0.18), Tint(bg, 0.85), 11);
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

        public static BitmapImage NeedsMe(Int32 attention, Int32 error, Int32 done, Int32 busy, Int32 total, PluginImageSize size, Int32 frame)
        {
            if (attention > 0)
            {
                var bg = (frame / BlinkFrames) % 2 == 1 ? Shade(Attention, 0.5) : Attention;
                return CountKey(attention.ToString(), "Needs me", bg, size);
            }

            if (error > 0)
            {
                return CountKey(error.ToString(), "Errored", Error, size);
            }

            if (done > 0)
            {
                return CountKey(done.ToString(), "Your turn", Done, size);
            }

            if (busy > 0)
            {
                return CountKey(busy.ToString(), "Working", Busy, size);
            }

            return total > 0
                ? CountKey(total.ToString(), "Idle", Idle, size)
                : CountKey("–", "Needs me", Empty, size);
        }

        public static BitmapImage Working(Int32 busy, PluginImageSize size) =>
            busy > 0
                ? CountKey(busy.ToString(), "Working", Busy, size)
                : CountKey("–", "Working", Empty, size);

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

            var bg = (frame / BlinkFrames) % 2 == 1 ? Shade(Attention, 0.5) : Attention;
            b.Clear(bg);
            var soft = Tint(bg, 0.82);
            var head = waiting > 1 ? $"allow? (1/{waiting})" : "allow?";
            b.DrawText(head, 2, (Int32)(h * 0.03), w - 4, (Int32)(h * 0.17), soft, 11);
            var body = s.Detail.Length > 0 ? $"{ToolName(s.Tool)}: {s.Detail}" : Or(ToolName(s.Tool), "tool");
            b.DrawText(End(body, 46), 3, (Int32)(h * 0.20), w - 6, (Int32)(h * 0.54), BitmapColor.White, 13);
            b.DrawText(Middle(Or(s.Project, "—"), 15), 2, (Int32)(h * 0.78), w - 4, (Int32)(h * 0.18), soft, 11);
            return b.ToImage();
        }

        private static BitmapImage CountKey(String count, String label, BitmapColor bg, PluginImageSize size)
        {
            using var b = new BitmapBuilder(size);
            b.Clear(bg);
            var h = b.Height;
            DrawCentred(b, count, (Int32)(h * 0.42), (Int32)(h * 0.46), BitmapColor.White);
            DrawCentred(b, label, (Int32)(h * 0.84), Math.Max(10, (Int32)(h * 0.125)), Tint(bg, 0.80));
            return b.ToImage();
        }

        // DrawText centres within its box by the font's line box rather than its glyphs, which
        // leaves large digits sitting visibly low; this corrects for it.
        private static void DrawCentred(BitmapBuilder b, String text, Int32 centreY, Int32 fontSize, BitmapColor color)
        {
            var y = centreY + (Int32)Math.Round((0.365 * fontSize) - 6) - (b.Height / 2);
            b.DrawText(text, 0, y, b.Width, b.Height, color, fontSize);
        }

        public static BitmapImage Blank(PluginImageSize size)
        {
            using var b = new BitmapBuilder(size);
            b.Clear(Empty);
            return b.ToImage();
        }

        public static BitmapImage Message(String line, String note, PluginImageSize size)
        {
            using var b = new BitmapBuilder(size);
            b.Clear(Neutral);
            b.DrawText(line, 2, (Int32)(b.Height * 0.18), b.Width - 4, (Int32)(b.Height * 0.34), BitmapColor.White, 14);
            b.DrawText(note, 2, (Int32)(b.Height * 0.54), b.Width - 4, (Int32)(b.Height * 0.36), Tint(Neutral, 0.6), 10);
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
                b.FillCircle(cx, cy, r, StateColor(badgeState));
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

        private static BitmapColor Tint(BitmapColor c, Double amount) => new(
            (Byte)(c.R + ((255 - c.R) * amount)),
            (Byte)(c.G + ((255 - c.G) * amount)),
            (Byte)(c.B + ((255 - c.B) * amount)));

        private static BitmapColor Shade(BitmapColor c, Double amount) => new(
            (Byte)(c.R * (1 - amount)),
            (Byte)(c.G * (1 - amount)),
            (Byte)(c.B * (1 - amount)));

        // Cuts at a word boundary where there is one.
        private static String End(String value, Int32 max)
        {
            if (String.IsNullOrEmpty(value) || value.Length <= max)
            {
                return value ?? "";
            }

            var cut = value.LastIndexOf(' ', max - 1);
            if (cut < max / 2)
            {
                cut = max - 1;
            }

            return value.Substring(0, cut).TrimEnd() + "…";
        }

        // Keeps both ends, since project names tend to differ at the end.
        private static String Middle(String value, Int32 max)
        {
            if (String.IsNullOrEmpty(value) || value.Length <= max)
            {
                return value ?? "";
            }

            var keep = max - 1;
            var head = (keep + 1) / 2;
            return value.Substring(0, head) + "…" + value.Substring(value.Length - (keep - head));
        }
    }
}
