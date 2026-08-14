// ---------------------------------------------------------------------------
//  The conversion engine: keyboard layouts, the character mapping, and the
//  pure logic behind Smart Selection.
//
//  Everything in this file is deliberately free of UI, clipboard and keyboard
//  side effects, so it can be tested exhaustively without driving a window.
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace KbFix
{
    /// One Caps Lock state of one layout: which character each physical key
    /// produces, and the reverse.
    internal sealed class LayoutMap
    {
        public readonly Dictionary<string, char> Forward = new Dictionary<string, char>();
        public readonly Dictionary<char, string> Reverse = new Dictionary<char, string>();

        public void Add(string position, char c)
        {
            Forward[position] = c;
            // A character can sit on more than one key in some layouts; the
            // first one wins so the mapping stays a function.
            if (!Reverse.ContainsKey(c)) Reverse[c] = position;
        }
    }

    /// A keyboard layout, described as "which character does each physical key
    /// produce", in both directions and for both Caps Lock states.
    ///
    /// Converting between two layouts is then: look the character up in the
    /// source layout's Reverse map to get the key it sits on, and ask the target
    /// layout what that same key produces. Nothing about that is language
    /// specific, which is why any pair of installed layouts works.
    internal sealed class Layout
    {
        public string Name;
        public int LangId;
        public IntPtr Hkl;
        public bool BuiltIn;
        public readonly LayoutMap[] Maps = new LayoutMap[] { new LayoutMap(), new LayoutMap() };

        public LayoutMap Map(int caps) { return Maps[caps == 0 ? 0 : 1]; }
        public int KeyCount { get { return Maps[0].Forward.Count; } }

        /// Whether this layout is the one producing ordinary Latin letters. Only
        /// then is an English dictionary any use for judging its words -- Windows
        /// has no Thai dictionary, and asking an English one about Thai text
        /// would answer "misspelled" to everything.
        public bool ProducesLatinLetters
        {
            get
            {
                LayoutMap m = Maps[0];
                int hits = 0;
                foreach (char c in "abcdefghijklmnopqrstuvwxyz")
                    if (m.Reverse.ContainsKey(c)) hits++;
                return hits >= 20;
            }
        }

        public override string ToString() { return Name; }
    }

    internal static class Layouts
    {
        // -------------------------------------------------------------------
        //  Built-in Thai Kedmanee table
        // -------------------------------------------------------------------
        //  Rows 0-3 are the unshifted keys, rows 4-7 the shifted ones in the
        //  same key order, so row r and row r+4 are the two halves of one key.
        //  The Thai side is written as Unicode code points so the mapping data
        //  cannot be corrupted by a bad file encoding.
        private static readonly string[] EnRows = new string[] {
            "`1234567890-=",
            "qwertyuiop[]\\",
            "asdfghjkl;'",
            "zxcvbnm,./",
            "~!@#$%^&*()_+",
            "QWERTYUIOP{}|",
            "ASDFGHJKL:\"",
            "ZXCVBNM<>?"
        };

        private static readonly string[] ThRows = new string[] {
            //  `    1    2    3    4    5    6    7    8    9    0    -    =
            "005F 0E45 002F 002D 0E20 0E16 0E38 0E36 0E04 0E15 0E08 0E02 0E0A",
            //  q    w    e    r    t    y    u    i    o    p    [    ]    \
            "0E46 0E44 0E33 0E1E 0E30 0E31 0E35 0E23 0E19 0E22 0E1A 0E25 0E03",
            //  a    s    d    f    g    h    j    k    l    ;    '
            "0E1F 0E2B 0E01 0E14 0E40 0E49 0E48 0E32 0E2A 0E27 0E07",
            //  z    x    c    v    b    n    m    ,    .    /
            "0E1C 0E1B 0E41 0E2D 0E34 0E37 0E17 0E21 0E43 0E1D",
            //  ~    !    @    #    $    %    ^    &    *    (    )    _    +
            "0025 002B 0E51 0E52 0E53 0E54 0E39 0E3F 0E55 0E56 0E57 0E58 0E59",
            //  Q    W    E    R    T    Y    U    I    O    P    {    }    |
            "0E50 0022 0E0E 0E11 0E18 0E4D 0E4A 0E13 0E2F 0E0D 0E10 002C 0E05",
            //  A    S    D    F    G    H    J    K    L    :    "
            "0E24 0E06 0E0F 0E42 0E0C 0E47 0E4B 0E29 0E28 0E0B 002E",
            //  Z    X    C    V    B    N    M    <    >    ?
            "0028 0029 0E09 0E2E 0E3A 0E4C 003F 0E12 0E2C 0E26"
        };

        /// The shipped table, expressed as two layouts so it goes through the
        /// same conversion code as the probed ones. Used when the machine has
        /// fewer than two usable layouts installed, and as the oracle the self
        /// test checks the probed Thai layout against.
        public static Layout[] BuiltIn()
        {
            Layout en = new Layout();
            en.Name = "English (US, built-in table)";
            en.LangId = 0x0409;
            en.BuiltIn = true;

            Layout th = new Layout();
            th.Name = "Thai (Kedmanee, built-in table)";
            th.LangId = 0x041E;
            th.BuiltIn = true;

            int half = EnRows.Length / 2;
            Dictionary<string, char> enOff = new Dictionary<string, char>();
            Dictionary<string, char> thOff = new Dictionary<string, char>();

            for (int r = 0; r < EnRows.Length; r++)
            {
                string enRow = EnRows[r];
                string[] parts = ThRows[r].Split(' ');
                if (enRow.Length != parts.Length)
                    throw new Exception("Built-in layout row " + r + " is misaligned: " +
                                        enRow.Length + " EN keys vs " + parts.Length + " TH chars.");
                for (int i = 0; i < enRow.Length; i++)
                {
                    // "<key identity>-<was shift held>", so the unshifted row and
                    // the shifted row of one key share a key identity.
                    string pos = "b" + (r % half) + "_" + i + "-" + (r >= half ? 1 : 0);
                    enOff[pos] = enRow[i];
                    thOff[pos] = (char)int.Parse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                }
            }

            foreach (KeyValuePair<string, char> kv in enOff)
            {
                en.Map(0).Add(kv.Key, kv.Value);
                // Caps Lock on a US layout inverts the case of letters and
                // leaves every other key alone.
                char c = kv.Value;
                char capped = c;
                if (char.IsLetter(c)) capped = char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c);
                en.Map(1).Add(kv.Key, capped);
            }

            foreach (KeyValuePair<string, char> kv in thOff)
            {
                th.Map(0).Add(kv.Key, kv.Value);
                // Caps Lock on the Thai layout acts as a second Shift on every
                // key, so the capped character is the other shift half.
                int dash = kv.Key.LastIndexOf('-');
                string keyId = kv.Key.Substring(0, dash);
                int shift = int.Parse(kv.Key.Substring(dash + 1), CultureInfo.InvariantCulture);
                th.Map(1).Add(kv.Key, thOff[keyId + "-" + (shift == 0 ? 1 : 0)]);
            }

            return new Layout[] { en, th };
        }

        // -------------------------------------------------------------------
        //  Layouts installed on this machine
        // -------------------------------------------------------------------
        public static string DisplayName(int langId)
        {
            try { return CultureInfo.GetCultureInfo(langId).DisplayName; }
            catch { return "Layout 0x" + langId.ToString("X4", CultureInfo.InvariantCulture); }
        }

        public static Layout[] Installed()
        {
            List<Layout> result = new List<Layout>();
            Dictionary<int, bool> seen = new Dictionary<int, bool>();

            foreach (IntPtr hkl in Native.InstalledLayouts())
            {
                int langId = (int)(hkl.ToInt64() & 0xFFFF);
                if (seen.ContainsKey(langId)) continue;   // same language, different IME
                seen[langId] = true;

                Dictionary<int, Dictionary<string, char>> probed = LayoutProbe.Probe(hkl);
                if (probed[0].Count == 0) continue;

                Layout l = new Layout();
                l.Name = DisplayName(langId);
                l.LangId = langId;
                l.Hkl = hkl;
                for (int caps = 0; caps <= 1; caps++)
                    foreach (KeyValuePair<string, char> kv in probed[caps])
                        l.Map(caps).Add(kv.Key, kv.Value);
                result.Add(l);
            }
            return result.ToArray();
        }

        /// The set the program actually converts with: the installed layouts, or
        /// the built-in Thai table when the machine has fewer than two.
        public static Layout[] Resolve(out bool usedBuiltIn)
        {
            Layout[] live;
            try { live = Installed(); }
            catch { live = new Layout[0]; }

            if (live.Length >= 2) { usedBuiltIn = false; return live; }
            usedBuiltIn = true;
            return BuiltIn();
        }
    }

    // -----------------------------------------------------------------------
    //  Conversion
    // -----------------------------------------------------------------------
    internal static class Converter
    {
        /// $caps is the Caps Lock state the text was typed under. It has to be
        /// part of the lookup rather than applied afterwards, because layouts
        /// implement Caps Lock differently: with it on, a US layout still gives
        /// "1" on the number row while a Thai one gives the shifted character.
        /// Reading the character back through the same Caps Lock state it was
        /// typed with recovers the physical key and whether Shift was really
        /// held, which is the only thing worth carrying across.
        public static string Convert(string text, Layout from, Layout to, int caps)
        {
            if (string.IsNullOrEmpty(text)) return text;
            Dictionary<char, string> reverse = from.Map(caps).Reverse;
            Dictionary<string, char> forward = to.Map(caps).Forward;

            StringBuilder sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                string pos;
                char mapped;
                if (reverse.TryGetValue(c, out pos) && forward.TryGetValue(pos, out mapped)) sb.Append(mapped);
                else sb.Append(c);   // spaces, newlines, emoji, keys the target lacks
            }
            return sb.ToString();
        }

        /// How many characters of the text only this layout could have produced.
        /// Characters every layout shares (digits, most punctuation) say nothing
        /// about which layout was in use, so they are not counted.
        private static void Score(string text, Layout layout, Layout[] all, int caps,
                                 out int distinctive, out int total)
        {
            distinctive = 0; total = 0;
            Dictionary<char, string> mine = layout.Map(caps).Reverse;
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c)) continue;
                if (!mine.ContainsKey(c)) continue;
                total++;
                bool elsewhere = false;
                foreach (Layout other in all)
                {
                    if (ReferenceEquals(other, layout)) continue;
                    if (other.Map(caps).Reverse.ContainsKey(c)) { elsewhere = true; break; }
                }
                if (!elsewhere) distinctive++;
            }
        }

        /// Works out which layout the text was actually typed on, or null when
        /// nothing in it is specific to any one layout and converting would be a
        /// guess.
        public static Layout SelectSource(string text, Layout[] layouts, int caps)
        {
            if (layouts == null || layouts.Length < 2 || string.IsNullOrEmpty(text)) return null;

            Layout best = null;
            int bestDistinctive = 0, bestTotal = 0;
            foreach (Layout l in layouts)
            {
                int distinctive, total;
                Score(text, l, layouts, caps, out distinctive, out total);
                if (distinctive > bestDistinctive || (distinctive == bestDistinctive && total > bestTotal))
                {
                    best = l; bestDistinctive = distinctive; bestTotal = total;
                }
            }
            return bestDistinctive > 0 ? best : null;
        }

        /// Picks what to convert into. With the usual two layouts installed
        /// there is only one answer; with more, the language Windows just
        /// switched to is the best evidence of what the user meant.
        public static Layout SelectTarget(Layout source, Layout[] layouts)
        {
            List<Layout> candidates = new List<Layout>();
            foreach (Layout l in layouts) if (!ReferenceEquals(l, source)) candidates.Add(l);
            if (candidates.Count == 0) return null;
            if (candidates.Count == 1) return candidates[0];

            int activeLang = Native.ForegroundLangId();
            foreach (Layout l in candidates) if (l.LangId == activeLang) return l;
            return candidates[0];
        }
    }

    // -----------------------------------------------------------------------
    //  Smart Selection
    // -----------------------------------------------------------------------
    internal enum TokenKind
    {
        /// Only characters that every layout can produce: digits, shared
        /// punctuation. Says nothing about which layout was in use.
        Neutral,
        /// Distinctive to exactly one layout.
        Single,
        /// Distinctive characters from more than one layout, so this token is a
        /// boundary rather than part of a mistyped run.
        Mixed
    }

    internal sealed class TailResult
    {
        /// Characters at the end of the line to select and convert. 0 means
        /// "decline", and the caller must leave the document alone.
        public int CharCount;
        public Layout Source;
        public string Reason = "";
    }

    /// Works out how much of the text before the caret was typed in the wrong
    /// layout, so the user does not have to select it by hand.
    ///
    /// The caller selects from the caret to the start of the line (a single
    /// Shift+Home, which no application interprets ambiguously), reads that
    /// text, and asks this class how many trailing CHARACTERS to keep. Character
    /// counts are used rather than word counts on purpose: Ctrl+Shift+Left
    /// disagrees between applications about whether punctuation splits a word,
    /// so "l;ylfu" is one word in some and three in others, and any count based
    /// on it would silently select the wrong span.
    ///
    /// Only the word next to the caret is taken by default, and that limit is
    /// the whole design rather than timidity. Text typed on the wrong layout is
    /// indistinguishable from text meant for that layout: "Please read l;ylfu"
    /// is three words that all look like English-layout typing, so a rule that
    /// walked left through same-layout words would convert the perfectly good
    /// "Please read" as well. Going the other way is safe -- Thai characters
    /// cannot be legitimate English -- but a rule that only works in one
    /// direction is worse than a rule that is predictable in both. Raise
    /// MaxSmartWords if the mistakes being fixed are usually several words long.
    ///
    /// Thai loses nothing to this limit, because Thai is written without spaces
    /// between words: a whole mistyped phrase is a single token.
    internal static class SmartSelection
    {
        public const int DefaultMaxChars = 300;
        public const int DefaultMaxWords = 1;

        internal struct Token
        {
            public int Start;
            public int Length;
            public int End { get { return Start + Length; } }
        }

        /// Non-whitespace runs, left to right.
        internal static List<Token> Tokenize(string text)
        {
            List<Token> tokens = new List<Token>();
            int i = 0;
            while (i < text.Length)
            {
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                if (i >= text.Length) break;
                int start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
                Token t = new Token();
                t.Start = start;
                t.Length = i - start;
                tokens.Add(t);
            }
            return tokens;
        }

        internal static TokenKind Classify(string token, Layout[] layouts, int caps, out Layout owner)
        {
            owner = null;
            List<Layout> hits = new List<Layout>();
            foreach (Layout l in layouts)
            {
                Dictionary<char, string> mine = l.Map(caps).Reverse;
                bool distinctive = false;
                foreach (char c in token)
                {
                    if (!mine.ContainsKey(c)) continue;
                    bool elsewhere = false;
                    foreach (Layout other in layouts)
                    {
                        if (ReferenceEquals(other, l)) continue;
                        if (other.Map(caps).Reverse.ContainsKey(c)) { elsewhere = true; break; }
                    }
                    if (!elsewhere) { distinctive = true; break; }
                }
                if (distinctive) hits.Add(l);
            }
            if (hits.Count == 0) return TokenKind.Neutral;
            if (hits.Count > 1) return TokenKind.Mixed;
            owner = hits[0];
            return TokenKind.Single;
        }

        public static TailResult ComputeTail(string linePrefix, Layout[] layouts, int caps)
        {
            return ComputeTail(linePrefix, layouts, caps, DefaultMaxChars, DefaultMaxWords, null);
        }

        public static TailResult ComputeTail(string linePrefix, Layout[] layouts, int caps, int maxChars)
        {
            return ComputeTail(linePrefix, layouts, caps, maxChars, DefaultMaxWords, null);
        }

        public static TailResult ComputeTail(string linePrefix, Layout[] layouts, int caps,
                                             int maxChars, int maxWords)
        {
            return ComputeTail(linePrefix, layouts, caps, maxChars, maxWords, null);
        }

        /// $judge, when supplied, ends the run at the first word that is a real
        /// word of the source layout's language. That is the signal that makes
        /// multi-word runs safe: without it there is no way to tell "Please read"
        /// from wrong-layout typing, because both are simply text the Latin
        /// layout can produce.
        ///
        /// It is deliberately only ever a reason to STOP. A word the dictionary
        /// dislikes does not extend the run on its own -- "github" and
        /// "getUserId" are both reported misspelled -- so the word limit still
        /// bounds how much can be taken.
        public static TailResult ComputeTail(string linePrefix, Layout[] layouts, int caps,
                                             int maxChars, int maxWords, IWordJudge judge)
        {
            if (maxWords < 1) maxWords = 1;
            TailResult r = new TailResult();
            if (layouts == null || layouts.Length < 2) { r.Reason = "fewer than two layouts"; return r; }
            if (string.IsNullOrEmpty(linePrefix)) { r.Reason = "nothing before the caret"; return r; }

            // Shift+Home cannot cross a line, so a line break here means the
            // caller grabbed something unexpected. Character counting would be
            // off by one per CRLF, so decline rather than mis-select.
            if (linePrefix.IndexOf('\n') >= 0 || linePrefix.IndexOf('\r') >= 0)
            {
                r.Reason = "text spans more than one line";
                return r;
            }

            List<Token> tokens = Tokenize(linePrefix);
            if (tokens.Count == 0) { r.Reason = "only whitespace before the caret"; return r; }

            // The token nearest the caret is the one the user just typed. If it
            // is not clearly from one layout there is nothing to fix here.
            Layout owner;
            int last = tokens.Count - 1;
            TokenKind kind = Classify(linePrefix.Substring(tokens[last].Start, tokens[last].Length),
                                      layouts, caps, out owner);
            if (kind != TokenKind.Single)
            {
                r.Reason = kind == TokenKind.Neutral
                    ? "the word before the caret is not specific to any layout"
                    : "the word before the caret mixes layouts";
                return r;
            }

            r.Source = owner;
            int runStartToken = last;

            // The dictionary only understands the layout that produces Latin
            // letters, and only that direction has the ambiguity worth resolving.
            IWordJudge activeJudge = (judge != null && owner.ProducesLatinLetters) ? judge : null;
            string stopNote = null;

            // Walk left while the run keeps belonging to the same layout, up to
            // the word limit. Neutral tokens (a lone dash, shared punctuation)
            // do not end the run, but they only get absorbed if a same-layout
            // token turns up further left -- otherwise they are just text the
            // user did not mistype. They do not count against the word limit
            // either, since they are carried along rather than chosen.
            int wordsTaken = 1;
            for (int j = last - 1; j >= 0 && wordsTaken < maxWords; j--)
            {
                string token = linePrefix.Substring(tokens[j].Start, tokens[j].Length);
                Layout tokenOwner;
                TokenKind k = Classify(token, layouts, caps, out tokenOwner);
                if (k == TokenKind.Single && ReferenceEquals(tokenOwner, owner))
                {
                    if (activeJudge != null && activeJudge.LooksIntentional(token))
                    {
                        stopNote = "stopped at '" + token + "', a real word";
                        break;
                    }
                    runStartToken = j;
                    wordsTaken++;
                }
                else if (k == TokenKind.Neutral) continue;
                else break;    // another layout, or a mix: the run ends here
            }

            // Keep everything from the start of the run to the caret, trailing
            // whitespace included -- whitespace maps to itself, so including it
            // costs nothing and keeps the arithmetic simple.
            int charCount = linePrefix.Length - tokens[runStartToken].Start;

            // Too long: give up whole tokens from the left until it fits.
            while (charCount > maxChars && runStartToken < last)
            {
                runStartToken++;
                charCount = linePrefix.Length - tokens[runStartToken].Start;
            }
            if (charCount > maxChars)
            {
                r.Source = null;
                r.Reason = "the word before the caret is longer than " + maxChars + " characters";
                return r;
            }

            r.CharCount = charCount;
            r.Reason = stopNote == null ? "ok" : "ok, " + stopNote;
            return r;
        }
    }
}
