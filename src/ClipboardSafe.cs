// ---------------------------------------------------------------------------
//  Clipboard access, and reading the current selection without destroying what
//  the user had copied.
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Threading;
using System.Windows.Forms;

namespace KbFix
{
    /// What was on the clipboard before we overwrote it.
    ///
    /// Text is kept as a plain string, which is the case that matters almost
    /// every time and costs nothing. Anything else -- an image, a set of files,
    /// rich content -- is copied format by format into a detached DataObject,
    /// because a text-only restore would silently throw it away. That copy is
    /// only ever paid for when the clipboard actually holds such content.
    internal sealed class ClipboardSnapshot
    {
        public string Text;
        public IDataObject Data;
        public bool Empty { get { return Text == null && Data == null; } }
        public string Kind = "empty";

        /// Formats worth carrying across. Copying every advertised format can
        /// mean pulling megabytes of alternative renderings out of Office, so
        /// this sticks to the ones a paste actually uses.
        private static readonly string[] InterestingFormats = new string[] {
            DataFormats.UnicodeText, DataFormats.Text, DataFormats.Rtf, DataFormats.Html,
            DataFormats.Bitmap, DataFormats.Dib, DataFormats.Tiff, DataFormats.EnhancedMetafile,
            DataFormats.FileDrop, "PNG", "image/png", "HTML Format"
        };

        public static ClipboardSnapshot Take()
        {
            ClipboardSnapshot snap = new ClipboardSnapshot();
            try
            {
                bool hasText = ClipboardSafe.Retry<bool>(delegate { return Clipboard.ContainsText(); }, false);
                bool hasImage = ClipboardSafe.Retry<bool>(delegate { return Clipboard.ContainsImage(); }, false);
                bool hasFiles = ClipboardSafe.Retry<bool>(delegate { return Clipboard.ContainsFileDropList(); }, false);

                // Fast path: plain text and nothing else. No data is duplicated.
                if (hasText && !hasImage && !hasFiles)
                {
                    snap.Text = ClipboardSafe.GetText();
                    snap.Kind = "text";
                    return snap;
                }
                if (!hasText && !hasImage && !hasFiles)
                {
                    // Could still be some private format. Try a generic copy,
                    // and treat failure as "nothing to preserve".
                    snap.Data = CopyDataObject();
                    snap.Kind = snap.Data != null ? "other" : "empty";
                    return snap;
                }

                snap.Data = CopyDataObject();
                if (snap.Data == null && hasText) { snap.Text = ClipboardSafe.GetText(); snap.Kind = "text"; }
                else snap.Kind = hasImage ? "image" : (hasFiles ? "files" : "mixed");
            }
            catch
            {
                snap.Text = null; snap.Data = null; snap.Kind = "unreadable";
            }
            return snap;
        }

        private static IDataObject CopyDataObject()
        {
            try
            {
                IDataObject source = ClipboardSafe.Retry<IDataObject>(
                    delegate { return Clipboard.GetDataObject(); }, null);
                if (source == null) return null;

                // false: native formats only, so auto-converted duplicates are
                // not copied twice.
                string[] formats = source.GetFormats(false);
                if (formats == null || formats.Length == 0) return null;

                DataObject copy = new DataObject();
                int kept = 0;
                foreach (string format in formats)
                {
                    if (Array.IndexOf(InterestingFormats, format) < 0) continue;
                    try
                    {
                        object data = source.GetData(format, false);
                        if (data == null) continue;
                        // StringCollection needs re-wrapping or a paste target
                        // will not see a file list.
                        if (data is StringCollection) copy.SetFileDropList((StringCollection)data);
                        else copy.SetData(format, false, data);
                        kept++;
                    }
                    catch { /* a format that refuses to be read is not worth failing over */ }
                }
                return kept > 0 ? copy : null;
            }
            catch { return null; }
        }

        /// Puts back what the user had. When nothing could be preserved the
        /// converted text is left behind rather than clearing the clipboard: the
        /// original is gone either way, and text is the less annoying leftover.
        public void Restore()
        {
            if (Data != null)
            {
                ClipboardSafe.Retry<bool>(delegate
                {
                    Clipboard.SetDataObject(Data, true);
                    return true;
                }, false);
                return;
            }
            if (!string.IsNullOrEmpty(Text)) ClipboardSafe.SetText(Text);
        }
    }

    internal static class ClipboardSafe
    {
        /// Clipboard calls throw while another process owns the clipboard, so
        /// every one of them gets a few attempts.
        public static T Retry<T>(Func<T> action, T fallback)
        {
            for (int i = 0; i < 8; i++)
            {
                try { return action(); }
                catch { Thread.Sleep(30); }
            }
            return fallback;
        }

        public static string GetText()
        {
            return Retry<string>(delegate
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }, null);
        }

        public static bool SetText(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return Retry<bool>(delegate
            {
                Clipboard.SetText(text);
                return true;
            }, false);
        }

        /// True once some application has written to the clipboard, which is how
        /// a copy is detected without clearing the clipboard first. Clearing
        /// would throw away whatever the user had, including images and files a
        /// text-only restore could never put back.
        public static bool WaitForWrite(uint before, int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                Thread.Sleep(20);
                waited += 20;
                if (Native.GetClipboardSequenceNumber() != before) return true;
            }
            return false;
        }
    }
}
