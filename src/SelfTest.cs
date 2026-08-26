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
using System.IO;
using System.Text;
using System.Windows.Forms;

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

            Console.WriteLine("== swapping letter case ==");
            CaseFixCases();

            Console.WriteLine("== undoing Caps Lock on a non-Latin layout ==");
            CapsLayerCases(builtIn, en, th);

            Console.WriteLine("== direction detection ==");
            DirectionCases(builtIn);

            Console.WriteLine("== every key round-trips ==");
            RoundTripCases(en, th);

            Console.WriteLine("== Smart Selection: tokenizer ==");
            TokenizerCases();

            Console.WriteLine("== Smart Selection: how much to convert ==");
            TailCases(builtIn, en, th);

            Console.WriteLine("== Smart Selection: stopping at real words ==");
            WordJudgeCases(builtIn);

            Console.WriteLine("== arrow-key press counts ==");
            TextUnitCases();

            Console.WriteLine("== undo ==");
            UndoCases();

            Console.WriteLine("== hotkey names ==");
            HotkeyCases();

            Console.WriteLine("== settings file ==");
            SettingsCases();

            Console.WriteLine("== keystroke marshalling ==");
            SendInputCases();

            Console.WriteLine("== copies that were never a selection ==");
            WholeLineCopyCases();

            Console.WriteLine("== layouts reported by Windows ==");
            LiveLayoutCases(en, th);

            Console.WriteLine();
            Console.WriteLine("  " + _checks + " checks, " + _failures + " failure(s).");
            return _failures == 0 ? 0 : 1;
        }

        // -------------------------------------------------------------------
        /// SendInput rejects the whole array when cbSize is not exactly the size
        /// of an INPUT, and it does so by returning zero -- no exception, no
        /// crash, simply no keystrokes. Every copy, paste and arrow key this
        /// program sends goes through that call, so a union laid out one field
        /// too small would leave a program that starts, logs, and does nothing
        /// whatsoever. Checking the size is how that stays impossible.
        private static void SendInputCases()
        {
            // 4 type + 4 padding + 32 union on 64-bit; 4 + 24 on 32-bit. The
            // union is the size of MOUSEINPUT, its largest member.
            int expected = IntPtr.Size == 8 ? 40 : 28;
            EqInt("INPUT is the size Windows expects",
                  System.Runtime.InteropServices.Marshal.SizeOf(typeof(INPUT)), expected);

            INPUT k = Native.KeyInput(Native.VK_RIGHT, true);
            EqInt("a key event is a keyboard event", (int)k.type, 1);
            EqInt("the virtual key survives the union", (int)k.u.ki.wVk, Native.VK_RIGHT);
            True("arrow keys carry the extended-key flag",
                 (k.u.ki.dwFlags & Native.KEYEVENTF_EXTENDEDKEY) != 0,
                 "flags 0x" + k.u.ki.dwFlags.ToString("X"));
            True("our own keystrokes are stamped, so the watcher ignores them",
                 k.u.ki.dwExtraInfo.ToUInt64() == Native.SIGNATURE,
                 "0x" + k.u.ki.dwExtraInfo.ToUInt64().ToString("X"));

            INPUT up = Native.KeyInput(Native.VK_SHIFT, false);
            True("a release carries KEYEVENTF_KEYUP",
                 (up.u.ki.dwFlags & Native.KEYEVENTF_KEYUP) != 0,
                 "flags 0x" + up.u.ki.dwFlags.ToString("X"));
            True("plain keys are not marked extended",
                 (up.u.ki.dwFlags & Native.KEYEVENTF_EXTENDEDKEY) == 0,
                 "flags 0x" + up.u.ki.dwFlags.ToString("X"));

            // An empty batch must be a no-op rather than a failed call: the
            // release paths reach it whenever there is nothing to give back.
            True("sending nothing succeeds", Native.Send(new INPUT[0]), "");
        }

        // -------------------------------------------------------------------
        /// The guard against editors that copy the whole line when nothing is
        /// selected. Pure enough to check here rather than by driving VS Code:
        /// what has to be right is which shapes of text are treated as "the user
        /// never selected this".
        private static void WholeLineCopyCases()
        {
            True("a plain word is a real selection", !Fixer.LooksLikeWholeLineCopy("l;ylfu"), "");
            True("so is a phrase with spaces", !Fixer.LooksLikeWholeLineCopy("Please read l;ylfu"), "");
            True("and so is text with a break in the MIDDLE",
                 !Fixer.LooksLikeWholeLineCopy("first\r\nsecond"), "");

            // What VS Code, Visual Studio, Notepad++ and the JetBrains IDEs all
            // put on the clipboard for Ctrl+C with an empty selection.
            True("a line with its CRLF is not a selection",
                 Fixer.LooksLikeWholeLineCopy("var x = 1;\r\n"), "");
            True("a line with a bare LF is not either",
                 Fixer.LooksLikeWholeLineCopy("var x = 1;\n"), "");
            True("nor one ending in a lone CR",
                 Fixer.LooksLikeWholeLineCopy("var x = 1;\r"), "");

            // Nothing copied at all is a different answer -- "no selection" --
            // handled by the caller, so this must not claim it.
            True("nothing copied is not a whole-line copy", !Fixer.LooksLikeWholeLineCopy(""), "");
            True("null is not a whole-line copy either", !Fixer.LooksLikeWholeLineCopy(null), "");
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
        /// Caps Lock does something quite different on a layout that has no
        /// upper and lower case. On Thai Kedmanee it is a second Shift on EVERY
        /// key, so text typed with it stuck on comes back as the shifted layer
        /// and ToUpper/ToLower cannot touch one character of it -- reported as
        /// "the Caps Lock fix works on English but does nothing to Thai".
        private static void CapsLayerCases(Layout[] builtIn, Layout en, Layout th)
        {
            // a s d on the Thai layout: unshifted gives ฟ ห ก, and with Caps
            // Lock stuck on the same keys give the shifted half, ฤ ฆ ฏ.
            string capsOn = U(0x0E24, 0x0E06, 0x0E0F);      // ฤ ฆ ฏ
            string meant = U(0x0E1F, 0x0E2B, 0x0E01);       // ฟ ห ก

            Eq("caps layer: Thai shifted -> what was meant", Converter.FlipCapsOn(capsOn, th), meant);
            Eq("caps layer: and back again", Converter.FlipCapsOn(meant, th), capsOn);

            // The exact text out of the bug report, six keys long.
            string reported = U(0x0E24, 0x0E0F, 0x0E06, 0x0E24, 0x0E06, 0x0E0F);   // ฤฏฆฤฆฏ
            Eq("caps layer: the reported sample",
               Converter.FlipCapsOn(reported, th),
               U(0x0E1F, 0x0E01, 0x0E2B, 0x0E1F, 0x0E2B, 0x0E01));                 // ฟกหฟหก

            // Applying it twice is the identity, which is the property the whole
            // feature rests on: pressing Caps Lock again undoes the fix, so
            // nothing has to be remembered for undo.
            string[] samples = new string[] {
                capsOn, meant, reported,
                U(0x0E24) + "VSCODE " + U(0x0E0F),
                U(0x0E2A, 0x0E27, 0x0E31, 0x0E2A, 0x0E14, 0x0E35),
                "hELLO", "", "  ", "123"
            };
            int bad = 0;
            foreach (string s in samples)
                if (Converter.FlipCapsOn(Converter.FlipCapsOn(s, th), th) != s)
                { bad++; Console.WriteLine("        '" + s + "'"); }
            if (bad == 0) Pass("caps layer: flipping twice is the identity", samples.Length + " samples");
            else Fail("caps layer: flipping twice is the identity", bad + " sample(s) did not round-trip");

            // A selection that ran across a language switch has both halves
            // wrong, and both have to come back: the Thai by its Shift layer,
            // the Latin by its case. This is the shape the report arrived in.
            Eq("caps layer: Thai and Latin in one selection",
               Converter.FlipCapsOn(capsOn + " VSCODE", th), meant + " vscode");

            // ...and it must not depend on which language happens to have more
            // characters in the selection. "VSCODE" is six distinctive Latin
            // letters against three Thai ones, so judging the selection as a
            // whole calls it English and leaves the Thai untouched -- which is
            // the reported bug over again, one layer down.
            Eq("caps layer: Latin-dominant mix still fixes the Thai",
               Converter.FlipCaps(capsOn + " VSCODE", builtIn), meant + " vscode");
            Eq("caps layer: Thai-dominant mix still fixes the Latin",
               Converter.FlipCaps(capsOn + capsOn + " VS", builtIn), meant + meant + " vs");
            True("caps layer: the mixed flip is its own inverse",
                 Converter.FlipCaps(Converter.FlipCaps(capsOn + " VSCODE", builtIn), builtIn)
                     == capsOn + " VSCODE", "there and back");

            // A full stop is on both layouts, so nothing about it says which key
            // was pressed. Guessing turns the end of an English sentence into
            // 'ง', which is why a character more than one layout claims is left
            // to the plain case swap.
            Eq("caps layer: shared punctuation is not guessed at",
               Converter.FlipCaps("hI. bYE.", builtIn), "Hi. Bye.");
            Eq("caps layer: digits survive a Latin-dominant mix",
               Converter.FlipCaps("aBc 123", builtIn), "AbC 123");

            // Nothing about this is Thai-specific: run on a Latin layout it has
            // to agree with the plain case swap, character for character, or the
            // English behaviour would have changed under everyone's feet.
            string[] latin = new string[] { "tHAILAND", "hELLO wORLD", "Ab-12_%$", "iPhone XS" };
            int drift = 0;
            foreach (string s in latin)
                if (Converter.FlipCapsOn(s, en) != CaseFix.Flip(s))
                { drift++; Console.WriteLine("        '" + s + "' -> '" + Converter.FlipCapsOn(s, en) + "'"); }
            True("caps layer: a Latin layout still just swaps case", drift == 0,
                 latin.Length + " samples agree with CaseFix.Flip");

            // With no idea which layout the text came from there is still the
            // case swap, which is what every build before this one did.
            Eq("caps layer: no layout falls back to the case swap",
               Converter.FlipCapsOn("hELLO", null), "Hello");
            Eq("caps layer: empty", Converter.FlipCapsOn("", th), "");
            True("caps layer: null survives", Converter.FlipCapsOn(null, th) == null, "no exception");

            // Whitespace must come back byte for byte or the paste reflows the
            // text. Neither layout maps it, so it goes through the case swap.
            Eq("caps layer: spaces, tabs and newlines kept",
               Converter.FlipCapsOn("a \t b\r\nc", th), "A \t B\r\nC");

            // And the selection has to be recognised as Thai in the first place,
            // or FlipCaps is never handed the right layout. Caps 0 for the
            // lookup, because a layout's caps-off table holds every character it
            // can produce, both Shift halves included.
            Layout picked = Converter.SelectSource(reported, builtIn, 0);
            True("caps layer: shifted Thai is recognised as Thai",
                 picked != null && picked.LangId == 0x041E,
                 picked != null ? picked.Name : "(nothing picked)");
        }

        private static void CaseFixCases()
        {
            Eq("case: all caps -> lower", CaseFix.Flip("HELLO"), "hello");
            Eq("case: lower -> all caps", CaseFix.Flip("hello"), "HELLO");

            // The mistake this exists for: Caps Lock stuck on turns a SHIFTED
            // letter lower case, so the capital and the rest swap round.
            Eq("case: Caps Lock stuck on", CaseFix.Flip("tHAILAND"), "Thailand");
            // Only the words that were shifted come back capitalised; a word
            // typed without Shift was all caps and becomes all lower case.
            Eq("case: mid-sentence", CaseFix.Flip("hELLO wORLD, hOW ARE YOU?"), "Hello World, How are you?");

            Eq("case: digits and punctuation untouched", CaseFix.Flip("aB-12_%$"), "Ab-12_%$");
            Eq("case: Thai untouched", CaseFix.Flip(U(0x0E2A, 0x0E27, 0x0E31, 0x0E2A, 0x0E14, 0x0E35)),
                                       U(0x0E2A, 0x0E27, 0x0E31, 0x0E2A, 0x0E14, 0x0E35));
            Eq("case: mixed Thai and Latin", CaseFix.Flip(U(0x0E44, 0x0E17, 0x0E22) + "aBc"),
                                             U(0x0E44, 0x0E17, 0x0E22) + "AbC");
            Eq("case: empty", CaseFix.Flip(""), "");
            True("case: null survives", CaseFix.Flip(null) == null, "no exception");

            // Whitespace is what a long selection is mostly made of, and it must
            // come back byte for byte or the paste would reflow the text.
            Eq("case: spaces, tabs and newlines kept",
               CaseFix.Flip("a \t b\r\nc"), "A \t B\r\nC");

            // Applying it twice is the identity. That is the property the whole
            // feature rests on: it is why pressing Caps Lock again on the same
            // selection undoes the fix, and why nothing has to be remembered.
            string[] samples = new string[] {
                "hELLO", "Thailand", "iPhone XS", "ABC abc 123", "a", "Z",
                U(0x0E2A, 0x0E27) + "Hi" + U(0x0E31), "  ", "éclair", "Ärger", "ÜBER"
            };
            int bad = 0;
            foreach (string s in samples)
                if (CaseFix.Flip(CaseFix.Flip(s)) != s) { bad++; Console.WriteLine("        '" + s + "'"); }
            if (bad == 0) Pass("case: flipping twice is the identity", samples.Length + " samples");
            else Fail("case: flipping twice is the identity", bad + " sample(s) did not round-trip");

            // Length must not change either: the German sharp s upper-cases to
            // "SS" as a string but stays one character as a char, and keeping it
            // one character is what makes the round trip above hold.
            EqInt("case: length is stable", CaseFix.Flip("straße").Length, 6);

            True("case: sees a cased letter", CaseFix.HasCasedLetter("12a"), "'12a'");
            True("case: digits alone have none", !CaseFix.HasCasedLetter("12 34"), "'12 34'");
            True("case: Thai alone has none",
                 !CaseFix.HasCasedLetter(U(0x0E2A, 0x0E27, 0x0E31, 0x0E2A, 0x0E14, 0x0E35)), "Thai");
            True("case: nothing to do is detectable", CaseFix.Flip("12 34") == "12 34", "unchanged");
        }

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
        /// A judge with a fixed vocabulary, so the walk is tested rather than
        /// whichever dictionary happens to be installed.
        private sealed class FakeJudge : IWordJudge
        {
            private readonly List<string> _realWords;
            public int Asked;
            public FakeJudge(params string[] realWords) { _realWords = new List<string>(realWords); }
            public bool LooksIntentional(string word)
            {
                Asked++;
                return _realWords.Contains(word);
            }
        }

        private static void WordJudgeCases(Layout[] layouts)
        {
            string sawatdi = U(0x0E2A, 0x0E27, 0x0E31, 0x0E2A, 0x0E14, 0x0E35);

            // The case the one-word default exists for. With a judge, the walk
            // can take several words and still stop before real English.
            FakeJudge judge = new FakeJudge("Please", "read", "the", "quick", "send");
            TailResult r = SmartSelection.ComputeTail("Please read l;ylfu", layouts, 0, 300, 5, judge);
            EqInt("judge: stops before a real word", r.CharCount, 6);
            True("judge: reason names the word", r.Reason.Contains("read"), r.Reason);

            r = SmartSelection.ComputeTail("Please read l;ylfu c9j g4njv", layouts, 0, 300, 5, judge);
            EqInt("judge: takes the whole mistyped run", r.CharCount, 16);

            // Without the judge the same input eats the good words too, which is
            // exactly why the limit is one word when no dictionary is available.
            r = SmartSelection.ComputeTail("Please read l;ylfu c9j g4njv", layouts, 0, 300, 5, null);
            EqInt("no judge: would swallow the real words", r.CharCount, 28);

            // The word limit still applies on top of the judge.
            r = SmartSelection.ComputeTail("l;ylfu c9j g4njv", layouts, 0, 300, 2, judge);
            EqInt("judge: word limit still caps the run", r.CharCount, 9);

            // Thai is the source: no English dictionary can say anything useful
            // about it, so the judge must not be consulted at all.
            FakeJudge thaiJudge = new FakeJudge();
            r = SmartSelection.ComputeTail(sawatdi + " " + sawatdi, layouts, 0, 300, 5, thaiJudge);
            EqInt("judge: Thai source takes the run", r.CharCount, 13);
            EqInt("judge: not consulted for a Thai source", thaiJudge.Asked, 0);

            // A judge that calls everything a real word can only ever shrink the
            // result to the single word next to the caret; it can never expand it.
            FakeJudge everything = new FakeJudge("l;ylfu", "c9j");
            r = SmartSelection.ComputeTail("l;ylfu c9j", layouts, 0, 300, 5, everything);
            EqInt("judge: worst case is still one word", r.CharCount, 3);
        }

        // -------------------------------------------------------------------
        /// Measured on Windows 11: a TextBox and a RichTextBox both move the
        /// caret by grapheme cluster, so "สวัสดี" (6 UTF-16 units, 4 clusters)
        /// took 8 units when crossed with 6 presses. Counting characters
        /// overshoots past the anchor and leaves text after the caret selected.
        private static void TextUnitCases()
        {
            string sawatdi = U(0x0E2A, 0x0E27, 0x0E31, 0x0E2A, 0x0E14, 0x0E35);   // 6 units, 4 clusters
            string emoji = char.ConvertFromUtf32(0x1F600) + char.ConvertFromUtf32(0x1F601); // 4 units, 2 clusters

            EqInt("presses: ascii equals length", TextUnits.PressCount("abcdef"), 6);
            EqInt("presses: Thai combining marks ride along", TextUnits.PressCount(sawatdi), 4);
            EqInt("presses: surrogate pairs count once", TextUnits.PressCount(emoji), 2);
            EqInt("presses: empty", TextUnits.PressCount(""), 0);
            EqInt("presses: null", TextUnits.PressCount(null), 0);

            // The prefix form is what decides how far to shrink a selection.
            EqInt("prefix presses: none", TextUnits.PressCountForPrefix("abc " + sawatdi, 0), 0);
            EqInt("prefix presses: ascii prefix", TextUnits.PressCountForPrefix("abc " + sawatdi, 4), 4);
            EqInt("prefix presses: Thai prefix", TextUnits.PressCountForPrefix(sawatdi + " abc", 7), 5);
            EqInt("prefix presses: whole string", TextUnits.PressCountForPrefix(sawatdi, 99), 4);

            // A count landing inside a cluster must round up to its boundary, so
            // a selection can never begin half way through a character.
            EqInt("prefix presses: mid-cluster rounds up", TextUnits.PressCountForPrefix(sawatdi, 3), 2);

            // The whole point: prefix presses plus tail presses cover the string
            // exactly, so shrinking then reselecting cannot drift.
            int tailChars = sawatdi.Length;
            string line = "abc " + sawatdi;
            EqInt("prefix + tail presses covers the line",
                  TextUnits.PressCountForPrefix(line, line.Length - tailChars) + TextUnits.PressCount(sawatdi),
                  TextUnits.PressCount(line));
        }

        // -------------------------------------------------------------------
        private static void UndoCases()
        {
            DateTime now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            UndoMemo memo = new UndoMemo();

            True("undo: nothing to offer at first", !memo.IsOffered(now, 5, IntPtr.Zero), "");

            IntPtr win = new IntPtr(1234);
            memo.Remember("l;ylfu", U(0x0E2A, 0x0E27, 0x0E31, 0x0E2A, 0x0E14, 0x0E35), 0x0409, win);
            True("undo: offered right after a conversion", memo.IsOffered(DateTime.UtcNow, 5, win), "");
            True("undo: not offered to a different window", !memo.IsOffered(DateTime.UtcNow, 5, new IntPtr(9999)),
                 "same text in another window must not be overwritten");
            True("undo: expires", !memo.IsOffered(DateTime.UtcNow.AddSeconds(30), 5, win), "30 s later");
            True("undo: a zero window disables it", !memo.IsOffered(DateTime.UtcNow, 0, win), "");

            True("undo: matches what was pasted",
                 memo.Matches(U(0x0E2A, 0x0E27, 0x0E31, 0x0E2A, 0x0E14, 0x0E35)), "");
            True("undo: rejects anything else", !memo.Matches("something the user typed"), "");
            True("undo: comparison is case-sensitive", !memo.Matches("L;YLFU"), "");
            Eq("undo: keeps the original", memo.Original, "l;ylfu");
            EqInt("undo: keeps the source language", memo.SourceLangId, 0x0409);

            memo.Clear();
            True("undo: cleared after use", !memo.IsOffered(DateTime.UtcNow, 5, win), "");

            // Restoring re-selects by character count, which cannot cross a line
            // break, so a multi-line conversion must not be offered back.
            UndoMemo multiline = new UndoMemo();
            multiline.Remember("a b", "x\r\ny", 0x0409, win);
            True("undo: multi-line conversions are not offered", !multiline.IsOffered(DateTime.UtcNow, 5, win), "");
        }

        // -------------------------------------------------------------------
        private static void HotkeyCases()
        {
            HotkeySpec caps = HotkeySpec.Parse("CapsLock");
            EqInt("hotkey: CapsLock is VK_CAPITAL", caps.Vk, Native.VK_CAPITAL);
            True("hotkey: CapsLock has no modifiers",
                 !caps.Ctrl && !caps.Alt && !caps.Shift && !caps.Win, caps.Display);

            // Keys.Capital and Keys.CapsLock share a value and ToString picks the
            // first declared, so the key would otherwise be shown as "Capital".
            Eq("hotkey: CapsLock is named properly", caps.Display, "CapsLock");
            Eq("hotkey: so is the other spelling", HotkeySpec.Parse("Capital").Display, "CapsLock");

            HotkeySpec winSpace = HotkeySpec.Parse("Win+Space");
            True("hotkey: Win+Space parses", winSpace.Win && winSpace.Vk == (int)Keys.Space, winSpace.Display);
            Eq("hotkey: Win+Space round trips", winSpace.Display, "Win+Space");

            // Both defaults must parse, or the program starts up with a feature
            // silently switched off.
            Settings dflt = new Settings();
            bool parsed = true;
            try { HotkeySpec.Parse(dflt.LangKey); HotkeySpec.Parse(dflt.CaseKey); HotkeySpec.Parse(dflt.Hotkey); }
            catch (Exception ex) { parsed = false; Console.WriteLine("        " + ex.Message); }
            True("hotkey: every default parses", parsed, dflt.Hotkey + " / " + dflt.LangKey + " / " + dflt.CaseKey);

            // A settings.json from before these keys existed must switch both on
            // rather than leave the user with the features missing.
            Dictionary<string, string> old = Settings.ParseFlatJson("{ \"Hotkey\": \"Ctrl+Alt+Space\" }");
            True("hotkey: an older file has no shared keys",
                 !old.ContainsKey("langkey") && !old.ContainsKey("casekey"), "as expected");
            Eq("hotkey: default fills in Win+Space", new Settings().LangKey, "Win+Space");
            Eq("hotkey: default fills in CapsLock", new Settings().CaseKey, "CapsLock");
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

            // The ignore list is an array, and arrays used to be skipped along
            // with objects.
            string json = "{ \"Hotkey\": \"Win+Space\", \"IgnoreApps\": [\"valorant.exe\", \"cs2.exe\"], " +
                          "\"UndoWindowSeconds\": 7 }";
            flat = Settings.ParseFlatJson(json);
            True("settings: array captured", flat.ContainsKey("ignoreapps") && flat["ignoreapps"].Contains("valorant"),
                 flat.ContainsKey("ignoreapps") ? flat["ignoreapps"] : "<missing>");
            Eq("settings: value after an array still read", flat["undowindowseconds"], "7");

            // A full round trip through the file, which is what actually has to
            // survive a restart.
            string dir = Path.Combine(Path.GetTempPath(), "kbfix-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dir);
                Settings written = new Settings();
                written.Hotkey = "Ctrl+Alt+K";
                written.SmartSelection = false;
                written.MaxSmartWords = 4;
                written.UndoWindowSeconds = 9;
                written.IgnoreApps = new string[] { "valorant.exe", "cs2.exe" };
                // Empty is how the shared keys are turned off, and it has to
                // survive the file: an empty string that came back as the
                // default would silently switch the feature on again.
                written.CaseKey = "";
                string why;
                True("settings: saved", written.Save(dir, out why), why == null ? "" : why);

                Settings loaded = Settings.Load(dir, out why);
                Eq("settings: hotkey round trip", loaded.Hotkey, "Ctrl+Alt+K");
                Eq("settings: shared key round trip", loaded.LangKey, "Win+Space");
                Eq("settings: a shared key turned off stays off", loaded.CaseKey, "");
                True("settings: bool round trip", loaded.SmartSelection == false, "SmartSelection");
                EqInt("settings: int round trip", loaded.MaxSmartWords, 4);
                EqInt("settings: undo window round trip", loaded.UndoWindowSeconds, 9);
                EqInt("settings: ignore list round trip", loaded.IgnoreApps.Length, 2);
                Eq("settings: ignore list content", string.Join(",", loaded.IgnoreApps), "valorant.exe,cs2.exe");
            }
            finally
            {
                try { Directory.Delete(dir, true); }
                catch { }
            }
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
