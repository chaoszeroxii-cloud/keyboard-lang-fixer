// ---------------------------------------------------------------------------
//  Reading the selection by asking the window for it, instead of sending
//  Ctrl+C and waiting to see what lands on the clipboard.
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace KbFix
{
    /// A copy probe costs a keystroke round trip, a clipboard write the user
    /// never asked for, and -- when nothing is selected -- a timeout, because
    /// "nothing arrived" can only be learned by waiting. Asking the control
    /// answers in well under a millisecond for a Win32 edit and a few for a
    /// browser, and "nothing selected" is an answer rather than a timeout.
    ///
    /// Anything that cannot be answered with certainty returns false, and the
    /// caller falls back to the copy probe. A wrong answer here is worse than
    /// a slow one: it would paste over the wrong text.
    internal static class DirectSelection
    {
        /// Longest selection read this way. A copy handles anything longer.
        public const int MaxChars = 20000;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct GUITHREADINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
            public RECT rcCaret;
        }

        [DllImport("user32.dll")]
        private static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
                                                        uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
        private static extern IntPtr SendMessageTimeoutText(IntPtr hWnd, uint msg, IntPtr wParam, StringBuilder lParam,
                                                            uint flags, uint timeout, out IntPtr result);
        [DllImport("kernel32.dll")]
        private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        private const uint WM_GETTEXT = 0x000D, WM_GETTEXTLENGTH = 0x000E, EM_GETSEL = 0x00B0;
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        private const uint MessageTimeoutMs = 100;
        private const int GWL_STYLE = -16, ES_PASSWORD = 0x0020;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        /// Browsers answer UI Automation for their text fields. Everything else
        /// Chromium-based (VS Code, Discord, Slack...) is left out on purpose:
        /// Electron apps switch into screen-reader mode the moment a UI
        /// Automation client asks them anything, and VS Code visibly changes
        /// how its editor behaves in that mode.
        private static readonly string[] UiaProcesses = new string[] {
            "chrome.exe", "msedge.exe", "brave.exe", "vivaldi.exe"
        };

        /// How the selection was read, for the log; null when it was not.
        public static string Via;

        /// True when $text is a trustworthy answer: the exact selected text,
        /// or "" for nothing selected.
        public static bool TryRead(IntPtr targetWindow, bool allowUia, out string text)
        {
            text = null;
            Via = null;
            if (targetWindow == IntPtr.Zero) return false;
            uint pid;
            uint thread = GetWindowThreadProcessId(targetWindow, out pid);
            if (thread == 0) return false;

            GUITHREADINFO info = new GUITHREADINFO();
            info.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
            IntPtr focus = GetGUIThreadInfo(thread, ref info) ? info.hwndFocus : IntPtr.Zero;
            if (focus != IntPtr.Zero)
            {
                string cls = ClassOf(focus);
                bool rich = cls.IndexOf("RichEdit", StringComparison.OrdinalIgnoreCase) >= 0;
                bool edit = !rich && (string.Equals(cls, "Edit", StringComparison.OrdinalIgnoreCase) ||
                                      cls.IndexOf(".EDIT.", StringComparison.OrdinalIgnoreCase) >= 0);
                if (edit || rich) return ReadEdit(focus, rich, out text);
            }

            if (allowUia && IsUiaProcess(pid))
            {
                try { return UiaSelection.TryRead(pid, out text); }
                catch { text = null; return false; }
            }
            return false;
        }

        private static string ClassOf(IntPtr hwnd)
        {
            StringBuilder sb = new StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        /// EM_GETSEL and WM_GETTEXT are marshalled across processes by Windows
        /// itself for the system edit classes, so both work on another
        /// program's control.
        private static bool ReadEdit(IntPtr hwnd, bool rich, out string text)
        {
            text = null;
            // A password box answers WM_GETTEXT with nothing from outside its
            // process; the copy probe cannot read one either, so leave it.
            if ((GetWindowLong(hwnd, GWL_STYLE) & ES_PASSWORD) != 0) return false;

            IntPtr r;
            if (SendMessageTimeout(hwnd, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero,
                                   SMTO_ABORTIFHUNG, MessageTimeoutMs, out r) == IntPtr.Zero) return false;
            int length = r.ToInt32();
            // EM_GETSEL packs both ends into 16 bits each when called this way.
            if (length < 0 || length > 0xFFFE) return false;

            if (SendMessageTimeout(hwnd, EM_GETSEL, IntPtr.Zero, IntPtr.Zero,
                                   SMTO_ABORTIFHUNG, MessageTimeoutMs, out r) == IntPtr.Zero) return false;
            long packed = r.ToInt64() & 0xFFFFFFFFL;
            int start = (int)(packed & 0xFFFF), end = (int)((packed >> 16) & 0xFFFF);
            if (end < start) { int t = start; start = end; end = t; }
            if (start == end)
            {
                text = "";
                Via = rich ? "EM_GETSEL (rich edit)" : "EM_GETSEL";
                return true;
            }
            if (end - start > MaxChars) return false;

            StringBuilder sb = new StringBuilder(length + 2);
            if (SendMessageTimeoutText(hwnd, WM_GETTEXT, new IntPtr(length + 1), sb,
                                       SMTO_ABORTIFHUNG, MessageTimeoutMs, out r) == IntPtr.Zero) return false;
            string all = sb.ToString();
            if (end > all.Length) return false;

            // A rich edit counts a paragraph break as ONE position while
            // WM_GETTEXT hands it back as CR LF, so every offset after a line
            // break is off by one per line. Only a single-line prefix is safe.
            if (rich && all.IndexOfAny(new char[] { (char)13, (char)10 }, 0, end) >= 0) return false;

            text = all.Substring(start, end - start);
            Via = rich ? "EM_GETSEL (rich edit)" : "EM_GETSEL";
            return true;
        }

        private static readonly Dictionary<uint, bool> _uiaByPid = new Dictionary<uint, bool>();

        private static bool IsUiaProcess(uint pid)
        {
            bool known;
            lock (_uiaByPid)
                if (_uiaByPid.TryGetValue(pid, out known)) return known;

            bool yes = false;
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h != IntPtr.Zero)
            {
                try
                {
                    StringBuilder sb = new StringBuilder(1024);
                    int size = sb.Capacity;
                    if (QueryFullProcessImageName(h, 0, sb, ref size))
                    {
                        string exe = System.IO.Path.GetFileName(sb.ToString());
                        foreach (string name in UiaProcesses)
                            if (string.Equals(exe, name, StringComparison.OrdinalIgnoreCase)) { yes = true; break; }
                    }
                }
                finally { CloseHandle(h); }
            }
            lock (_uiaByPid)
            {
                if (_uiaByPid.Count > 256) _uiaByPid.Clear();
                _uiaByPid[pid] = yes;
            }
            return yes;
        }
    }
}
