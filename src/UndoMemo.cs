// ---------------------------------------------------------------------------
//  Remembering the last conversion so pressing the hotkey again puts the
//  original text back.
//
//  Converting twice already round-trips, because the character mapping is a
//  bijection -- but that is re-conversion, not undo. It re-derives the span,
//  which may not be the same span, and it costs a full convert cycle. This
//  restores the exact characters that were there before, and only after
//  checking that what sits at the caret is still what the tool put there.
// ---------------------------------------------------------------------------
using System;

namespace KbFix
{
    internal sealed class UndoMemo
    {
        private string _original;
        private string _converted;
        private int _sourceLangId;
        private DateTime _at;

        public bool HasEntry { get { return _converted != null; } }
        public string Converted { get { return _converted; } }
        public string Original { get { return _original; } }
        public int SourceLangId { get { return _sourceLangId; } }

        public void Remember(string original, string converted, int sourceLangId)
        {
            _original = original;
            _converted = converted;
            _sourceLangId = sourceLangId;
            _at = DateTime.UtcNow;
        }

        public void Clear()
        {
            _original = null;
            _converted = null;
            _sourceLangId = 0;
        }

        /// Whether an undo is still on offer. Kept as its own testable decision
        /// rather than being buried in the keystroke code.
        public bool IsOffered(DateTime nowUtc, int windowSeconds)
        {
            if (windowSeconds <= 0) return false;
            if (_converted == null || _original == null) return false;
            if (_converted.Length == 0) return false;

            // Restoring means re-selecting the converted text by character
            // count, which cannot cross a line break, so a multi-line
            // conversion is never offered back.
            if (_converted.IndexOf('\n') >= 0 || _converted.IndexOf('\r') >= 0) return false;

            double age = (nowUtc - _at).TotalSeconds;
            if (age < 0) return false;                 // clock moved; do not guess
            return age <= windowSeconds;
        }

        /// True when the text found at the caret is what this memo expects, which
        /// is the check that stops an undo overwriting something else the user
        /// has typed since.
        public bool Matches(string textAtCaret)
        {
            return _converted != null && string.Equals(textAtCaret, _converted, StringComparison.Ordinal);
        }
    }
}
