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
using System.Threading;

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

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    /// Only the keyboard member is ever filled in, but the union has to be the
    /// size of its LARGEST member or SendInput rejects the whole array with
    /// ERROR_INVALID_PARAMETER. MOUSEINPUT is that member, so it is declared
    /// here purely to get the layout right.
    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    internal static class Native
    {
        public const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
        public const int VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_CAPITAL = 0x14;
        /// An unassigned virtual key, sent purely so the window in front SEES a
        /// keystroke it cannot possibly act on.
        ///
        /// Windows arms a latch when Alt goes down, and DefWindowProc opens the
        /// window's menu bar -- or, in a window without one, its
        /// Restore/Move/Size/Close menu -- when Alt comes back up with that latch
        /// still armed. Any key pressed in between disarms it, which is why
        /// Alt+Tab never leaves a menu behind. This program's own hotkey removes
        /// that key: RegisterHotKey swallows the Space, and the Space was the
        /// "other key".
        ///
        /// 0x07 is documented as undefined. No layout maps a character to it, so
        /// it produces no WM_SYSCHAR and matches no menu mnemonic. Every real key
        /// is worse: Alt+letter picks a menu item, Alt+Space opens the system
        /// menu outright, and Alt+Shift is the input-language switch on a great
        /// many machines.
        public const int VK_UNASSIGNED = 0x07;

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
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")]
        public static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")]
        public static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max, uint removeMsg);

        public const uint PM_REMOVE = 0x0001;
        private const uint INPUT_KEYBOARD = 1;

        /// One key event, stamped so the watcher can tell it apart from a key the
        /// user actually pressed.
        public static INPUT KeyInput(int vk, bool down)
        {
            INPUT i = new INPUT();
            i.type = INPUT_KEYBOARD;
            i.u.ki.wVk = (ushort)vk;
            i.u.ki.wScan = 0;
            i.u.ki.dwFlags = down ? 0u : KEYEVENTF_KEYUP;
            if (IsExtendedKey(vk)) i.u.ki.dwFlags |= KEYEVENTF_EXTENDEDKEY;
            i.u.ki.time = 0;
            i.u.ki.dwExtraInfo = new UIntPtr(SIGNATURE);
            return i;
        }

        /// Sends a whole burst of key events as one unit.
        ///
        /// SendInput rather than keybd_event, and one call rather than a loop,
        /// because Windows guarantees the events in a single SendInput array are
        /// NOT interleaved with anything the user types meanwhile. A loop of
        /// keybd_event calls has no such promise: a person typing while the
        /// program was sending Shift+Right could land a character in the middle
        /// of the burst, which is how fast typing during a fix came out garbled.
        public static bool Send(INPUT[] inputs)
        {
            if (inputs == null || inputs.Length == 0) return true;
            uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
            return sent == (uint)inputs.Length;
        }

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

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

        /// Hands the pages this process is not using back to Windows.
        ///
        /// A tray program spends essentially all of its life asleep, and the
        /// working set it built up while starting -- WinForms, the layout probe,
        /// the spell checker, the icon -- is all still resident and all still
        /// counted against the machine. -1 for both bounds is the documented way
        /// to say "trim to whatever is genuinely in use"; anything needed again
        /// comes back as a soft fault from the standby list, which is memory
        /// that was never given away.
        ///
        /// Called at the two moments the program is about to be idle for a long
        /// time: once startup has finished, and after each fix.
        public static void TrimWorkingSet()
        {
            try { SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1)); }
            catch { }
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
        private const uint WM_KEYUP = 0x0101;
        private const uint WM_SYSKEYUP = 0x0105;

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

        /// Keys currently held, so auto-repeat is not mistaken for new typing.
        /// Touched only from the hook callback, which always runs on the thread
        /// that seated the hook.
        private static readonly Dictionary<uint, bool> _held = new Dictionary<uint, bool>();
        private static int _typed;

        public static bool Installed { get { return _hook != IntPtr.Zero; } }

        /// How many characters the user has typed since the program started.
        ///
        /// A fix takes the best part of a second of clipboard and keyboard round
        /// trips, and it is only safe for as long as the document underneath is
        /// standing still. Comparing this against the value it had when the
        /// trigger arrived is how the fixer notices that the user carried on
        /// typing and abandons the attempt instead of pasting over what they
        /// have since written.
        public static int TypedCount { get { return Thread.VolatileRead(ref _typed); } }

        /// Whether a key press counts as "the user is typing".
        ///
        /// Modifiers on their own do not, and neither does anything held with
        /// Ctrl, Alt or Win -- a chord is a command, and one of them is the very
        /// trigger that started this. Text is only ever typed without those.
        private static bool IsTyping(uint vk)
        {
            switch (vk)
            {
                case 0x10: case 0x11: case 0x12:          // Shift, Ctrl, Alt
                case 0xA0: case 0xA1:                      // L/R Shift
                case 0xA2: case 0xA3:                      // L/R Ctrl
                case 0xA4: case 0xA5:                      // L/R Alt
                case (uint)Native.VK_LWIN:
                case (uint)Native.VK_RWIN:
                case (uint)Native.VK_CAPITAL:
                    return false;
            }
            if (Native.IsDown(Native.VK_CONTROL)) return false;
            if (Native.IsDown(Native.VK_MENU)) return false;
            if (Native.IsDown(Native.VK_LWIN) || Native.IsDown(Native.VK_RWIN)) return false;
            return true;
        }

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

        // Byte offsets into the unmanaged KBDLLHOOKSTRUCT. Four DWORDs, then a
        // ULONG_PTR -- which lands at 16 on both 32- and 64-bit, since 16 is
        // already 8-aligned. Only these two fields are ever wanted, and reading
        // them directly is what keeps this callback allocation-free; see Proc.
        private const int OffsetVkCode = 0;
        private const int OffsetExtraInfo = 16;

        /// Runs for EVERY key event on the machine, on the message loop of the
        /// thread that seated the hook -- so every microsecond spent here is
        /// added to the latency of every keystroke the user makes anywhere in
        /// Windows, and going over LowLevelHooksTimeout (300 ms) makes Windows
        /// drop the hook silently.
        ///
        /// It therefore allocates nothing. The obvious
        /// Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT)) boxes the
        /// struct, which is a garbage allocation twice per keystroke -- around
        /// 15 kB a minute of ordinary typing, for a callback that wants two
        /// fields out of five. Marshal.ReadInt32 reads them in place.
        private static IntPtr Proc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode == HC_ACTION)
            {
                uint msg = (uint)wParam.ToInt64();
                bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                if (down || msg == WM_KEYUP || msg == WM_SYSKEYUP)
                {
                    uint vk = (uint)Marshal.ReadInt32(lParam, OffsetVkCode);
                    if (!down)
                    {
                        _held.Remove(vk);
                    }
                    else if ((uint)Marshal.ReadInt32(lParam, OffsetExtraInfo) != Native.SIGNATURE)
                    {
                        if (!_held.ContainsKey(vk))
                        {
                            _held[vk] = true;
                            if (IsTyping(vk)) Interlocked.Increment(ref _typed);
                        }
                        Watched hit = Find(vk);
                        if (hit != null)
                        {
                            // The count travels with the press. Reading it later,
                            // in the fixer, would already include whatever the
                            // user typed in the meantime -- which is the one
                            // thing it exists to detect.
                            Native.PostMessage(_targetWindow, hit.Message,
                                               new IntPtr(Thread.VolatileRead(ref _typed)), Native.GetForegroundWindow());
                            if (hit.Swallow) return (IntPtr)1;
                        }
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
            // Any key released while the hook was down was never seen coming
            // back up, and a key stuck in this list is one that would never
            // count as typing again.
            _held.Clear();
            _proc = new LowLevelKeyboardProc(Proc);
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            return _hook != IntPtr.Zero;
        }

        public static void Uninstall()
        {
            if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
            _held.Clear();
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
