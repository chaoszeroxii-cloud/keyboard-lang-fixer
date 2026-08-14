<#
================================================================================
  Keyboard Language Fixer  --  fixes text typed with the wrong keyboard layout
================================================================================
  Bound to Win+Space, the same key you already use to change input language:

      nothing selected  ->  Windows switches the language, exactly as before.
      text selected     ->  Windows switches the language AND every selected
                            character is re-mapped by its physical key position.

      "l;ylfu"  ->  "สวัสดี"        (Thai typed while EN layout was active)
      "้ำสสน"   ->  "hello"         (English typed while TH layout was active)

  The mapping is not hard-coded to Thai. At startup the script asks Windows what
  each physical key produces under every keyboard layout installed for this user
  (ToUnicodeEx + MapVirtualKeyEx), so any pair of installed layouts works --
  Russian, German, French, Hebrew, whatever is set up. The Thai Kedmanee table
  further down is only a fallback for machines with a single layout installed,
  and the oracle the self-test checks the probed layout against.

  Win+Space is owned by the Windows shell, so it cannot be taken over with
  RegisterHotKey. Instead a low-level keyboard hook *watches* for it without
  swallowing it -- the native language switch still happens, this tool just
  does the extra work on top. Any non-Win hotkey uses RegisterHotKey instead.

  Usage:
      powershell -ExecutionPolicy Bypass -File KeyboardLangFixer.ps1
      powershell -ExecutionPolicy Bypass -File KeyboardLangFixer.ps1 -Hotkey "Ctrl+Alt+X"
      powershell -ExecutionPolicy Bypass -File KeyboardLangFixer.ps1 -ListLayouts
      powershell -ExecutionPolicy Bypass -File KeyboardLangFixer.ps1 -SelfTest

  Apart from a few comments, this file is ASCII: the fallback table writes every
  Thai character as a Unicode code point, so the mapping data cannot be
  corrupted by a bad file encoding.
================================================================================
#>

[CmdletBinding()]
param(
    # Modifiers: Ctrl / Alt / Shift / Win. Key names come from
    # System.Windows.Forms.Keys  (Space, X, Q, F9, Pause, ...)
    [string]$Hotkey = 'Win+Space',

    # Hotkey that quits the tool. Only used in RegisterHotKey mode; the tray
    # icon's Exit item always works.
    [string]$QuitHotkey = 'Ctrl+Alt+Shift+X',

    # Auto   - Win+<key> watches via hook (native behaviour kept), else RegisterHotKey
    # Hook   - always watch via a low-level keyboard hook
    # Hotkey - always claim the combo exclusively with RegisterHotKey
    [ValidateSet('Auto', 'Hook', 'Hotkey')][string]$Mode = 'Auto',

    # Do NOT touch the Windows input language after converting.
    [switch]$NoLangSwitch,

    # Do NOT show a system-tray icon.
    [switch]$NoTrayIcon,

    # Run the built-in conversion tests and exit.
    [switch]$SelfTest,

    # Print the keyboard layouts this machine can convert between, and exit.
    [switch]$ListLayouts,

    # Open the hotkey picker, save the choice, restart a running copy, and exit.
    [switch]$ConfigureHotkey,

    # Append a line per trigger to this file, for troubleshooting.
    [string]$LogPath,

    # Internal: set when the script re-launches itself in STA mode.
    [switch]$Relaunched
)

$ErrorActionPreference = 'Stop'

