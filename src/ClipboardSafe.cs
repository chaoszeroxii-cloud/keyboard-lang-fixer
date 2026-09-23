// ---------------------------------------------------------------------------
//  Clipboard access, and reading the current selection without destroying what
//  the user had copied.
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
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
                bool hasText = ClipboardSafe.HasFormat(13);
                bool hasImage = ClipboardSafe.HasFormat(2) || ClipboardSafe.HasFormat(8);
                bool hasFiles = ClipboardSafe.HasFormat(15);

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
        public static IntPtr OwnerWindow;
        public const int WM_CLIPBOARDUPDATE = 0x031D;
        private static readonly AutoResetEvent Written = new AutoResetEvent(false);
        private static volatile bool _listening;

        [DllImport("user32.dll")]
        private static extern bool AddClipboardFormatListener(IntPtr window);
        [DllImport("user32.dll")]
        private static extern bool RemoveClipboardFormatListener(IntPtr window);

        public static void StartListening(IntPtr window)
        {
            _listening = AddClipboardFormatListener(window);
        }

        public static void StopListening(IntPtr window)
        {
            if (_listening) RemoveClipboardFormatListener(window);
            _listening = false;
            Written.Set();
        }

        public static void NotifyWrite() { Written.Set(); }

        [DllImport("user32.dll", EntryPoint = "IsClipboardFormatAvailable")]
        public static extern bool HasFormat(uint format);
        [DllImport("user32.dll")]
        private static extern bool OpenClipboard(IntPtr owner);
        [DllImport("user32.dll")]
        private static extern bool CloseClipboard();
        [DllImport("user32.dll")]
        private static extern bool EmptyClipboard();
        [DllImport("user32.dll")]
        private static extern IntPtr GetClipboardData(uint format);
        [DllImport("user32.dll")]
        private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalLock(IntPtr memory);
        [DllImport("kernel32.dll")]
        private static extern bool GlobalUnlock(IntPtr memory);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalFree(IntPtr memory);

        private static bool Open()
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                if (OpenClipboard(OwnerWindow)) return true;
                Thread.Sleep(1 + attempt * 2);
            }
            return false;
        }

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
            // Check the format only AFTER acquiring the clipboard. A producer
            // increments the sequence at EmptyClipboard, before publishing the
            // text; checking availability while it still holds the lock can
            // mistake an in-progress copy for an unsupported shortcut.
            if (!Open()) return null;
            try
            {
                if (!HasFormat(13)) return null;
                IntPtr memory = GetClipboardData(13);
                if (memory == IntPtr.Zero) return null;
                IntPtr chars = GlobalLock(memory);
                if (chars == IntPtr.Zero) return null;
                try { return Marshal.PtrToStringUni(chars); }
                finally { GlobalUnlock(memory); }
            }
            finally { CloseClipboard(); }
        }

        public static bool SetText(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            // Materialize CF_UNICODETEXT directly. OLE's SetText/flush crosses
            // apartments and can stall when the short-lived fix worker exits.
            // The OS owns this buffer after SetClipboardData succeeds; no
            // delayed rendering or message pump is needed to paste it later.
            IntPtr memory = GlobalAlloc(0x0042, new UIntPtr((uint)((text.Length + 1) * 2)));
            if (memory == IntPtr.Zero) return false;
            try
            {
                IntPtr chars = GlobalLock(memory);
                if (chars == IntPtr.Zero) return false;
                try
                {
                    char[] data = (text + "\0").ToCharArray();
                    Marshal.Copy(data, 0, chars, data.Length);
                }
                finally { GlobalUnlock(memory); }
                if (!Open()) return false;
                try
                {
                    if (!EmptyClipboard() || SetClipboardData(13, memory) == IntPtr.Zero) return false;
                    memory = IntPtr.Zero;
                    return true;
                }
                finally { CloseClipboard(); }
            }
            finally { if (memory != IntPtr.Zero) GlobalFree(memory); }
        }

        /// Sequence numbers are the authority; notifications only wake us.
        /// Do not Reset before waiting: a copy can arrive between the sequence
        /// check and WaitOne. AutoResetEvent retains that wake-up, and stale or
        /// coalesced notifications cannot turn an unchanged clipboard into a
        /// successful copy. The UI receives events while the STA worker waits.
        public static string WaitForText(uint before, int timeoutMs)
        {
            int start = Environment.TickCount;
            while (true)
            {
                bool changed = Native.GetClipboardSequenceNumber() != before;
                if (changed)
                {
                    string text = GetText();
                    if (text != null) return text;
                }
                int remaining = timeoutMs - unchecked(Environment.TickCount - start);
                if (remaining <= 0) return null;
                // A writer may still own the clipboard when the notification
                // arrives. Retry that transient failure with a bounded wait;
                // likewise remain functional if listener registration failed.
                Written.WaitOne(_listening && !changed ? remaining : Math.Min(10, remaining));
            }
        }
    }

    /// Leave a full 700 ms for slow paste consumers without blocking the next
    /// hotkey. A second conversion inherits the ORIGINAL snapshot, not the
    /// first conversion's temporary text. The generation and clipboard sequence
    /// prevent a stale timer from overwriting a later fix or a user's new copy.
    internal static class ClipboardRestore
    {
        private static readonly object Gate = new object();
        private static ClipboardSnapshot _pending;
        private static uint _sequence;
        private static int _generation;
        private static System.Threading.Timer _timer;
        private static StaWorker _worker;

        public static void Start(StaWorker worker)
        {
            lock (Gate) { _worker = worker; }
        }

        public static void Stop()
        {
            lock (Gate) { Cancel(); _worker = null; }
        }

        public static ClipboardSnapshot Take()
        {
            lock (Gate)
            {
                ClipboardSnapshot original = _pending;
                bool owned = original != null && Native.GetClipboardSequenceNumber() == _sequence;
                Cancel();
                if (owned) return original;
            }
            // OLE may call back into the UI. Never hold Gate across those
            // calls: shutdown takes the same gate on the UI thread.
            return ClipboardSnapshot.Take();
        }

        private static void Cancel()
        {
            _generation++;
            _pending = null;
            if (_timer != null) { _timer.Dispose(); _timer = null; }
        }

        public static void Schedule(ClipboardSnapshot snapshot, uint stagedSequence)
        {
            lock (Gate)
            {
                Cancel();
                // Never OpenClipboard after queuing Ctrl+V: even our read lock
                // can make an edit control's paste fail and erase its selection.
                // Ownership was captured before the paste; checking the sequence
                // does not acquire the clipboard or interfere with its consumer.
                if (Native.GetClipboardSequenceNumber() != stagedSequence) return;
                _pending = snapshot;
                _sequence = stagedSequence;
                int generation = _generation;
                _timer = new System.Threading.Timer(delegate(object ignored)
                {
                    lock (Gate)
                    {
                        if (generation != _generation || _worker == null) return;
                        // Use the same live STA for restoration as for copying.
                        // If a fix is active, this queues behind it and its new
                        // generation cancels the old restore before it can run.
                        _worker.Post(delegate
                        {
                            bool owned;
                            lock (Gate)
                            {
                                if (generation != _generation) return;
                                owned = Native.GetClipboardSequenceNumber() == _sequence;
                                Cancel();
                            }
                            // Fixes and restores share this STA, so another fix
                            // cannot interleave here. Release Gate before OLE.
                            if (owned) snapshot.Restore();
                        });
                    }
                }, null, 700, Timeout.Infinite);
            }
        }
    }
}
