// ---------------------------------------------------------------------------
//  Win32 interop and the low-level keyboard watcher.
//
//  Compiled with the csc.exe that ships with the .NET Framework, so the whole
//  project is C# 5: no string interpolation, no ?. operator, no nameof, no
//  expression-bodied members. Keep it that way or the build breaks on a clean
//  machine, which is the entire point of targeting that compiler.
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace KbFix
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    internal static class Native
    {
        public const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
        public const int VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_CAPITAL = 0x14;
        public const int VK_C = 0x43, VK_V = 0x56, VK_INSERT = 0x2D, VK_DELETE = 0x2E;
        public const int VK_PRIOR = 0x21, VK_NEXT = 0x22, VK_END = 0x23, VK_HOME = 0x24;
        public const int VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28;

        /// Navigation keys exist twice on a keyboard: on the dedicated cluster
        /// and on the numeric keypad. keybd_event with a zero scan code picks the
        /// KEYPAD one, and with Num Lock on that is a digit key -- so Shift+Home
        /// arrives as Shift+numpad7, which moves the caret WITHOUT extending the
        /// selection. Every one of these has to carry KEYEVENTF_EXTENDEDKEY to
        /// mean the key the user would have pressed.
        public static bool IsExtendedKey(int vk)
        {
            switch (vk)
            {
                case VK_INSERT:
                case VK_DELETE:
                case VK_PRIOR:
                case VK_NEXT:
                case VK_END:
                case VK_HOME:
                case VK_LEFT:
                case VK_UP:
                case VK_RIGHT:
                case VK_DOWN:
                    return true;
                default:
                    return false;
            }
        }

        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

        public const int MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004,
                         MOD_WIN = 0x0008, MOD_NOREPEAT = 0x4000;

        public const int WM_HOTKEY = 0x0312;
        public const int WM_INPUTLANGCHANGEREQUEST = 0x0050;
        public const int WM_CLOSE = 0x0010;

        /// Stamped into dwExtraInfo on every keystroke this program synthesises,
        /// so the watcher can ignore its own input. Injected keystrokes in
        /// general are NOT ignored: plenty of people drive the trigger from a
        /// remapper or a macro keyboard and those must keep working.
        public const uint SIGNATURE = 0x4B424658; // "KBFX"

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")]
        public static extern short GetKeyState(int nVirtKey);
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern IntPtr PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        public static extern uint GetKeyboardLayoutList(int nBuff, [Out] IntPtr[] lpList);
        [DllImport("user32.dll")]
        public static extern IntPtr GetKeyboardLayout(uint idThread);
        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);
        [DllImport("user32.dll")]
        public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")]
        public static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")]
        public static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max, uint removeMsg);

        public const uint PM_REMOVE = 0x0001;

        /// Removes every pending copy of one message and reports how many there
        /// were. The count matters: converting blocks the message loop, so a
        /// press the user made during it is sitting here rather than lost, and
        /// the caller decides whether it meant something.
        public static int DrainMessages(IntPtr hWnd, uint message)
        {
            int count = 0;
            MSG m;
            while (PeekMessage(out m, hWnd, message, message, PM_REMOVE)) count++;
            return count;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr process, uint flags,
                                                             StringBuilder name, ref int size);

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        /// The executable name of whatever window is in front, or "" if it
        /// cannot be read. QueryFullProcessImageName is used rather than
        /// System.Diagnostics.Process because it needs only the limited-query
        /// right, so it also works for processes this one may not fully open.
        public static string ForegroundProcessName()
        {
            IntPtr handle = IntPtr.Zero;
            try
            {
                uint pid;
                GetWindowThreadProcessId(GetForegroundWindow(), out pid);
                if (pid == 0) return "";
                handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (handle == IntPtr.Zero) return "";

                StringBuilder sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (!QueryFullProcessImageName(handle, 0, sb, ref size)) return "";
                string full = sb.ToString();
                int slash = full.LastIndexOf('\\');
                return slash >= 0 ? full.Substring(slash + 1) : full;
            }
            catch { return ""; }
            finally { if (handle != IntPtr.Zero) CloseHandle(handle); }
        }

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        public static string ForegroundWindowClass()
        {
            IntPtr h = GetForegroundWindow();
            if (h == IntPtr.Zero) return "";
            StringBuilder sb = new StringBuilder(256);
            int n = GetClassName(h, sb, sb.Capacity);
            return n > 0 ? sb.ToString() : "";
        }

        public static int ForegroundLangId()
        {
            IntPtr hkl = GetKeyboardLayout(GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero));
            return (int)(hkl.ToInt64() & 0xFFFF);
        }

        public static IntPtr[] InstalledLayouts()
        {
            IntPtr[] buf = new IntPtr[64];
            uint n = GetKeyboardLayoutList(buf.Length, buf);
            IntPtr[] result = new IntPtr[n];
            Array.Copy(buf, result, (int)n);
            return result;
        }

        public static bool IsDown(int vk)
        {
            return (GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        /// Caps Lock and friends report through the LOW bit. GetAsyncKeyState is
        /// documented as unreliable for the toggle bit, hence GetKeyState.
        public static int CapsLockState()
        {
            return GetKeyState(VK_CAPITAL) & 0x0001;
        }
    }

    /// Watches the keyboard for one key combination without consuming it, so
    /// whatever Windows already does with that combination keeps happening.
    ///
    /// The hook callback must return within a few hundred milliseconds or the
    /// system starts skipping it, so it only posts a message to the app's own
    /// window and lets the message loop do the real work. Posting to a window
    /// rather than the thread matters: WinForms' message pump reliably
    /// dispatches window messages, while thread messages can be swallowed.
    internal static class Watcher
    {
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        private const int WH_KEYBOARD_LL = 13;
        private const int HC_ACTION = 0;
        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_SYSKEYDOWN = 0x0104;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        /// One combination being watched. There is more than one: Win+Space and
        /// Caps Lock both belong to Windows and neither can be claimed outright
        /// without taking the key away from the user, so both are watched here
        /// and neither is ever swallowed.
        private sealed class Watched
        {
            public uint Message;
            public int Vk;
            public bool Ctrl, Alt, Shift, Win, Swallow;
        }

        private static IntPtr _hook = IntPtr.Zero;
        private static LowLevelKeyboardProc _proc;   // must stay rooted or the GC eats it
        private static IntPtr _targetWindow;
        private static readonly List<Watched> _watched = new List<Watched>();

        public static bool Installed { get { return _hook != IntPtr.Zero; } }

        private static bool ModifiersMatch(Watched w)
        {
            if (w.Ctrl != Native.IsDown(Native.VK_CONTROL)) return false;
            if (w.Alt != Native.IsDown(Native.VK_MENU)) return false;
            if (w.Shift != Native.IsDown(Native.VK_SHIFT)) return false;
            bool win = Native.IsDown(Native.VK_LWIN) || Native.IsDown(Native.VK_RWIN);
            if (w.Win != win) return false;
            return true;
        }

        private static Watched Find(uint vkCode)
        {
            for (int i = 0; i < _watched.Count; i++)
            {
                Watched w = _watched[i];
                if ((int)vkCode == w.Vk && ModifiersMatch(w)) return w;
            }
            return null;
        }

        private static IntPtr Proc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode == HC_ACTION)
            {
                uint msg = (uint)wParam.ToInt64();
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    KBDLLHOOKSTRUCT k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                    bool mine = ((uint)(k.dwExtraInfo.ToInt64() & 0xFFFFFFFF)) == Native.SIGNATURE;
                    Watched hit = mine ? null : Find(k.vkCode);
                    if (hit != null)
                    {
                        Native.PostMessage(_targetWindow, hit.Message, IntPtr.Zero, IntPtr.Zero);
                        if (hit.Swallow) return (IntPtr)1;
                    }
                }
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        /// Starts watching one combination, seating the hook if this is the
        /// first. Watching the same message again replaces the old combination,
        /// which is how a hotkey change takes effect without a restart.
        public static bool Watch(IntPtr targetWindow, uint message, int vk,
                                 bool ctrl, bool alt, bool shift, bool win, bool swallow)
        {
            _targetWindow = targetWindow;
            Forget(message);

            Watched w = new Watched();
            w.Message = message; w.Vk = vk;
            w.Ctrl = ctrl; w.Alt = alt; w.Shift = shift; w.Win = win; w.Swallow = swallow;
            _watched.Add(w);

            return _hook != IntPtr.Zero || Seat();
        }

        private static void Forget(uint message)
        {
            for (int i = 0; i < _watched.Count; i++)
                if (_watched[i].Message == message) { _watched.RemoveAt(i); return; }
        }

        /// Stops watching one combination, dropping the hook once none are left
        /// so the program costs nothing when every feature using it is off.
        public static void Unwatch(uint message)
        {
            Forget(message);
            if (_watched.Count == 0) Uninstall();
        }

        public static bool IsWatching(uint message)
        {
            for (int i = 0; i < _watched.Count; i++)
                if (_watched[i].Message == message) return true;
            return false;
        }

        private static bool Seat()
        {
            _proc = new LowLevelKeyboardProc(Proc);
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            return _hook != IntPtr.Zero;
        }

        public static void Uninstall()
        {
            if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        }

        /// Windows silently stops calling a hook that once took too long to
        /// return. Converting blocks the message loop for around a second, so
        /// the hook is re-seated after every conversion.
        public static bool Reinstall()
        {
            if (_hook == IntPtr.Zero) return false;
            Uninstall();
            return Seat();
        }
    }

    /// Asks Windows what each physical key produces under a given keyboard
    /// layout. This is what makes the tool work for any pair of layouts the
    /// machine has installed rather than one hard-coded language pair.
    internal static class LayoutProbe
    {
        private const uint MAPVK_VSC_TO_VK = 1;
        private const uint MAPVK_VSC_TO_VK_EX = 3;

        /// Bit 2 tells ToUnicodeEx not to disturb the keyboard state, which
        /// matters because this runs while the user is typing (Win10 1607+).
        private const uint TU_NOSTATECHANGE = 0x4;

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKeyEx(uint uCode, uint uMapType, IntPtr dwhkl);

        // CharSet.Unicode is not optional: ToUnicodeEx writes UTF-16, and the
        // default ANSI marshalling hands back mangled bytes for every non-Latin
        // character, silently reducing a Thai layout to the handful of keys that
        // happen to produce ASCII.
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState,
                                              StringBuilder pwszBuff, int cchBuff, uint wFlags, IntPtr dwhkl);

        /// The printable block of a standard keyboard, in physical scan-code
        /// order: the number row, the three letter rows, and the two keys that
        /// only exist on some physical layouts (0x2B and 0x56).
        public static int[] ScanCodes()
        {
            List<int> sc = new List<int>();
            sc.Add(0x29);                                   // ` key
            for (int i = 0x02; i <= 0x0D; i++) sc.Add(i);   // 1 .. =
            for (int i = 0x10; i <= 0x1B; i++) sc.Add(i);   // Q .. ]
            sc.Add(0x2B);                                   // backslash
            for (int i = 0x1E; i <= 0x28; i++) sc.Add(i);   // A .. '
            for (int i = 0x2C; i <= 0x35; i++) sc.Add(i);   // Z .. /
            sc.Add(0x56);                                   // ISO extra key
            return sc.ToArray();
        }

        /// Probes one layout. Caps Lock is a separate dimension because layouts
        /// disagree about what it does: a US layout applies it to letters only,
        /// a Thai one treats it as a second Shift on every key, number row
        /// included. Assuming either rule would corrupt the other.
        public static Dictionary<int, Dictionary<string, char>> Probe(IntPtr hkl)
        {
            Dictionary<int, Dictionary<string, char>> byCaps = new Dictionary<int, Dictionary<string, char>>();
            byCaps[0] = new Dictionary<string, char>();
            byCaps[1] = new Dictionary<string, char>();

            byte[] state = new byte[256];
            StringBuilder sb = new StringBuilder(8);

            foreach (int sc in ScanCodes())
            {
                uint vk = MapVirtualKeyEx((uint)sc, MAPVK_VSC_TO_VK_EX, hkl);
                if (vk == 0) vk = MapVirtualKeyEx((uint)sc, MAPVK_VSC_TO_VK, hkl);
                if (vk == 0) continue;

                for (int caps = 0; caps <= 1; caps++)
                {
                    for (int shift = 0; shift <= 1; shift++)
                    {
                        Array.Clear(state, 0, state.Length);
                        if (shift == 1) state[Native.VK_SHIFT] = 0x80;
                        if (caps == 1) state[Native.VK_CAPITAL] = 0x01;

                        // ToUnicodeEx can report a dead key (negative) or nothing
                        // on the first call for a layout it has not warmed up
                        // yet, so give each key a few attempts.
                        int r = 0;
                        for (int attempt = 0; attempt < 3; attempt++)
                        {
                            sb.Length = 0;
                            r = ToUnicodeEx(vk, (uint)sc, state, sb, sb.Capacity, TU_NOSTATECHANGE, hkl);
                            if (r == 1) break;
                        }
                        if (r != 1 || sb.Length != 1) continue;

                        char c = sb[0];
                        if (c < ' ' || c == '\u007F') continue;
                        byCaps[caps][sc.ToString() + "-" + shift.ToString()] = c;
                    }
                }
            }
            return byCaps;
        }
    }
}
