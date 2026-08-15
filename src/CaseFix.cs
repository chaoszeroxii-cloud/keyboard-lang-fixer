// ---------------------------------------------------------------------------
//  Flipping the case of text typed with Caps Lock in the wrong state.
// ---------------------------------------------------------------------------
using System;
using System.Globalization;
using System.Text;

namespace KbFix
{
    /// The other half of the same mistake the layout converter fixes: the
    /// keyboard was in a state the user did not intend, and every keystroke
    /// came out wrong in a way that is exactly reversible.
    internal static class CaseFix
    {
        /// Swaps the case of every letter, which is precisely what Caps Lock did
        /// to the text in the first place -- including its interaction with
        /// Shift, where Caps Lock on turns a shifted letter LOWER case. Typing
        /// "Thailand" with Caps Lock stuck on produces "tHAILAND", and swapping
        /// each letter gives "Thailand" back.
        ///
        /// Deliberately not "capitalise the first letter": swapping is a true
        /// inverse, so applying it twice returns the original text byte for
        /// byte. That is what makes the fix safe to repeat and unnecessary to
        /// remember for undo.
        ///
        /// Characters with no case -- Thai, digits, punctuation, emoji -- are
        /// left exactly as they are, so a mixed selection only has its Latin (or
        /// Greek, or Cyrillic) letters touched.
        public static string Flip(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // The invariant culture, not the current one: under a Turkish
            // culture 'i' upper-cases to 'İ' and the result would neither match
            // what the keyboard produced nor survive a second flip.
            CultureInfo inv = CultureInfo.InvariantCulture;
            StringBuilder sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsUpper(c)) sb.Append(char.ToLower(c, inv));
                else if (char.IsLower(c)) sb.Append(char.ToUpper(c, inv));
                else sb.Append(c);
            }
            return sb.ToString();
        }

        /// True when flipping would change something. Used to decline quietly
        /// rather than paste an identical string over a selection, which would
        /// cost the user their undo history for no gain.
        public static bool HasCasedLetter(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < text.Length; i++)
                if (char.IsUpper(text[i]) || char.IsLower(text[i])) return true;
            return false;
        }
    }
}
