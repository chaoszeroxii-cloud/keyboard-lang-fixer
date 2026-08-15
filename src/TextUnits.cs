// ---------------------------------------------------------------------------
//  How many times to press an arrow key to cross a piece of text.
//
//  This is NOT the string's Length. Measured on Windows 11, in both a plain
//  TextBox (Edit) and a RichTextBox (RichEdit):
//
//      "สวัสดี"      6 UTF-16 units, 4 clusters -> 6 presses selected 8 units
//      two emoji     4 UTF-16 units, 2 clusters -> 4 presses selected 6 units
//      "abcdef"      6 UTF-16 units, 6 clusters -> 6 presses selected 6 units
//
//  Caret movement follows grapheme clusters, so a Thai vowel or tone mark rides
//  along with its base character and a surrogate pair counts once. Counting
//  UTF-16 units overshoots by exactly the number of combining marks -- which,
//  for a tool whose main job is Thai, is most of the time.
//
//  Overshooting is not merely "selects too much": the selections this program
//  builds are anchored at the user's caret and extend left, so an overshoot to
//  the right runs past the anchor and leaves text AFTER the caret selected. The
//  next character the user types would replace it.
// ---------------------------------------------------------------------------
using System.Globalization;

namespace KbFix
{
    internal static class TextUnits
    {
        /// Number of arrow-key presses needed to cross the whole string.
        public static int PressCount(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return new StringInfo(text).LengthInTextElements;
        }

        /// Number of presses needed to cross the first $charCount UTF-16 units,
        /// which is how the caller expresses "everything except the tail I want".
        /// A count landing inside a cluster is rounded up to the cluster
        /// boundary, so the selection can never start mid-character.
        public static int PressCountForPrefix(string text, int charCount)
        {
            if (string.IsNullOrEmpty(text) || charCount <= 0) return 0;
            if (charCount >= text.Length) return PressCount(text);

            int presses = 0;
            int index = 0;
            TextElementEnumerator e = StringInfo.GetTextElementEnumerator(text);
            while (e.MoveNext())
            {
                string element = (string)e.Current;
                if (index >= charCount) break;
                presses++;
                index += element.Length;
            }
            return presses;
        }
    }
}