# ------------------------------------------------------------------------------
# Clipboard access requires a single-threaded apartment. Re-launch if needed.
# ------------------------------------------------------------------------------
if (-not $SelfTest -and -not $ListLayouts -and -not $ConfigureHotkey -and -not $Relaunched -and
    [Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    $argList = @(
        '-NoProfile', '-STA', '-ExecutionPolicy', 'Bypass',
        '-File', "`"$PSCommandPath`"",
        '-Hotkey', "`"$Hotkey`"", '-QuitHotkey', "`"$QuitHotkey`"",
        '-Mode', $Mode, '-Relaunched'
    )
    if ($NoLangSwitch) { $argList += '-NoLangSwitch' }
    if ($NoTrayIcon)   { $argList += '-NoTrayIcon' }
    if ($LogPath)      { $argList += @('-LogPath', "`"$LogPath`"") }
    Start-Process -FilePath 'powershell.exe' -ArgumentList $argList -WindowStyle Hidden
    return
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# ==============================================================================
#  Layout map  --  US QWERTY key  ->  Thai Kedmanee character
# ==============================================================================
$EnRows = @(
    '`1234567890-='   # number row, unshifted
    'qwertyuiop[]\'   # top row,    unshifted
    "asdfghjkl;'"     # home row,   unshifted
    'zxcvbnm,./'      # bottom row, unshifted
    '~!@#$%^&*()_+'   # number row, shifted
    'QWERTYUIOP{}|'   # top row,    shifted
    'ASDFGHJKL:"'     # home row,   shifted
    'ZXCVBNM<>?'      # bottom row, shifted
)

# Each row is one space-separated hex code point per key, aligned with $EnRows.
$ThRows = @(
    #  `    1    2    3    4    5    6    7    8    9    0    -    =
    '005F 0E45 002F 002D 0E20 0E16 0E38 0E36 0E04 0E15 0E08 0E02 0E0A'
    #  q    w    e    r    t    y    u    i    o    p    [    ]    \
    '0E46 0E44 0E33 0E1E 0E30 0E31 0E35 0E23 0E19 0E22 0E1A 0E25 0E03'
    #  a    s    d    f    g    h    j    k    l    ;    '
    '0E1F 0E2B 0E01 0E14 0E40 0E49 0E48 0E32 0E2A 0E27 0E07'
    #  z    x    c    v    b    n    m    ,    .    /
    '0E1C 0E1B 0E41 0E2D 0E34 0E37 0E17 0E21 0E43 0E1D'
    #  ~    !    @    #    $    %    ^    &    *    (    )    _    +
    '0025 002B 0E51 0E52 0E53 0E54 0E39 0E3F 0E55 0E56 0E57 0E58 0E59'
    #  Q    W    E    R    T    Y    U    I    O    P    {    }    |
    '0E50 0022 0E0E 0E11 0E18 0E4D 0E4A 0E13 0E2F 0E0D 0E10 002C 0E05'
    #  A    S    D    F    G    H    J    K    L    :    "
    '0E24 0E06 0E0F 0E42 0E0C 0E47 0E4B 0E29 0E28 0E0B 002E'
    #  Z    X    C    V    B    N    M    <    >    ?
    '0028 0029 0E09 0E2E 0E3A 0E4C 003F 0E12 0E2C 0E26'
)

# ==============================================================================
#  Layouts
# ==============================================================================
<#
  A layout is just "which character does each physical key produce", in both
  directions:

      Forward   "<key position>" -> character
      Reverse   character        -> "<key position>"

  Converting between two layouts is then: look the character up in the source
  layout's Reverse to get the key it sits on, and ask the target layout what
  that same key produces. Nothing in that is Thai-specific, which is why any
  pair of installed layouts works.
#>
function New-Layout {
    param(
        [Parameter(Mandatory)][string]$Name,
        # Forward tables keyed by Caps Lock state: @{ 0 = @{pos=char}; 1 = @{pos=char} }
        [Parameter(Mandatory)][hashtable]$ForwardByCaps,
        [int]$LangId = 0,
        [IntPtr]$Hkl = [IntPtr]::Zero,
        [switch]$BuiltIn
    )
    $maps = @{}
    foreach ($caps in 0, 1) {
        $forward = $ForwardByCaps[$caps]
        $reverse = @{}
        foreach ($pos in $forward.Keys) {
            $ch = $forward[$pos]
            # A character can sit on more than one key in some layouts; the
            # first one wins so the mapping stays a function.
            if (-not $reverse.ContainsKey($ch)) { $reverse[$ch] = $pos }
        }
        $maps[$caps] = [pscustomobject]@{ Forward = $forward; Reverse = $reverse }
    }
    return [pscustomobject]@{
        Name     = $Name
        LangId   = $LangId
        Hkl      = $Hkl
        Maps     = $maps          # Maps[capsLockState].Forward / .Reverse
        BuiltIn  = [bool]$BuiltIn
        KeyCount = $maps[0].Forward.Count
    }
}

# The shipped Thai table, expressed as two layouts so it goes through exactly
# the same conversion code as the ones probed from Windows. Used when the
# machine has fewer than two usable layouts installed, and as the oracle the
# self-test checks the probed Thai layout against.
function Get-BuiltInLayouts {
    # Rows 0-3 are the unshifted keys, rows 4-7 the shifted ones, in the same
    # key order, so row r and row r+4 are the two halves of the same key.
    $enOff = @{}; $thOff = @{}
    $enOn  = @{}; $thOn  = @{}
    $half = [int]($EnRows.Count / 2)

    for ($r = 0; $r -lt $EnRows.Count; $r++) {
        $en = $EnRows[$r]
        $th = @($ThRows[$r].Split(' ') | ForEach-Object { [Convert]::ToInt32($_, 16) })
        if ($en.Length -ne $th.Count) {
            throw "Layout row $r is misaligned: $($en.Length) EN keys vs $($th.Count) TH chars."
        }
        for ($i = 0; $i -lt $en.Length; $i++) {
            # "<key identity>-<was shift held>", so the unshifted row and the
            # shifted row of the same key share a key identity.
            $pos = "b$($r % $half)_$i-$([int]($r -ge $half))"
            $enOff[$pos] = $en[$i]
            $thOff[$pos] = [char]$th[$i]
        }
    }

    # Caps Lock, as Windows actually implements these two layouts (verified
    # against the live layouts by -SelfTest):
    #   US   - inverts the case of letters, leaves every other key alone.
    #   Thai - acts as a second Shift on every key, number row included.
    foreach ($pos in $enOff.Keys) {
        $c = $enOff[$pos]
        $enOn[$pos] = if ([char]::IsLetter($c)) {
            if ([char]::IsUpper($c)) { [char]::ToLower($c) } else { [char]::ToUpper($c) }
        } else { $c }
    }
    foreach ($pos in $thOff.Keys) {
        # Same key, opposite shift half.
        $flipped = $pos -replace '-(\d)$', ''
        $shift = [int]($pos -replace '^.*-', '')
        $thOn[$pos] = $thOff["$flipped-$([int](-not [bool]$shift))"]
    }

    return @(
        (New-Layout -Name 'English (US, built-in table)' -LangId 0x0409 -BuiltIn `
            -ForwardByCaps @{ 0 = $enOff; 1 = $enOn })
        (New-Layout -Name 'Thai (Kedmanee, built-in table)' -LangId 0x041E -BuiltIn `
            -ForwardByCaps @{ 0 = $thOff; 1 = $thOn })
    )
}

function Get-LayoutDisplayName([int]$langId) {
    try { return [System.Globalization.CultureInfo]::GetCultureInfo($langId).DisplayName }
    catch { return ('Layout 0x{0:X4}' -f $langId) }
}

# Asks Windows about every keyboard layout installed for this user.
function Get-InstalledLayouts {
    $buf = New-Object 'IntPtr[]' 64
    $n = [KbFix.Native]::GetKeyboardLayoutList(64, $buf)
    $layouts = @()
    $seen = @{}
    for ($i = 0; $i -lt $n; $i++) {
        $hkl = $buf[$i]
        $langId = [int](([int64]$hkl) -band 0xFFFF)
        if ($seen.ContainsKey($langId)) { continue }     # same language, different IME
        $seen[$langId] = $true

        $quads = [KbFix.Layouts]::Probe($hkl)
        if ($quads.Count -lt 4) { continue }

        $byCaps = @{ 0 = @{}; 1 = @{} }
        for ($t = 0; $t -lt $quads.Count; $t += 4) {
            $sc = $quads[$t]; $shift = $quads[$t + 1]; $caps = $quads[$t + 2]
            $byCaps[$caps]["$sc-$shift"] = [char]$quads[$t + 3]
        }
        $layouts += (New-Layout -Name (Get-LayoutDisplayName $langId) -LangId $langId -Hkl $hkl -ForwardByCaps $byCaps)
    }
    return $layouts
}

# ==============================================================================
#  Conversion
# ==============================================================================

<#
  Works out which layout the text was actually typed on.

  Characters shared by every layout (digits, most punctuation) say nothing, so
  a layout is scored on the characters only it can produce. Text typed on the
  wrong layout is full of those: Latin letters exist only in the Latin layout,
  Thai letters only in the Thai one.
#>
function Select-SourceLayout([string]$text, $layouts, [int]$caps = 0) {
    if ($layouts.Count -lt 2) { return $null }

    $chars = @($text.ToCharArray() | Where-Object { -not [char]::IsWhiteSpace($_) })
    if ($chars.Count -eq 0) { return $null }

    $best = $null; $bestDistinct = 0; $bestTotal = 0
    foreach ($layout in $layouts) {
        $others = @($layouts | Where-Object { $_ -ne $layout })
        $mine = $layout.Maps[$caps].Reverse
        $distinct = 0; $total = 0
        foreach ($c in $chars) {
            if (-not $mine.ContainsKey($c)) { continue }
            $total++
            $elsewhere = $false
            foreach ($o in $others) { if ($o.Maps[$caps].Reverse.ContainsKey($c)) { $elsewhere = $true; break } }
            if (-not $elsewhere) { $distinct++ }
        }
        if ($distinct -gt $bestDistinct -or ($distinct -eq $bestDistinct -and $total -gt $bestTotal)) {
            $best = $layout; $bestDistinct = $distinct; $bestTotal = $total
        }
    }
    # Nothing in the selection is specific to any one layout (all digits, say):
    # converting would be a guess, so decline instead.
    if ($bestDistinct -eq 0) { return $null }
    return $best
}

<#
  Picks what to convert into. With the usual two layouts installed there is only
  one answer. With more, the language Windows just switched to is the best signal
  of what the user meant.
#>
function Select-TargetLayout($source, $layouts) {
    $candidates = @($layouts | Where-Object { $_ -ne $source })
    if ($candidates.Count -eq 0) { return $null }
    if ($candidates.Count -eq 1) { return $candidates[0] }

    $activeHkl = [KbFix.Native]::GetKeyboardLayout(
        [KbFix.Native]::GetWindowThreadProcessId([KbFix.Native]::GetForegroundWindow(), [IntPtr]::Zero))
    $activeLang = [int](([int64]$activeHkl) -band 0xFFFF)
    $active = $candidates | Where-Object { $_.LangId -eq $activeLang } | Select-Object -First 1
    if ($active) { return $active }
    return $candidates[0]
}

<#
  $Caps is the Caps Lock state the text was typed under. It has to be part of
  the lookup, not applied afterwards, because layouts implement Caps Lock
  differently: with it on, a US layout still gives "1" on the number row while a
  Thai one gives the shifted character. Reading the character back through the
  same Caps Lock state it was typed with recovers the physical key and whether
  Shift was actually held, which is the only thing worth carrying across.
#>
function Convert-LayoutText {
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Text,
        [Parameter(Mandatory)]$From,
        [Parameter(Mandatory)]$To,
        [ValidateSet(0, 1)][int]$Caps = 0
    )
    $reverse = $From.Maps[$Caps].Reverse
    $forward = $To.Maps[$Caps].Forward

    $sb = New-Object System.Text.StringBuilder $Text.Length
    foreach ($c in $Text.ToCharArray()) {
        $pos = $reverse[$c]
        if ($null -ne $pos -and $forward.ContainsKey($pos)) { [void]$sb.Append($forward[$pos]) }
        else { [void]$sb.Append($c) }        # spaces, newlines, emoji, keys the target lacks
    }
    return [pscustomobject]@{
        Text = $sb.ToString()
        From = $From.Name
        To   = $To.Name
        Caps = $Caps
    }
}

# ==============================================================================
#  Win32 interop
# ==============================================================================
if (-not ('KbFix.Native' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace KbFix {
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG {
        public IntPtr hwnd;
        public uint   message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint   time;
        public int    ptX;
        public int    ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT {
        public uint   vkCode;
        public uint   scanCode;
        public uint   flags;
        public uint   time;
        public IntPtr dwExtraInfo;
    }

    public static class Native {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")]
        public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);
        [DllImport("user32.dll")]
        public static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max, uint removeMsg);
        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref MSG lpMsg);
        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessage(ref MSG lpMsg);
        [DllImport("user32.dll")]
        public static extern void PostQuitMessage(int nExitCode);
        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")]
        public static extern short GetKeyState(int nVirtKey);
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
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
        [DllImport("user32.dll")]
        public static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        public static string ForegroundWindowClass() {
            IntPtr h = GetForegroundWindow();
            if (h == IntPtr.Zero) return "";
            StringBuilder sb = new StringBuilder(256);
            int n = GetClassName(h, sb, sb.Capacity);
            return n > 0 ? sb.ToString() : "";
        }
    }

    /// Asks Windows what each physical key produces under a given keyboard
    /// layout. This is what makes the tool work for any pair of layouts the
    /// machine has installed, instead of only the Thai table it ships with.
    public static class Layouts {
        const uint MAPVK_VK_TO_VSC    = 0;
        const uint MAPVK_VSC_TO_VK    = 1;
        const uint MAPVK_VSC_TO_VK_EX = 3;
        const int  VK_SHIFT   = 0x10;
        const int  VK_CAPITAL = 0x14;

        /// Bit 2 tells ToUnicodeEx not to disturb the keyboard state, which
        /// matters because this runs while the user is typing (Win10 1607+).
        const uint TU_NOSTATECHANGE = 0x4;

        [DllImport("user32.dll")]
        static extern uint MapVirtualKeyEx(uint uCode, uint uMapType, IntPtr dwhkl);
        // CharSet.Unicode is not optional: ToUnicodeEx writes UTF-16, and the
        // default ANSI marshalling would hand back mangled bytes for every
        // non-Latin character -- silently reducing a Thai layout to the handful
        // of keys that happen to produce ASCII.
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState,
                                      StringBuilder pwszBuff, int cchBuff, uint wFlags, IntPtr dwhkl);

        /// The printable block of a standard keyboard, in physical scan-code
        /// order: the number row, the three letter rows, and the two keys that
        /// only exist on some physical layouts (0x2B and 0x56).
        public static int[] ScanCodes() {
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

        /// Returns flat quadruples (scanCode, shift, capsLock, codePoint).
        ///
        /// Caps Lock is probed as its own dimension because layouts disagree
        /// about what it does: a US layout applies it to letters only, while a
        /// Thai one treats it as a shift toggle on every key, number row
        /// included. Assuming either rule would corrupt the other.
        public static int[] Probe(IntPtr hkl) {
            List<int> result = new List<int>();
            byte[] state = new byte[256];
            StringBuilder sb = new StringBuilder(8);

            foreach (int sc in ScanCodes()) {
                uint vk = MapVirtualKeyEx((uint)sc, MAPVK_VSC_TO_VK_EX, hkl);
                if (vk == 0) vk = MapVirtualKeyEx((uint)sc, MAPVK_VSC_TO_VK, hkl);
                if (vk == 0) continue;

                for (int caps = 0; caps <= 1; caps++) {
                for (int shift = 0; shift <= 1; shift++) {
                    Array.Clear(state, 0, state.Length);
                    if (shift == 1) state[VK_SHIFT] = 0x80;
                    // Toggle keys report through the LOW bit, not the high one.
                    if (caps == 1) state[VK_CAPITAL] = 0x01;

                    // ToUnicodeEx can report a dead key (negative) or nothing on
                    // the first call for a layout it has not warmed up yet, so
                    // give each key a few attempts before giving up on it.
                    int r = 0;
                    for (int attempt = 0; attempt < 3; attempt++) {
                        sb.Length = 0;
                        r = ToUnicodeEx(vk, (uint)sc, state, sb, sb.Capacity, TU_NOSTATECHANGE, hkl);
                        if (r == 1) break;
                    }
                    if (r != 1 || sb.Length != 1) continue;

                    char c = sb[0];
                    if (c < ' ' || c == '\u007F') continue;
                    result.Add(sc); result.Add(shift); result.Add(caps); result.Add((int)c);
                }
                }
            }
            return result.ToArray();
        }
    }

    /// Watches the keyboard for one key combination without consuming it, so
    /// whatever Windows already does with that combination keeps happening.
    /// The hook callback must return within a few hundred ms or the system
    /// starts skipping it, so it only posts a message and lets the message
    /// loop do the real work.
    public static class Watcher {
        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        public const int  WH_KEYBOARD_LL   = 13;
        public const uint WM_TRIGGER       = 0x0400 + 77;   // WM_USER + 77
        const int  HC_ACTION        = 0;
        const uint WM_KEYDOWN       = 0x0100;
        const uint WM_SYSKEYDOWN    = 0x0104;
        const int  VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12,
                   VK_LWIN  = 0x5B, VK_RWIN    = 0x5C;

        /// Stamped into dwExtraInfo on every keystroke this tool synthesises, so
        /// the hook can skip its own input. Injected keystrokes in general are
        /// NOT skipped -- plenty of people drive the trigger combo from a
        /// remapper or a macro keyboard, and those should still work.
        public const uint SIGNATURE = 0x4B424658;   // "KBFX"

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")]
        static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandle(string lpModuleName);
        [DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")]
        static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

        static IntPtr _hook = IntPtr.Zero;
        static LowLevelKeyboardProc _proc;   // must stay rooted or the GC eats it
        static uint _threadId;
        static int  _vk;
        static bool _ctrl, _alt, _shift, _win, _swallow;

        static bool Down(int vk) { return (Native.GetAsyncKeyState(vk) & 0x8000) != 0; }

        static bool ModifiersMatch() {
            if (_ctrl  != Down(VK_CONTROL))              return false;
            if (_alt   != Down(VK_MENU))                 return false;
            if (_shift != Down(VK_SHIFT))                return false;
            if (_win   != (Down(VK_LWIN) || Down(VK_RWIN))) return false;
            return true;
        }

        static IntPtr Proc(int nCode, IntPtr wParam, IntPtr lParam) {
            if (nCode == HC_ACTION) {
                uint msg = (uint)wParam.ToInt64();
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) {
                    KBDLLHOOKSTRUCT k =
                        (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                    bool mine = ((uint)(k.dwExtraInfo.ToInt64() & 0xFFFFFFFF)) == SIGNATURE;
                    if (!mine && (int)k.vkCode == _vk && ModifiersMatch()) {
                        PostThreadMessage(_threadId, WM_TRIGGER, IntPtr.Zero, IntPtr.Zero);
                        if (_swallow) return (IntPtr)1;
                    }
                }
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        public static bool Install(int vk, bool ctrl, bool alt, bool shift, bool win, bool swallow) {
            _vk = vk; _ctrl = ctrl; _alt = alt; _shift = shift; _win = win; _swallow = swallow;
            _threadId = GetCurrentThreadId();
            _proc = new LowLevelKeyboardProc(Proc);
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            return _hook != IntPtr.Zero;
        }

        public static void Uninstall() {
            if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        }

        /// Windows silently stops calling a hook that once took too long to
        /// return. Converting blocks the message loop for about a second, so
        /// the hook is re-seated after every conversion.
        public static bool Reinstall() {
            Uninstall();
            _proc = new LowLevelKeyboardProc(Proc);
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            return _hook != IntPtr.Zero;
        }
    }
}
'@
}

# ==============================================================================
#  Self test
# ==============================================================================
if ($SelfTest) {
    # sawatdi   = ส ว ั ส ด ี      typed on an EN-active keyboard as  l;ylfu
    # khopkhun  = ข อ บ ค ุ ณ      typed on an EN-active keyboard as  -v[86I
    # "hello" typed on a TH-active keyboard comes out as  ้ ำ ส ส น
    $TH_sawatdi  = [string]::Join('', (0x0E2A,0x0E27,0x0E31,0x0E2A,0x0E14,0x0E35 | ForEach-Object { [char]$_ }))
    $TH_khopkhun = [string]::Join('', (0x0E02,0x0E2D,0x0E1A,0x0E04,0x0E38,0x0E13 | ForEach-Object { [char]$_ }))
    # NOTE: PowerShell variable names are case-insensitive, so these two cannot
    # be called $TH_hello and $TH_Hello -- the second would silently be the first.
    $TH_lowerHello = [string]::Join('', (0x0E49,0x0E33,0x0E2A,0x0E2A,0x0E19     | ForEach-Object { [char]$_ }))

    # "Hello" typed on a TH-active keyboard: Shift+H gives U+0E47 where plain h
    # gives U+0E49, so the capital has to survive the round trip.
    $TH_upperHello = [string]::Join('', (0x0E47,0x0E33,0x0E2A,0x0E2A,0x0E19 | ForEach-Object { [char]$_ }))

    $fail = 0
    $builtIn = Get-BuiltInLayouts
    $bEn = $builtIn | Where-Object { $_.LangId -eq 0x0409 }
    $bTh = $builtIn | Where-Object { $_.LangId -eq 0x041E }

    $cases = @(
        @{ Name = 'EN-mode typing -> Thai';       In = 'l;ylfu';       Expect = $TH_sawatdi;  From = $bEn; To = $bTh }
        @{ Name = 'EN-mode typing -> Thai (2)';   In = '-v[86I';       Expect = $TH_khopkhun; From = $bEn; To = $bTh }
        @{ Name = 'TH-mode typing -> English';    In = $TH_lowerHello; Expect = 'hello';      From = $bTh; To = $bEn }
        @{ Name = 'reverse of case 1';            In = $TH_sawatdi;    Expect = 'l;ylfu';     From = $bTh; To = $bEn }
        @{ Name = 'capital letter (Shift+H)';     In = $TH_upperHello; Expect = 'Hello';      From = $bTh; To = $bEn }

        # Caps Lock on: "HELLO" came from unshifted key presses, and the Thai
        # layout treats Caps Lock as a second Shift, so it maps to the shifted
        # Thai characters.
        @{ Name = 'CapsLock: letters';             In = 'HELLO'; Caps = 1; From = $bEn; To = $bTh
           Expect = [string]::Join('', (0x0E47,0x0E0E,0x0E28,0x0E28,0x0E2F | ForEach-Object { [char]$_ })) }
        # The number row is where the two layouts disagree: US ignores Caps Lock
        # there, Thai does not. This is the case the caps-blind version got wrong.
        @{ Name = 'CapsLock: number row';          In = '1';     Caps = 1; From = $bEn; To = $bTh
           Expect = '+' }
        @{ Name = 'same key, CapsLock off';        In = '1';     Caps = 0; From = $bEn; To = $bTh
           Expect = [string][char]0x0E45 }
        @{ Name = 'CapsLock: back the other way';  In = '+';     Caps = 1; From = $bTh; To = $bEn
           Expect = '1' }
    )

    foreach ($c in $cases) {
        $caps = if ($c.ContainsKey('Caps')) { $c.Caps } else { 0 }
        $r = Convert-LayoutText -Text $c.In -From $c.From -To $c.To -Caps $caps
        # -ceq, not -eq: PowerShell's default string comparison ignores case,
        # which would hide exactly the capital-letter bugs these cases exist for.
        if ($r.Text -ceq $c.Expect) {
            Write-Host ("  PASS  {0,-36} '{1}' -> '{2}'" -f $c.Name, $c.In, $r.Text) -ForegroundColor Green
        } else {
            $fail++
            Write-Host ("  FAIL  {0,-36} '{1}' -> '{2}'  expected '{3}'" -f $c.Name, $c.In, $r.Text, $c.Expect) -ForegroundColor Red
        }
    }

    # ---- direction is picked from the text, not told to it ------------------
    foreach ($t in @(@{ In = 'l;ylfu'; Want = 0x0409 }, @{ In = $TH_sawatdi; Want = 0x041E })) {
        $src = Select-SourceLayout $t.In $builtIn
        if ($src -and $src.LangId -eq $t.Want) {
            Write-Host ("  PASS  {0,-36} '{1}' -> {2}" -f 'source layout auto-detected', $t.In, $src.Name) -ForegroundColor Green
        } else {
            $fail++
            Write-Host ("  FAIL  {0,-36} '{1}' -> {2}" -f 'source layout auto-detected', $t.In, $(if ($src) { $src.Name } else { '<none>' })) -ForegroundColor Red
        }
    }
    # Text made only of characters both layouts can produce says nothing about
    # which one it was typed on, so it must be declined rather than guessed at.
    # ("/" and "-" sit on the Thai layout too, on the 2 and 3 keys.)
    $ambiguous = '-/-/'
    if ($null -eq (Select-SourceLayout $ambiguous $builtIn)) {
        Write-Host ("  PASS  {0,-36} '{1}'" -f 'ambiguous text is declined', $ambiguous) -ForegroundColor Green
    } else {
        $fail++
        Write-Host ("  FAIL  {0,-36} '{1}' was converted anyway" -f 'ambiguous text is declined', $ambiguous) -ForegroundColor Red
    }

    # ---- every key round-trips, in both Caps Lock states --------------------
    $shifted = 0
    foreach ($caps in 0, 1) {
        foreach ($pos in $bEn.Maps[$caps].Forward.Keys) {
            $enChar = [string]$bEn.Maps[$caps].Forward[$pos]
            $thChar = [string]$bTh.Maps[$caps].Forward[$pos]
            $back = Convert-LayoutText -Text $thChar -From $bTh -To $bEn -Caps $caps
            if ($back.Text -cne $enChar) {
                $fail++
                Write-Host ("  FAIL  round trip broken for EN key '{0}' (caps={1})" -f $enChar, $caps) -ForegroundColor Red
            }
            if ($caps -eq 0 -and ([char]::IsUpper($enChar[0]) -or '~!@#$%^&*()_+{}|:"<>?'.IndexOf($enChar[0]) -ge 0)) { $shifted++ }
        }
    }

    # A realistic mixed sentence, shift layer included, must come back unchanged
    # whichever Caps Lock state it was typed under.
    $mixed = 'Hello, World! (Test #1) A_B+C {x} "q" <y>|z'
    foreach ($caps in 0, 1) {
        $there = Convert-LayoutText -Text $mixed -From $bEn -To $bTh -Caps $caps
        $back  = Convert-LayoutText -Text $there.Text -From $bTh -To $bEn -Caps $caps
        if ($back.Text -ceq $mixed) {
            Write-Host ("  PASS  {0,-36} '{1}'" -f "sentence round trip (caps=$caps)", $there.Text) -ForegroundColor Green
        } else {
            $fail++
            Write-Host ("  FAIL  {0,-36} got '{1}'" -f "sentence round trip (caps=$caps)", $back.Text) -ForegroundColor Red
        }
    }

    Write-Host ""
    Write-Host ("  built-in table: {0} keys, {1} of them shift-layer." -f $bEn.Maps[0].Forward.Count, $shifted) -ForegroundColor DarkGray

    # ---- what Windows itself reports, checked against the built-in table ----
    # This is the part that proves the generic layout probing is trustworthy:
    # on a machine with Thai installed, what Windows says each key produces must
    # match the hand-written Kedmanee table exactly.
    Write-Host ""
    Write-Host "  Layouts installed on this machine:" -ForegroundColor DarkGray
    $live = Get-InstalledLayouts
    foreach ($l in $live) {
        Write-Host ("    0x{0:X4}  {1,-34} {2} keys" -f $l.LangId, $l.Name, $l.KeyCount) -ForegroundColor DarkGray
    }
    if ($live.Count -lt 2) {
        Write-Host "  (fewer than two layouts installed - the built-in Thai table would be used)" -ForegroundColor Yellow
    }

    $liveEn = $live | Where-Object { $_.LangId -eq 0x0409 }
    $liveTh = $live | Where-Object { $_.LangId -eq 0x041E }
    if ($liveEn -and $liveTh) {
        foreach ($caps in 0, 1) {
            $checked = 0; $bad = 0
            foreach ($pos in $liveEn.Maps[$caps].Forward.Keys) {
                if (-not $liveTh.Maps[$caps].Forward.ContainsKey($pos)) { continue }
                $enChar = $liveEn.Maps[$caps].Forward[$pos]
                $probed = $liveTh.Maps[$caps].Forward[$pos]
                $oraclePos = $bEn.Maps[$caps].Reverse[$enChar]
                if ($null -eq $oraclePos) { continue }
                $expected = $bTh.Maps[$caps].Forward[$oraclePos]
                $checked++
                if ($probed -cne $expected) {
                    $bad++
                    Write-Host ("  FAIL  probed Thai layout (caps={0}): EN '{1}' -> U+{2:X4}, table says U+{3:X4}" -f `
                        $caps, $enChar, [int]$probed, [int]$expected) -ForegroundColor Red
                }
            }
            $fail += $bad
            if ($bad -eq 0) {
                Write-Host ("  PASS  {0,-36} {1} keys agree with the built-in table" -f "probed Thai layout (caps=$caps)", $checked) -ForegroundColor Green
            }
        }
    } else {
        Write-Host "  (skipped the probe-vs-table check: needs both 0x0409 and 0x041E installed)" -ForegroundColor Yellow
    }

    Write-Host ""
    Write-Host ("  {0} failure(s)." -f $fail) -ForegroundColor $(if ($fail) { 'Red' } else { 'Green' })
    exit $(if ($fail) { 1 } else { 0 })
}

# ==============================================================================
#  Single instance
# ==============================================================================
# Two copies running at once is worse than none: both would react to the same
# Win+Space, convert the same selection one after the other, and hand back the
# original text. The second copy also loses the quit hotkey to the first, which
# makes the pair awkward to get rid of.
# -ListLayouts and -ConfigureHotkey report or reconfigure and exit, so neither
# must be blocked by a copy that is already running -- that is exactly when
# someone wants to use them.
if (-not $ListLayouts -and -not $ConfigureHotkey) {
    $isFirstInstance = $false    # [ref] needs the variable to exist first
    $script:InstanceMutex = New-Object System.Threading.Mutex($true, 'Local\KeyboardLangFixer.SingleInstance', [ref]$isFirstInstance)
    if (-not $isFirstInstance) {
        Write-Host "Keyboard Language Fixer is already running." -ForegroundColor Yellow
        Write-Host "Quit the existing one from its tray icon (or with $QuitHotkey) before starting another." -ForegroundColor DarkGray
        exit 2
    }
}


$MOD_ALT      = 0x0001
$MOD_CONTROL  = 0x0002
$MOD_SHIFT    = 0x0004
$MOD_WIN      = 0x0008
$MOD_NOREPEAT = 0x4000
$WM_HOTKEY    = 0x0312
$WM_TRIGGER   = [KbFix.Watcher]::WM_TRIGGER
$WM_INPUTLANGCHANGEREQUEST = 0x0050
$PM_REMOVE    = 0x0001

$VK_SHIFT  = 0x10; $VK_CONTROL = 0x11; $VK_MENU = 0x12
$VK_LWIN   = 0x5B; $VK_RWIN    = 0x5C
$VK_C      = 0x43; $VK_V       = 0x56
$VK_INSERT = 0x2D
$KEYEVENTF_KEYUP = 0x0002

$HOTKEY_ID_CONVERT = 1
$HOTKEY_ID_QUIT    = 2

# Windows classes where Ctrl+C means "interrupt the running program", not "copy".
$ConsoleWindowClasses = @(
    'ConsoleWindowClass'                # conhost / cmd / classic PowerShell
    'CASCADIA_HOSTING_WINDOW_CLASS'     # Windows Terminal
    'VirtualConsoleClass'               # ConEmu / Cmder
    'mintty'                            # Git Bash / MSYS2
    'PuTTY'
)

function ConvertTo-HotkeySpec([string]$spec) {
    $ctrl = $false; $alt = $false; $shift = $false; $win = $false
    $keyName = $null
    foreach ($part in ($spec -split '\+' | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
        switch ($part.ToLowerInvariant()) {
            'ctrl'    { $ctrl = $true;  continue }
            'control' { $ctrl = $true;  continue }
            'alt'     { $alt = $true;   continue }
            'shift'   { $shift = $true; continue }
            'win'     { $win = $true;   continue }
            default   { $keyName = $part }
        }
    }
    if (-not $keyName) { throw "Hotkey '$spec' has no key, only modifiers." }
    try   { $vk = [int][Enum]::Parse([System.Windows.Forms.Keys], $keyName, $true) }
    catch { throw "Hotkey '$spec': '$keyName' is not a valid key name." }

    $mods = 0
    if ($ctrl)  { $mods = $mods -bor $MOD_CONTROL }
    if ($alt)   { $mods = $mods -bor $MOD_ALT }
    if ($shift) { $mods = $mods -bor $MOD_SHIFT }
    if ($win)   { $mods = $mods -bor $MOD_WIN }

    return [pscustomobject]@{
        Ctrl = $ctrl; Alt = $alt; Shift = $shift; Win = $win
        Vk = $vk; Mods = ($mods -bor $MOD_NOREPEAT); Display = $spec
    }
}

# ==============================================================================
#  Keyboard / clipboard plumbing
# ==============================================================================

function Test-KeyDown([int]$vk) {
    return (([KbFix.Native]::GetAsyncKeyState($vk) -band 0x8000) -ne 0)
}

# Toggle keys live in the LOW bit. GetAsyncKeyState is documented as unreliable
# for the toggle bit, so this uses GetKeyState.
function Get-CapsLockState {
    return [int]([KbFix.Native]::GetKeyState(0x14) -band 0x0001)
}

<#
  The trigger fires while Win (or Ctrl/Alt) is still physically held. Anything
  sent at that moment would pick those modifiers up, so wait for the user to
  let go, then force them up in case one is stuck.
#>
$SIG = [UIntPtr][uint32][KbFix.Watcher]::SIGNATURE

function Clear-Modifiers([int]$timeoutMs = 900) {
    $watched = @($VK_CONTROL, $VK_MENU, $VK_SHIFT, $VK_LWIN, $VK_RWIN)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        if (-not ($watched | Where-Object { Test-KeyDown $_ })) { break }
        Start-Sleep -Milliseconds 15
    }
    foreach ($vk in $watched) {
        [KbFix.Native]::keybd_event([byte]$vk, 0, $KEYEVENTF_KEYUP, $SIG)
    }
    Start-Sleep -Milliseconds 20
}

function Send-KeyCombo([int]$modVk, [int]$vk) {
    [KbFix.Native]::keybd_event([byte]$modVk, 0, 0, $SIG)
    [KbFix.Native]::keybd_event([byte]$vk, 0, 0, $SIG)
    Start-Sleep -Milliseconds 15
    [KbFix.Native]::keybd_event([byte]$vk, 0, $KEYEVENTF_KEYUP, $SIG)
    [KbFix.Native]::keybd_event([byte]$modVk, 0, $KEYEVENTF_KEYUP, $SIG)
}

# Clipboard calls throw while another process owns the clipboard, so retry a bit.
function Get-ClipboardText([int]$tries = 8) {
    for ($i = 0; $i -lt $tries; $i++) {
        try {
            if ([System.Windows.Forms.Clipboard]::ContainsText()) {
                return [System.Windows.Forms.Clipboard]::GetText()
            }
            return $null
        } catch { Start-Sleep -Milliseconds 30 }
    }
    return $null
}

function Set-ClipboardText([AllowNull()][string]$text, [int]$tries = 8) {
    for ($i = 0; $i -lt $tries; $i++) {
        try {
            if ([string]::IsNullOrEmpty($text)) { [System.Windows.Forms.Clipboard]::Clear() }
            else { [System.Windows.Forms.Clipboard]::SetText($text) }
            return
        } catch { Start-Sleep -Milliseconds 30 }
    }
}

# Puts back what the user had copied. If there was no text there (an image, or
# an empty clipboard) the converted text is left behind rather than clearing the
# clipboard: the original is gone either way, and text is the less annoying of
# the two leftovers.
function Restore-Clipboard([AllowNull()][string]$saved) {
    if (-not [string]::IsNullOrEmpty($saved)) { Set-ClipboardText $saved }
}

# Returns $true as soon as some app has written to the clipboard.
function Wait-ForClipboardWrite([uint32]$before, [int]$timeoutMs) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        Start-Sleep -Milliseconds 20
        if ([KbFix.Native]::GetClipboardSequenceNumber() -ne $before) { return $true }
    }
    return $false
}

<#
  Reads the current selection, leaving the clipboard completely untouched when
  there is nothing selected -- which is most of the time, since this runs on
  every single Win+Space. That is why the copy is detected with the clipboard
  sequence number rather than by clearing the clipboard and seeing what lands:
  clearing would throw away whatever the user had copied, including images and
  files that a text-only restore could never put back.

  Ctrl+Insert is tried first because in a console window Ctrl+C would interrupt
  the running program instead of copying. Ctrl+C is only a fallback, and only in
  non-console windows, for the few apps that ignore Ctrl+Insert.
#>
function Get-Selection([bool]$isConsole) {
    $seq = [KbFix.Native]::GetClipboardSequenceNumber()

    Send-KeyCombo $VK_CONTROL $VK_INSERT
    if (Wait-ForClipboardWrite $seq 260) {
        Write-Log '  copy: Ctrl+Insert worked'
        return (Get-ClipboardText)
    }
    if ($isConsole) { return $null }

    Send-KeyCombo $VK_CONTROL $VK_C
    if (Wait-ForClipboardWrite $seq 320) {
        Write-Log '  copy: Ctrl+C worked'
        return (Get-ClipboardText)
    }
    return $null
}

function Get-ForegroundLangId {
    $hkl = [KbFix.Native]::GetKeyboardLayout(
        [KbFix.Native]::GetWindowThreadProcessId([KbFix.Native]::GetForegroundWindow(), [IntPtr]::Zero))
    return [int](([int64]$hkl) -band 0xFFFF)
}

<#
  Asks the focused window to switch to a specific layout, so whatever the user
  types next matches the text that was just converted.

  The request is verified rather than fired and forgotten: Windows' own
  Win+Space switch is committed by the language flyout a moment after the key is
  released, and if that lands after this request it silently undoes it. So the
  result is confirmed, re-requested if it did not stick, and confirmed again
  once the flyout has had time to settle.
#>
function Set-InputLanguage([int]$langId) {
    $buf = New-Object 'IntPtr[]' 64
    $n = [KbFix.Native]::GetKeyboardLayoutList(64, $buf)
    $target = [IntPtr]::Zero
    for ($i = 0; $i -lt $n; $i++) {
        if ((([int64]$buf[$i]) -band 0xFFFF) -eq $langId) { $target = $buf[$i]; break }
    }
    if ($target -eq [IntPtr]::Zero) { return $false }

    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        [void][KbFix.Native]::PostMessage(
            [KbFix.Native]::GetForegroundWindow(),
            $WM_INPUTLANGCHANGEREQUEST, [IntPtr]::Zero, $target)
        Start-Sleep -Milliseconds 120
        if ((Get-ForegroundLangId) -ne $langId) { continue }

        Start-Sleep -Milliseconds 220          # let the flyout finish its own switch
        if ((Get-ForegroundLangId) -eq $langId) { return $true }
    }
    return $false
}

function Send-Failure {
    try { [System.Media.SystemSounds]::Hand.Play() } catch { }
}

function Write-Log([string]$message) {
    if (-not $script:LogFile) { return }
    try {
        Add-Content -Path $script:LogFile -Encoding UTF8 -Value `
            ("{0:HH:mm:ss.fff}  {1}" -f (Get-Date), $message)
    } catch { }
}

# ==============================================================================
#  The actual action
# ==============================================================================
<#
  Runs on every trigger. When nothing is selected this does nothing visible and
  the language switch Windows already performed is the only effect.
#>
function Invoke-FixSelection {
    Clear-Modifiers

    $class = [KbFix.Native]::ForegroundWindowClass()
    $isConsole = $ConsoleWindowClasses -contains $class
    Write-Log ("trigger: foreground class '{0}'{1}" -f $class, $(if ($isConsole) { ' (console)' } else { '' }))

    # Read before copying so the old clipboard can be put back afterwards. This
    # is a read only; if nothing turns out to be selected the clipboard is never
    # written to at all.
    $saved = Get-ClipboardText
    $selection = Get-Selection $isConsole

    if ([string]::IsNullOrEmpty($selection)) {
        Write-Log "  nothing copied -> treated as 'no selection', language switch only"
        return $false                       # no selection: plain language switch
    }

    # Caps Lock changes what the keys produced, differently per layout, so the
    # conversion has to be told which state the text was typed under. The
    # current state is the best evidence available: the text was typed seconds
    # ago, and Caps Lock is exactly the kind of thing left on by accident.
    $caps = Get-CapsLockState
    $source = Select-SourceLayout $selection $script:Layouts $caps
    $target = if ($source) { Select-TargetLayout $source $script:Layouts } else { $null }
    if (-not $source -or -not $target) {
        Restore-Clipboard $saved
        Send-Failure
        Write-Log ("  copied '{0}' but no layout explains it, left alone" -f $selection)
        return $false
    }

    $result = Convert-LayoutText -Text $selection -From $source -To $target -Caps $caps
    Write-Log ("  copied '{0}' -> '{1}'  [{2} -> {3}{4}]" -f `
        $selection, $result.Text, $source.Name, $target.Name, $(if ($caps) { ', CapsLock on' } else { '' }))

    if ($result.Text -ceq $selection) {
        Restore-Clipboard $saved
        Send-Failure                        # nothing in the selection is convertible
        Write-Log "  nothing convertible, left alone"
        return $false
    }

    Set-ClipboardText $result.Text
    Start-Sleep -Milliseconds 40

    # Pasting an empty clipboard would wipe the selection instead of fixing it.
    $staged = Get-ClipboardText
    if ($staged -ne $result.Text) {
        Restore-Clipboard $saved
        Send-Failure
        Write-Log ("  clipboard write did not stick (got '{0}'), aborted without pasting" -f $staged)
        return $false
    }

    if ($isConsole) { Send-KeyCombo $VK_SHIFT $VK_INSERT } else { Send-KeyCombo $VK_CONTROL $VK_V }
    Write-Log ("  pasted via {0}" -f $(if ($isConsole) { 'Shift+Insert' } else { 'Ctrl+V' }))

    if (-not $script:NoLangSwitchFlag -and $target.LangId -ne 0) {
        Start-Sleep -Milliseconds 60
        if (Set-InputLanguage $target.LangId) {
            Write-Log ("  input language set to 0x{0:X4}" -f $target.LangId)
        } else {
            Write-Log ("  could not settle input language on 0x{0:X4} (now 0x{1:X4})" -f $target.LangId, (Get-ForegroundLangId))
        }
    }

    # Give the target app time to read the clipboard before putting it back.
    Start-Sleep -Milliseconds 250
    Restore-Clipboard $saved
    return $true
}

# ==============================================================================
#  Tray icon
# ==============================================================================
$script:NoLangSwitchFlag = [bool]$NoLangSwitch
$script:LogFile = if ($LogPath) { $LogPath } else { $null }

# ==============================================================================
#  Which layouts this machine can convert between
# ==============================================================================
$script:Layouts = @(Get-InstalledLayouts)
$usingBuiltIn = $false
if ($script:Layouts.Count -lt 2) {
    # Only one layout installed (or probing came back empty): fall back to the
    # shipped Thai <-> English table so the tool is still useful.
    $script:Layouts = @(Get-BuiltInLayouts)
    $usingBuiltIn = $true
}

<#
  Reports which keys THIS machine uses to switch input language, which differs
  between machines and matters when deciding what to bind the fixer to.

  Win+Space is a shell shortcut built into Windows 8 and later. It is not listed
  in the "Input language hot keys" dialog and cannot be turned off there, so it
  is the one combination that can be relied on everywhere. The legacy toggles
  under HKCU\Keyboard Layout\Toggle are the configurable ones, and their value
  really does vary from machine to machine.
#>
function Get-LanguageSwitchHotkeys {
    $found = @('Win+Space (built into Windows 8+, cannot be unassigned)')
    $names = @{ '1' = 'Left Alt+Shift'; '2' = 'Ctrl+Shift'; '4' = 'Grave accent (`)' }
    $key = 'HKCU:\Keyboard Layout\Toggle'
    if (Test-Path $key) {
        $props = Get-ItemProperty -Path $key
        foreach ($value in 'Language Hotkey', 'Layout Hotkey') {
            if ($props.PSObject.Properties.Name -notcontains $value) { continue }
            $name = $names["$($props.$value)"]
            if ($name -and $found -notcontains $name) { $found += $name }
        }
    } else {
        $found += 'Left Alt+Shift (Windows default when the toggle is unset)'
    }
    return $found
}

function Show-Layouts {
    Write-Host "Layouts available for conversion:" -ForegroundColor Cyan
    foreach ($l in $script:Layouts) {
        Write-Host ("  0x{0:X4}  {1,-36} {2} keys{3}" -f `
            $l.LangId, $l.Name, $l.KeyCount, $(if ($l.BuiltIn) { '  (built-in table)' } else { '' }))
    }
    if ($usingBuiltIn) {
        Write-Host "Windows reported fewer than two keyboard layouts, so the built-in Thai table is in use." -ForegroundColor Yellow
        Write-Host "Add a second keyboard layout in Windows Settings to convert between other languages." -ForegroundColor DarkGray
    }
    Write-Host "This machine switches input language with:" -ForegroundColor Cyan
    foreach ($h in Get-LanguageSwitchHotkeys) { Write-Host "  $h" }
}

if ($ListLayouts) { Show-Layouts; exit 0 }

<#
  Uses icon.ico from the script folder when it is there, so the icon can be
  swapped by replacing that one file (see tools\Convert-PngToIco.ps1). Falls
  back to a drawn placeholder so the tool still runs if the file is missing.
#>
function Get-TrayIconImage {
    $icoPath = Join-Path $PSScriptRoot 'icon.ico'
    if (Test-Path -LiteralPath $icoPath) {
        try {
            $small = [System.Windows.Forms.SystemInformation]::SmallIconSize
            return New-Object System.Drawing.Icon $icoPath, $small.Width, $small.Height
        } catch {
            Write-Host "icon.ico could not be loaded ($_), using the built-in icon." -ForegroundColor Yellow
        }
    }

    $bmp = New-Object System.Drawing.Bitmap 32, 32
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 33, 118, 199))), 0, 0, 31, 31)
    $font = New-Object System.Drawing.Font 'Segoe UI', 15, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
    # U+0E01 is Thai "ko kai", the letter that shares the "d" key.
    $g.DrawString([string][char]0x0E01, $font, [System.Drawing.Brushes]::White,
                  (New-Object System.Drawing.RectangleF 0, 0, 32, 32), $fmt)
    $g.Dispose()
    return [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
}

# ==============================================================================
#  Settings
# ==============================================================================
# Kept next to the script so the whole folder stays portable: copy it to another
# machine and the chosen hotkey travels with it.
$script:SettingsPath = Join-Path $PSScriptRoot 'settings.json'

function Read-Settings {
    if (-not (Test-Path -LiteralPath $script:SettingsPath)) { return $null }
    try { return Get-Content -LiteralPath $script:SettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch {
        Write-Host "settings.json could not be read ($_), using defaults." -ForegroundColor Yellow
        return $null
    }
}

function Write-Settings([hashtable]$values) {
    try {
        $values | ConvertTo-Json | Set-Content -LiteralPath $script:SettingsPath -Encoding UTF8
        return $true
    } catch {
        [System.Windows.Forms.MessageBox]::Show(
            "Could not save settings to`n$($script:SettingsPath)`n`n$_",
            'Keyboard Language Fixer', 'OK', 'Warning') | Out-Null
        return $false
    }
}

# ==============================================================================
#  Trigger registration
# ==============================================================================
$script:HookInstalled = $false
$script:HotkeyClaimed = $false
$script:Hk = $null

function Unregister-Trigger {
    if ($script:HookInstalled) { [KbFix.Watcher]::Uninstall(); $script:HookInstalled = $false }
    if ($script:HotkeyClaimed) {
        [void][KbFix.Native]::UnregisterHotKey([IntPtr]::Zero, $HOTKEY_ID_CONVERT)
        $script:HotkeyClaimed = $false
    }
}

<#
  Starts listening for a hotkey. Win+<key> combinations belong to the Windows
  shell and cannot be claimed with RegisterHotKey, so those are watched with a
  pass-through hook instead; everything else is claimed exclusively.
  Returns $null on success or a message explaining why it could not be done.
#>
function Register-Trigger([string]$spec) {
    try { $hk = ConvertTo-HotkeySpec $spec } catch { return "$_" }

    $useHook = switch ($Mode) {
        'Hook'   { $true }
        'Hotkey' { $false }
        default  { $hk.Win }
    }

    Unregister-Trigger
    if ($useHook) {
        if (-not [KbFix.Watcher]::Install($hk.Vk, $hk.Ctrl, $hk.Alt, $hk.Shift, $hk.Win, $false)) {
            return 'Windows refused to install the keyboard hook.'
        }
        $script:HookInstalled = $true
    } else {
        if (-not [KbFix.Native]::RegisterHotKey([IntPtr]::Zero, $HOTKEY_ID_CONVERT, $hk.Mods, $hk.Vk)) {
            return "'$spec' is already taken by another program."
        }
        $script:HotkeyClaimed = $true
    }
    $script:Hk = $hk
    $script:UseHook = $useHook
    return $null
}

# ==============================================================================
#  Hotkey picker
# ==============================================================================
# Modifier checkboxes and a key list rather than "press the combination you
# want": capturing raw keystrokes cannot see Win+Space, because the shell
# swallows it before any dialog gets a look at it.
function Show-HotkeyDialog([string]$current) {
    $keyChoices = @('Space', 'Enter', 'Tab', 'Back', 'Escape', 'Insert', 'Delete', 'Home', 'End', 'PageUp', 'PageDown',
                    'Pause', 'CapsLock', 'Left', 'Right', 'Up', 'Down') +
                  (65..90 | ForEach-Object { [char]$_ }) +
                  (0..9 | ForEach-Object { "D$_" }) +
                  (1..12 | ForEach-Object { "F$_" }) +
                  @('Oem1', 'Oem2', 'Oem3', 'Oem4', 'Oem5', 'Oem6', 'Oem7', 'OemMinus', 'Oemplus', 'Oemcomma', 'OemPeriod')

    $form = New-Object System.Windows.Forms.Form
    $form.Text = 'Keyboard Language Fixer - hotkey'
    $form.FormBorderStyle = 'FixedDialog'
    $form.MaximizeBox = $false; $form.MinimizeBox = $false
    $form.StartPosition = 'CenterScreen'
    # The tool runs windowless in the tray, so it is never the foreground process
    # and Windows refuses its SetForegroundWindow calls. Without TopMost the
    # dialog can open behind whatever the user was looking at.
    $form.TopMost = $true
    $form.ClientSize = New-Object System.Drawing.Size 400, 232
    $form.Font = New-Object System.Drawing.Font 'Segoe UI', 9
    try { $form.Icon = Get-TrayIconImage } catch { }

    $lbl = New-Object System.Windows.Forms.Label
    $lbl.Text = 'Select text, press this combination, and it gets converted.'
    $lbl.SetBounds(14, 12, 372, 20)
    $form.Controls.Add($lbl)

    $group = New-Object System.Windows.Forms.GroupBox
    $group.Text = 'Modifiers'
    $group.SetBounds(14, 36, 372, 60)
    $form.Controls.Add($group)

    $boxes = @{}
    $x = 14
    foreach ($name in 'Ctrl', 'Alt', 'Shift', 'Win') {
        $cb = New-Object System.Windows.Forms.CheckBox
        $cb.Text = $name
        $cb.SetBounds($x, 24, 66, 22)
        $group.Controls.Add($cb)
        $boxes[$name] = $cb
        $x += 76
    }

    $keyLabel = New-Object System.Windows.Forms.Label
    $keyLabel.Text = 'Key'
    $keyLabel.SetBounds(14, 108, 30, 20)
    $form.Controls.Add($keyLabel)

    $combo = New-Object System.Windows.Forms.ComboBox
    $combo.DropDownStyle = 'DropDownList'
    $combo.SetBounds(48, 104, 160, 24)
    foreach ($k in $keyChoices) { [void]$combo.Items.Add([string]$k) }
    $form.Controls.Add($combo)

    $preview = New-Object System.Windows.Forms.Label
    $preview.SetBounds(14, 140, 372, 26)
    $preview.Font = New-Object System.Drawing.Font 'Segoe UI', 12, ([System.Drawing.FontStyle]::Bold)
    $form.Controls.Add($preview)

    $note = New-Object System.Windows.Forms.Label
    $note.SetBounds(14, 166, 372, 30)
    $note.ForeColor = [System.Drawing.Color]::DimGray
    $form.Controls.Add($note)

    # Pre-fill from whatever is in use right now.
    $cur = ConvertTo-HotkeySpec $current
    $boxes['Ctrl'].Checked  = $cur.Ctrl
    $boxes['Alt'].Checked   = $cur.Alt
    $boxes['Shift'].Checked = $cur.Shift
    $boxes['Win'].Checked   = $cur.Win
    $curKey = [string]([System.Windows.Forms.Keys]$cur.Vk)
    if ($combo.Items.Contains($curKey)) { $combo.SelectedItem = $curKey } else { $combo.SelectedIndex = 0 }

    $ok = New-Object System.Windows.Forms.Button
    $ok.Text = 'Save'; $ok.SetBounds(214, 196, 84, 26)
    $ok.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $form.Controls.Add($ok); $form.AcceptButton = $ok

    $cancel = New-Object System.Windows.Forms.Button
    $cancel.Text = 'Cancel'; $cancel.SetBounds(302, 196, 84, 26)
    $cancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $form.Controls.Add($cancel); $form.CancelButton = $cancel

    $build = {
        $parts = @()
        foreach ($name in 'Ctrl', 'Alt', 'Shift', 'Win') { if ($boxes[$name].Checked) { $parts += $name } }
        if ($combo.SelectedItem) { $parts += [string]$combo.SelectedItem }
        $spec = $parts -join '+'
        $preview.Text = $spec

        if (-not $combo.SelectedItem) {
            $note.Text = ''; $ok.Enabled = $false; return
        }
        if ($boxes['Win'].Checked) {
            $note.Text = "Windows keeps its own use of this combination; the fixer just adds to it."
        } elseif ($parts.Count -lt 2) {
            $note.Text = 'Pick at least one modifier, or this will fire on ordinary typing.'
        } else {
            $note.Text = 'This combination will be taken over completely.'
        }
        $ok.Enabled = ($boxes['Win'].Checked -or $parts.Count -ge 2)
    }
    foreach ($name in 'Ctrl', 'Alt', 'Shift', 'Win') { $boxes[$name].Add_CheckedChanged($build) }
    $combo.Add_SelectedIndexChanged($build)
    & $build

    # This process is started with a hidden window style (from the tray, via
    # Start-Hidden.vbs; standalone, via Settings.cmd). Windows applies that show
    # state to the first top-level window the process creates, which would be
    # this dialog -- it exists but never appears. Force it visible.
    $form.Add_Shown({
        [void][KbFix.Native]::ShowWindow($form.Handle, 5)          # SW_SHOW
        [void][KbFix.Native]::SetForegroundWindow($form.Handle)
        $form.Activate()
    }.GetNewClosure())

    if ($form.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { $form.Dispose(); return $null }
    $result = $preview.Text
    $form.Dispose()
    return $result
}

# ==============================================================================
#  Tray icon
# ==============================================================================
function Test-StartupInstalled {
    return (Test-Path -LiteralPath (Join-Path ([Environment]::GetFolderPath('Startup')) 'Keyboard Language Fixer.lnk'))
}

function New-TrayIcon {
    $ni = New-Object System.Windows.Forms.NotifyIcon
    $ni.Icon = Get-TrayIconImage
    $ni.Visible = $true

    $menu = New-Object System.Windows.Forms.ContextMenuStrip
    $script:MenuHeader = $menu.Items.Add('')
    $script:MenuHeader.Enabled = $false
    [void]$menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))

    $itemHotkey = $menu.Items.Add('Change hotkey...')
    $itemHotkey.Add_Click({
        $chosen = Show-HotkeyDialog $script:Hk.Display
        if (-not $chosen -or $chosen -eq $script:Hk.Display) { return }

        $previous = $script:Hk.Display
        $problem = Register-Trigger $chosen
        if ($problem) {
            [System.Windows.Forms.MessageBox]::Show(
                "Could not use $chosen`:`n$problem`n`nKeeping $previous.",
                'Keyboard Language Fixer', 'OK', 'Warning') | Out-Null
            [void](Register-Trigger $previous)      # put the working one back
            return
        }
        [void](Write-Settings @{ Hotkey = $chosen })
        Update-TrayText
    })

    $script:MenuStartup = $menu.Items.Add('Start with Windows')
    $script:MenuStartup.Checked = Test-StartupInstalled
    $script:MenuStartup.Add_Click({
        $installer = Join-Path $PSScriptRoot 'Install-Startup.ps1'
        if (-not (Test-Path -LiteralPath $installer)) {
            [System.Windows.Forms.MessageBox]::Show("Install-Startup.ps1 is missing from`n$PSScriptRoot",
                'Keyboard Language Fixer', 'OK', 'Warning') | Out-Null
            return
        }
        $psArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$installer`"")
        if (Test-StartupInstalled) { $psArgs += '-Uninstall' }
        Start-Process powershell.exe -ArgumentList $psArgs -WindowStyle Hidden -Wait
        $script:MenuStartup.Checked = Test-StartupInstalled
    })

    [void]$menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))
    $itemExit = $menu.Items.Add('Exit')
    $itemExit.Add_Click({ [KbFix.Native]::PostQuitMessage(0) })

    $ni.ContextMenuStrip = $menu
    return $ni
}

function Update-TrayText {
    $text = "Keyboard Language Fixer  -  select text + $($script:Hk.Display)"
    if ($script:Tray) {
        # NotifyIcon.Text throws above 63 characters, and a long hotkey name can
        # reach that.
        $script:Tray.Text = if ($text.Length -gt 63) { $text.Substring(0, 60) + '...' } else { $text }
        if ($script:MenuHeader) { $script:MenuHeader.Text = $text }
    }
    Write-Host $text -ForegroundColor Cyan
}

# An explicit -Hotkey wins over the saved one; otherwise the saved one wins over
# the default.
$settings = Read-Settings
if (-not $PSBoundParameters.ContainsKey('Hotkey') -and $settings -and $settings.Hotkey) {
    $Hotkey = [string]$settings.Hotkey
}

# ==============================================================================
#  Standalone hotkey picker
# ==============================================================================
if ($ConfigureHotkey) {
    $chosen = Show-HotkeyDialog $Hotkey
    if (-not $chosen) { exit 0 }
    if (-not (Write-Settings @{ Hotkey = $chosen })) { exit 1 }

    # Restart whatever is running so the new hotkey takes effect immediately.
    $running = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
        Where-Object { $_.CommandLine -like "*$($MyInvocation.MyCommand.Name)*" -and
                       $_.CommandLine -notlike '*ConfigureHotkey*' -and
                       $_.ProcessId -ne $PID })
    foreach ($proc in $running) { try { Stop-Process -Id $proc.ProcessId -Force } catch { } }
    if ($running.Count -gt 0) {
        Start-Sleep -Milliseconds 600
        $launcher = Join-Path $PSScriptRoot 'Start-Hidden.vbs'
        if (Test-Path -LiteralPath $launcher) {
            Start-Process wscript.exe -ArgumentList "`"$launcher`""
        }
    }

    [System.Windows.Forms.MessageBox]::Show(
        "Hotkey set to $chosen." + $(if ($running.Count) { "`n`nKeyboard Language Fixer has been restarted." }
                                    else { "`n`nIt will be used the next time the tool starts." }),
        'Keyboard Language Fixer', 'OK', 'Information') | Out-Null
    exit 0
}

# ==============================================================================
#  Wire up the trigger and pump messages
# ==============================================================================

$problem = Register-Trigger $Hotkey
if ($problem) { throw "Could not listen for '$Hotkey': $problem" }

$hkQuit = ConvertTo-HotkeySpec $QuitHotkey
$quitRegistered = [KbFix.Native]::RegisterHotKey([IntPtr]::Zero, $HOTKEY_ID_QUIT, $hkQuit.Mods, $hkQuit.Vk)

$script:Tray = $null
if (-not $NoTrayIcon) {
    $script:Tray = New-TrayIcon
    $script:Tray.ShowBalloonTip(3000, 'Keyboard Language Fixer',
        "Ready. $($script:Hk.Display) still changes language; with text selected it also fixes the layout.", 'Info')
}
$tray = $script:Tray
Update-TrayText

Show-Layouts
Write-Host ("Mode: {0}   Quit: {1}{2}" -f `
    $(if ($script:UseHook) { 'keyboard hook (pass-through)' } else { 'RegisterHotKey (exclusive)' }),
    $hkQuit.Display,
    $(if (-not $quitRegistered) { ' (unavailable - use the tray icon)' })) -ForegroundColor DarkGray

try {
    $msg = New-Object KbFix.MSG
    while ([KbFix.Native]::GetMessage([ref]$msg, [IntPtr]::Zero, 0, 0) -gt 0) {

        $isTrigger = ($msg.message -eq $WM_TRIGGER) -or
                     ($msg.message -eq $WM_HOTKEY -and [int]$msg.wParam -eq $HOTKEY_ID_CONVERT)

        if ($msg.message -eq $WM_HOTKEY -and [int]$msg.wParam -eq $HOTKEY_ID_QUIT) {
            [KbFix.Native]::PostQuitMessage(0)
            continue
        }

        if ($isTrigger) {
            try { [void](Invoke-FixSelection) }
            catch { Write-Host "convert failed: $_" -ForegroundColor Yellow }

            # Holding the combo down can queue several triggers; only honour one.
            $drain = New-Object KbFix.MSG
            while ([KbFix.Native]::PeekMessage([ref]$drain, [IntPtr]::Zero, $WM_TRIGGER, $WM_TRIGGER, $PM_REMOVE)) { }

            # Converting blocked the loop long enough that Windows may have
            # started skipping the hook. Re-seat it.
            if ($script:HookInstalled) { [void][KbFix.Watcher]::Reinstall() }
            continue
        }

        [void][KbFix.Native]::TranslateMessage([ref]$msg)
        [void][KbFix.Native]::DispatchMessage([ref]$msg)
    }
}
finally {
    Unregister-Trigger
    [void][KbFix.Native]::UnregisterHotKey([IntPtr]::Zero, $HOTKEY_ID_QUIT)
    if ($tray) { $tray.Visible = $false; $tray.Dispose() }
    if ($script:InstanceMutex) {
        try { $script:InstanceMutex.ReleaseMutex() } catch { }
        $script:InstanceMutex.Dispose()
    }
}
