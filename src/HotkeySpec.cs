// ---------------------------------------------------------------------------
//  Parsing and describing a hotkey such as "Win+Space" or "Ctrl+Alt+X".
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace KbFix
{
    internal sealed class HotkeySpec
    {
        public bool Ctrl, Alt, Shift, Win;
        public int Vk;
        public string Display = "";

        public uint Mods
        {
            get
            {
                uint m = Native.MOD_NOREPEAT;
                if (Ctrl) m |= Native.MOD_CONTROL;
                if (Alt) m |= Native.MOD_ALT;
                if (Shift) m |= Native.MOD_SHIFT;
                if (Win) m |= Native.MOD_WIN;
                return m;
            }
        }

        public string KeyName { get { return NameOf((Keys)Vk); } }

        /// Keys has several pairs of names sharing one value, and ToString picks
        /// whichever was declared first -- so Caps Lock comes back as "Capital",
        /// which nobody calls it. The names people actually use are spelled out.
        public static string NameOf(Keys key)
        {
            switch (key)
            {
                case Keys.Capital: return "CapsLock";     // == Keys.CapsLock
                case Keys.Next: return "PageDown";        // == Keys.PageDown
                case Keys.Prior: return "PageUp";         // == Keys.PageUp
                case Keys.Return: return "Enter";         // == Keys.Enter
                default: return key.ToString();
            }
        }

        /// Throws with a readable message on bad input; callers show it to the user.
        public static HotkeySpec Parse(string spec)
        {
            if (string.IsNullOrEmpty(spec)) throw new FormatException("No hotkey given.");

            HotkeySpec h = new HotkeySpec();
            string keyName = null;
            foreach (string rawPart in spec.Split('+'))
            {
                string part = rawPart.Trim();
                if (part.Length == 0) continue;
                switch (part.ToLowerInvariant())
                {
                    case "ctrl":
                    case "control": h.Ctrl = true; break;
                    case "alt": h.Alt = true; break;
                    case "shift": h.Shift = true; break;
                    case "win": h.Win = true; break;
                    default: keyName = part; break;
                }
            }
            if (keyName == null)
                throw new FormatException("'" + spec + "' has modifiers but no key. A real key is required.");

            // Enum.Parse happily turns "1" into (Keys)1, which is VK_LBUTTON --
            // a key no keyboard hook ever sees. A hand-edited "Ctrl+Alt+1" would
            // then start up reporting success and never fire, so digits are
            // rejected outright: the digit keys are named D0..D9.
            if (char.IsDigit(keyName[0]))
                throw new FormatException("'" + keyName + "' is a number, not a key name. " +
                                          "The digit keys are called D0 to D9, so use \"D" + keyName + "\".");

            Keys key;
            try { key = (Keys)Enum.Parse(typeof(Keys), keyName, true); }
            catch { throw new FormatException("'" + keyName + "' is not a key name."); }
            if (!Enum.IsDefined(typeof(Keys), key))
                throw new FormatException("'" + keyName + "' is not a key name.");

            h.Vk = (int)key;
            h.Display = Describe(h.Ctrl, h.Alt, h.Shift, h.Win, key);
            return h;
        }

        /// Why this combination must not be claimed, or null when it is fine.
        ///
        /// Alt+Space is how Windows opens a window's Move/Size/Close menu,
        /// Alt+F4 closes one, Alt+Tab and Alt+Escape switch between them.
        /// Claiming one with RegisterHotKey takes it away from every application
        /// on the machine; NOT claiming it -- in hook mode, or anywhere the
        /// claim does not reach the window in front -- hands the application the
        /// raw chord and it does what it always did. Neither is acceptable.
        ///
        /// Holding Ctrl cancels all of these in Windows itself, which is why the
        /// check only applies without it.
        public static string ReservedReason(HotkeySpec h)
        {
            if (h == null || !h.Alt || h.Ctrl) return null;
            switch ((Keys)h.Vk)
            {
                case Keys.Space:
                    return "Alt+Space is how Windows opens a window's Move/Size/Close menu.";
                case Keys.F4:
                    return "Alt+F4 is how Windows closes a window.";
                case Keys.Tab:
                case Keys.Escape:
                    return "Windows uses this to switch between windows.";
                case Keys.Return:
                    return "Alt+Enter is Windows' full-screen and Properties key.";
                default:
                    return null;
            }
        }

        public static string Describe(bool ctrl, bool alt, bool shift, bool win, Keys key)
        {
            List<string> parts = new List<string>();
            if (ctrl) parts.Add("Ctrl");
            if (alt) parts.Add("Alt");
            if (shift) parts.Add("Shift");
            if (win) parts.Add("Win");
            parts.Add(NameOf(key));
            return string.Join("+", parts.ToArray());
        }
    }
}
