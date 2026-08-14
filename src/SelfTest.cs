// ---------------------------------------------------------------------------
//  Tests that need no window, no clipboard and no keyboard: the character
//  tables, Caps Lock, direction detection, and the Smart Selection arithmetic.
//
//  Everything here is checked with ordinal, case-SENSITIVE comparison. A
//  case-insensitive compare would pass "Hello" for "hello" and quietly hide the
//  exact class of bug the shift-layer cases exist to catch.
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace KbFix
{
    internal static class SelfTest
    {
        private static int _failures;
        private static int _checks;

        private static string U(params int[] codePoints)
        {
            StringBuilder sb = new StringBuilder(codePoints.Length);
            foreach (int c in codePoints) sb.Append((char)c);
            return sb.ToString();
        }

        private static void Pass(string name, string detail)
        {
            _checks++;
            Console.WriteLine("  PASS  " + name.PadRight(44) + " " + detail);
        }

        private static void Fail(string name, string detail)
        {
            _checks++; _failures++;
            Console.WriteLine("  FAIL  " + name.PadRight(44) + " " + detail);
        }

        private static void Eq(string name, string actual, string expected)
        {
            if (string.Equals(actual, expected, StringComparison.Ordinal)) Pass(name, "'" + actual + "'");
            else Fail(name, "got '" + actual + "', expected '" + expected + "'");
        }

        private static void EqInt(string name, int actual, int expected)
        {
            if (actual == expected) Pass(name, actual.ToString(CultureInfo.InvariantCulture));
            else Fail(name, "got " + actual + ", expected " + expected);
        }

        private static void True(string name, bool condition, string detail)
        {
            if (condition) Pass(name, detail); else Fail(name, detail);
        }

        public static int Run()
        {
            _failures = 0; _checks = 0;

            Layout[] builtIn = Layouts.BuiltIn();
            Layout en = builtIn[0], th = builtIn[1];

            Console.WriteLine("== character mapping ==");
            MappingCases(en, th);

            Console.WriteLine("== Caps Lock ==");
            CapsCases(en, th);

            Console.WriteLine("== direction detection ==");
            DirectionCases(builtIn);

            Console.WriteLine("== every key round-trips ==");
            RoundTripCases(en, th);

            Console.WriteLine("== Smart Selection: tokenizer ==");
            TokenizerCases();

            Console.WriteLine("== Smart Selection: how much to convert ==");
            TailCases(builtIn, en, th);

            Console.WriteLine("== settings file ==");
            SettingsCases();

            Console.WriteLine("== layouts reported by Windows ==");
            LiveLayoutCases(en, th);

            Console.WriteLine();
            Console.WriteLine("  " + _checks + " checks, " + _failures + " failure(s).");
            return _failures == 0 ? 0 : 1;
        }

        // -------------------------------------------------------------------
        private static void MappingCases(Layout en, Layout th)
        {
            // sawatdi  = the Thai word typed on an EN-active keyboard as l;ylfu
            // khopkhun = the Thai word typed on an EN-active keyboard as -v[86I
            string sawatdi = U(0x0E2A, 0x0E27, 0x0E31, 0x0E2A, 0x0E14, 0x0E35);
            string khopkhun = U(0x0E02, 0x0E2D, 0x0E1A, 0x0E04, 0x0E38, 0x0E13);
            // "hello" typed on a TH-active keyboard; the capital H lands on the
            // shift layer (U+0E47) where plain h gives U+0E49.
            string lowerHello = U(0x0E49, 0x0E33, 0x0E2A, 0x0E2A, 0x0E19);
            string upperHello = U(0x0E47, 0x0E33, 0x0E2A, 0x0E2A, 0x0E19);

            Eq("EN-mode typing -> Thai", Converter.Convert("l;ylfu", en, th, 0), sawatdi);
            Eq("EN-mode typing -> Thai (2)", Converter.Convert("-v[86I", en, th, 0), khopkhun);
            Eq("TH-mode typing -> English", Converter.Convert(lowerHello, th, en, 0), "hello");
            Eq("capital letter (Shift+H)", Converter.Convert(upperHello, th, en, 0), "Hello");
            Eq("unmapped characters pass through", Converter.Convert("ab 12", en, th, 0),
               U(0x0E1F, 0x0E34) + " " + U(0x0E45) + "/");
            Eq("empty string", Converter.Convert("", en, th, 0), "");

            // A realistic mixed sentence, shift layer included, must survive a
            // round trip unchanged.
            string mixed = "Hello, World! (Test #1) A_B+C {x} \"q\" <y>|z";
            string there = Converter.Convert(mixed, en, th, 0);
            Eq("shift-layer sentence round trip", Converter.Convert(there, th, en, 0), mixed);

            // A long sentence with plenty of spaces, which is what the tool is
            // actually used on.
            string longEn = "the quick brown fox jumps over the lazy dog and then keeps running for a while";
            string longTh = Converter.Convert(longEn, en, th, 0);
            Eq("long sentence round trip", Converter.Convert(longTh, th, en, 0), longEn);
            True("long sentence really changed", longTh != longEn, longTh.Substring(0, 20) + "...");
            EqInt("long sentence keeps its length", longTh.Length, longEn.Length);

            int spacesBefore = 0, spacesAfter = 0;
            foreach (char c in longEn) if (c == ' ') spacesBefore++;
            foreach (char c in longTh) if (c == ' ') spacesAfter++;
            EqInt("spaces are untouched", spacesAfter, spacesBefore);

            // Tabs and newlines are not on any key, so they must pass straight
            // through and keep their positions. "abc\tdef\r\nghi" holds one tab,
            // one CR and one LF.
            string withBreaks = "abc\tdef\r\nghi";
            string converted = Converter.Convert(withBreaks, en, th, 0);
            EqInt("tab/newline count preserved",
                  CountOf(converted, '\t') + CountOf(converted, '\r') + CountOf(converted, '\n'), 3);
            EqInt("tab stays in place", converted.IndexOf('\t'), 3);
            EqInt("line break stays in place", converted.IndexOf('\r'), 7);
        }

        private static int CountOf(string s, char c)
        {
            int n = 0;
            foreach (char x in s) if (x == c) n++;
            return n;
        }

        // -------------------------------------------------------------------
        private static void CapsCases(Layout en, Layout th)
        {
            // With Caps Lock on, "HELLO" came from unshifted key presses, and the
            // Thai layout treats Caps Lock as a second Shift, so it maps to the
            // shifted Thai characters.
            Eq("CapsLock: letters", Converter.Convert("HELLO", en, th, 1),
               U(0x0E47, 0x0E0E, 0x0E28, 0x0E28, 0x0E2F));
            // The number row is where the layouts disagree: US ignores Caps Lock
            // there, Thai does not. This is what a caps-blind version gets wrong.
            Eq("CapsLock: number row", Converter.Convert("1", en, th, 1), "+");
            Eq("same key, CapsLock off", Converter.Convert("1", en, th, 0), U(0x0E45));
            Eq("CapsLock: back the other way", Converter.Convert("+", th, en, 1), "1");

            string mixed = "Hello, World! (Test #1) A_B+C {x} \"q\" <y>|z";
            for (int caps = 0; caps <= 1; caps++)
            {
                string there = Converter.Convert(mixed, en, th, caps);
                Eq("sentence round trip (caps=" + caps + ")", Converter.Convert(there, th, en, caps), mixed);
            }
        }

        // -------------------------------------------------------------------
        private static void DirectionCases(Layout[] layouts)
        {
            string sawatdi = U(0x0E2A, 0x0E27, 0x0E31, 0x0E2A, 0x0E14, 0x0E35);

            Layout src = Converter.SelectSource("l;ylfu", layouts, 0);
            True("source detected as English", src != null && src.LangId == 0x0409,
                 src == null ? "<none>" : src.Name);
            src = Converter.SelectSource(sawatdi, layouts, 0);
            True("source detected as Thai", src != null && src.LangId == 0x041E,
                 src == null ? "<none>" : src.Name);

            // Only characters both layouts can produce: converting would be a
            // guess, so it must be declined.
            True("ambiguous text declined", Converter.SelectSource("-/-/", layouts, 0) == null, "'-/-/'");
            True("whitespace declined", Converter.SelectSource("   ", layouts, 0) == null, "'   '");
            True("empty declined", Converter.SelectSource("", layouts, 0) == null, "''");

            // A long mostly-Latin sentence with one Thai word still reads as
            // English, because Latin letters outnumber the Thai ones.
            string mostlyEn = "the quick brown fox " + sawatdi;
            src = Converter.SelectSource(mostlyEn, layouts, 0);
            True("majority wins on mixed text", src != null && src.LangId == 0x0409,
                 src == null ? "<none>" : src.Name);
        }

        // -------------------------------------------------------------------
        private static void RoundTripCases(Layout en, Layout th)
        {
            int shifted = 0, broken = 0, keys = 0;
            for (int caps = 0; caps <= 1; caps++)
            {
                foreach (KeyValuePair<string, char> kv in en.Map(caps).Forward)
                {
                    keys++;
                    string enChar = kv.Value.ToString();
                    string thChar = th.Map(caps).Forward[kv.Key].ToString();
                    if (!string.Equals(Converter.Convert(thChar, th, en, caps), enChar, StringComparison.Ordinal))
                    {
                        broken++;
                        Console.WriteLine("        broken: EN '" + enChar + "' (caps=" + caps + ")");
                    }
                    if (caps == 0 && (char.IsUpper(kv.Value) ||
                        "~!@#$%^&*()_+{}|:\"<>?".IndexOf(kv.Value) >= 0)) shifted++;
                }
            }
            if (broken == 0) Pass("every key round-trips", keys + " key/caps combinations");
            else Fail("every key round-trips", broken + " broken");
            EqInt("shift-layer keys in the table", shifted, 47);
        }

        // -------------------------------------------------------------------
        private static void TokenizerCases()
        {
            EqInt("tokenize: empty", SmartSelection.Tokenize("").Count, 0);
            EqInt("tokenize: only spaces", SmartSelection.Tokenize("     ").Count, 0);
            EqInt("tokenize: one word", SmartSelection.Tokenize("hello").Count, 1);
            EqInt("tokenize: leading/trailing spaces", SmartSelection.Tokenize("   hello   ").Count, 1);
            EqInt("tokenize: runs of spaces", SmartSelection.Tokenize("a     b\t\tc").Count, 3);

            List<SmartSelection.Token> t = SmartSelection.Tokenize("  ab  cde ");
            EqInt("tokenize: first token start", t[0].Start, 2);
            EqInt("tokenize: first token length", t[0].Length, 2);
            EqInt("tokenize: second token start", t[1].Start, 6);
            EqInt("tokenize: second token length", t[1].Length, 3);

            // Many words with many spaces: the counts must stay exact.
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 50; i++) sb.Append("word").Append(i).Append("   ");
            EqInt("tokenize: 50 words, triple spaces", SmartSelection.Tokenize(sb.ToString()).Count, 50);
        }

        // -------------------------------------------------------------------
        private static void TailCases(Layout[] layouts, Layout en, Layout th)
        {
            string sawatdi = U(0x0E2A, 0x0E27, 0x0E31, 0x0E2A, 0x0E14, 0x0E35);   // 6 Thai chars
            string khopkhun = U(0x0E02, 0x0E2D, 0x0E1A, 0x0E04, 0x0E38, 0x0E13);

            // --- the simple case: one mistyped word, nothing else on the line
            TailResult r = SmartSelection.ComputeTail("l;ylfu", layouts, 0);
            EqInt("tail: single word", r.CharCount, 6);
            True("tail: single word source", r.Source != null && r.Source.LangId == 0x0409, r.Reason);

            // --- trailing space is included; it converts to itself
            r = SmartSelection.ComputeTail("l;ylfu ", layouts, 0);
            EqInt("tail: trailing space included", r.CharCount, 7);

            // --- THE point of the one-word default: correct English in front of
            //     the mistyped word must survive, even though it is
            //     indistinguishable from wrong-layout typing.
            r = SmartSelection.ComputeTail("Please read l;ylfu", layouts, 0);
            EqInt("tail: leaves correct English alone", r.CharCount, 6);

            r = SmartSelection.ComputeTail(sawatdi + " l;ylfu", layouts, 0);
            EqInt("tail: stops at the other layout", r.CharCount, 6);

            // --- one word only, however many mistyped words are on the line
            r = SmartSelection.ComputeTail("l;ylfu l;ylfu l;ylfu", layouts, 0);
            EqInt("tail: default takes one word", r.CharCount, 6);

            // --- raising the word limit takes the run, and still stops at the
            //     other layout rather than running off the front of the line
            r = SmartSelection.ComputeTail("l;ylfu l;ylfu l;ylfu", layouts, 0, 300, 5);
            EqInt("tail: 3 words with the limit raised", r.CharCount, 20);
            r = SmartSelection.ComputeTail(sawatdi + " hello world", layouts, 0, 300, 5);
            EqInt("tail: raised limit stops at the other layout", r.CharCount, 11);
            r = SmartSelection.ComputeTail("l;ylfu l;ylfu", layouts, 0, 300, 2);
            EqInt("tail: word limit is exact", r.CharCount, 13);

            // --- digits are NOT neutral between these two layouts: the Thai
            //     layout has no ASCII digits at all, so "123" reads as English
            r = SmartSelection.ComputeTail("123", layouts, 0);
            True("tail: digits count as English here", r.CharCount == 3 &&
                 r.Source != null && r.Source.LangId == 0x0409, r.Reason);

            // --- genuinely neutral: characters both layouts can produce
            r = SmartSelection.ComputeTail("l;ylfu -/-", layouts, 0);
            EqInt("tail: neutral word at the caret declined", r.CharCount, 0);
            True("tail: neutral reason given", r.Reason.Length > 0, r.Reason);

            // --- a neutral word inside the run gets absorbed once the limit
            //     allows a second word
            r = SmartSelection.ComputeTail("l;ylfu -/- l;ylfu", layouts, 0, 300, 5);
            EqInt("tail: neutral word absorbed mid-run", r.CharCount, 17);

            // --- nothing before the caret
            EqInt("tail: empty line", SmartSelection.ComputeTail("", layouts, 0).CharCount, 0);
            EqInt("tail: whitespace only", SmartSelection.ComputeTail("     ", layouts, 0).CharCount, 0);

            // --- Shift+Home cannot cross a line, so a line break means the grab
            //     went wrong; character counting would be off by one per CRLF
            EqInt("tail: newline declined", SmartSelection.ComputeTail("a\r\nl;ylfu", layouts, 0).CharCount, 0);
            EqInt("tail: bare LF declined", SmartSelection.ComputeTail("a\nl;ylfu", layouts, 0).CharCount, 0);

            // --- tabs count as whitespace, so they split tokens like spaces
            r = SmartSelection.ComputeTail("hello\tl;ylfu", layouts, 0);
            EqInt("tail: tab separates tokens", r.CharCount, 6);
            r = SmartSelection.ComputeTail("hello\t\t\t   l;ylfu", layouts, 0);
            EqInt("tail: mixed whitespace run", r.CharCount, 6);

            // --- long line, many spaces: only the mistyped tail is taken
            StringBuilder line = new StringBuilder();
            for (int i = 0; i < 40; i++) line.Append("word ");          // 200 chars of correct English
            string prefix = line.ToString();
            r = SmartSelection.ComputeTail(prefix + sawatdi, layouts, 0);
            EqInt("tail: long English line + Thai tail", r.CharCount, sawatdi.Length);
            True("tail: long line source is Thai", r.Source != null && r.Source.LangId == 0x041E, r.Reason);

            // --- long run of mistyped words with the limit raised high enough
            StringBuilder run = new StringBuilder();
            for (int i = 0; i < 20; i++) run.Append("l;ylfu ");         // 140 chars
            r = SmartSelection.ComputeTail(sawatdi + " " + run.ToString(), layouts, 0, 300, 25);
            EqInt("tail: 20 mistyped words taken whole", r.CharCount, run.Length);

            // --- over the character cap: whole words are given up from the left
            StringBuilder huge = new StringBuilder();
            for (int i = 0; i < 100; i++) huge.Append("l;ylfu ");       // 700 chars
            r = SmartSelection.ComputeTail(huge.ToString(), layouts, 0, 50, 100);
            True("tail: char cap respected", r.CharCount > 0 && r.CharCount <= 50,
                 r.CharCount + " chars (cap 50)");
            True("tail: char cap lands on a word boundary",
                 r.CharCount % 7 == 0, r.CharCount + " is a multiple of 7");

            // --- one word longer than the cap is refused rather than cut in half
            r = SmartSelection.ComputeTail(new string('l', 80), layouts, 0, 50);
            EqInt("tail: oversized single word declined", r.CharCount, 0);

            // --- a very long single Thai token is fine as long as it fits
            r = SmartSelection.ComputeTail(new string((char)0x0E2A, 120), layouts, 0, 300, 1);
            EqInt("tail: 120-character Thai token", r.CharCount, 120);

            // --- the Thai direction, which is the common one: Thai has no spaces
            //     so a whole phrase is a single token
            string thaiRun = sawatdi + khopkhun;
            r = SmartSelection.ComputeTail("hello " + thaiRun, layouts, 0);
            EqInt("tail: Thai phrase is one token", r.CharCount, thaiRun.Length);

            // --- Caps Lock changes which characters are distinctive, so the
            //     arithmetic must be done in the right state
            r = SmartSelection.ComputeTail("HELLO", layouts, 1);
            EqInt("tail: caps on, letters", r.CharCount, 5);

            // --- what the caller does with the result must line up exactly
            string linePrefix = "Please read l;ylfu";
            r = SmartSelection.ComputeTail(linePrefix, layouts, 0);
            string wanted = linePrefix.Substring(linePrefix.Length - r.CharCount);
            Eq("tail: substring matches the tail", wanted, "l;ylfu");
            Eq("tail: converting the tail gives Thai", Converter.Convert(wanted, en, th, 0), sawatdi);
        }

        // -------------------------------------------------------------------
        private static void SettingsCases()
        {
            Dictionary<string, string> flat = Settings.ParseFlatJson(
                "{ \"Hotkey\": \"Ctrl+Alt+K\", \"SmartSelection\": false, \"MaxSmartChars\": 120 }");
            Eq("settings: string value", flat["hotkey"], "Ctrl+Alt+K");
            Eq("settings: bool value", flat["smartselection"], "false");
            Eq("settings: int value", flat["maxsmartchars"], "120");

            // Written by an older or newer version: unknown keys and nested
            // objects must not stop the known ones being read.
            flat = Settings.ParseFlatJson(
                "{\n  \"Hotkey\" : \"Win+Space\" ,\n  \"Nested\": { \"a\": 1 },\n  \"Later\": [1,2],\n  \"SwitchLanguage\": true\n}");
            Eq("settings: survives nesting", flat["hotkey"], "Win+Space");
            Eq("settings: reads past nesting", flat["switchlanguage"], "true");

            flat = Settings.ParseFlatJson("{ \"Hotkey\": \"a\\\"b\\\\c\" }");
            Eq("settings: escapes decoded", flat["hotkey"], "a\"b\\c");

            flat = Settings.ParseFlatJson("not json at all");
            EqInt("settings: junk yields nothing", flat.Count, 0);
        }

        // -------------------------------------------------------------------
        //  What Windows itself reports, checked against the built-in table.
        //  This is what proves the generic layout probing is trustworthy: on a
        //  machine with Thai installed, what Windows says each key produces must
        //  match the hand-written Kedmanee table exactly, in both Caps states.
        private static void LiveLayoutCases(Layout builtInEn, Layout builtInTh)
        {
            Layout[] live;
            try { live = Layouts.Installed(); }
            catch (Exception ex) { Fail("probe installed layouts", ex.Message); return; }

            Console.WriteLine("  layouts installed on this machine:");
            foreach (Layout l in live)
                Console.WriteLine("    0x" + l.LangId.ToString("X4", CultureInfo.InvariantCulture) +
                                  "  " + l.Name.PadRight(34) + " " + l.KeyCount + " keys");

            Layout liveEn = null, liveTh = null;
            foreach (Layout l in live)
            {
                if (l.LangId == 0x0409) liveEn = l;
                if (l.LangId == 0x041E) liveTh = l;
            }
            if (liveEn == null || liveTh == null)
            {
                Console.WriteLine("  (skipped: needs both 0x0409 and 0x041E installed)");
                return;
            }

            for (int caps = 0; caps <= 1; caps++)
            {
                int checked_ = 0, bad = 0;
                foreach (KeyValuePair<string, char> kv in liveEn.Map(caps).Forward)
                {
                    char probedTh;
                    if (!liveTh.Map(caps).Forward.TryGetValue(kv.Key, out probedTh)) continue;
                    string oraclePos;
                    if (!builtInEn.Map(caps).Reverse.TryGetValue(kv.Value, out oraclePos)) continue;
                    char expected = builtInTh.Map(caps).Forward[oraclePos];
                    checked_++;
                    if (probedTh != expected)
                    {
                        bad++;
                        Console.WriteLine("        EN '" + kv.Value + "' -> U+" +
                                          ((int)probedTh).ToString("X4", CultureInfo.InvariantCulture) +
                                          ", table says U+" +
                                          ((int)expected).ToString("X4", CultureInfo.InvariantCulture));
                    }
                }
                if (bad == 0) Pass("probed Thai layout (caps=" + caps + ")", checked_ + " keys agree with the table");
                else Fail("probed Thai layout (caps=" + caps + ")", bad + " keys disagree");
            }
        }
    }
}
