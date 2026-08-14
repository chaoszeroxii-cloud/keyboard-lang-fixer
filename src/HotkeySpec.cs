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

        public string KeyName { get { return ((Keys)Vk).ToString(); } }

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

            Keys key;
            try { key = (Keys)Enum.Parse(typeof(Keys), keyName, true); }
            catch { throw new FormatException("'" + keyName + "' is not a key name."); }

            h.Vk = (int)key;
            h.Display = Describe(h.Ctrl, h.Alt, h.Shift, h.Win, key);
            return h;
        }

        public static string Describe(bool ctrl, bool alt, bool shift, bool win, Keys key)
        {
            List<string> parts = new List<string>();
            if (ctrl) parts.Add("Ctrl");
            if (alt) parts.Add("Alt");
            if (shift) parts.Add("Shift");
            if (win) parts.Add("Win");
            parts.Add(key.ToString());
            return string.Join("+", parts.ToArray());
        }
    }
}
