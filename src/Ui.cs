// ---------------------------------------------------------------------------
//  The visible parts: the tray icon and menu, the hotkey picker, and the hidden
//  window the hotkey and the keyboard watcher post to.
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace KbFix
{
    internal sealed class TriggerEventArgs : EventArgs
    {
        public readonly FixMode Mode;
        public TriggerEventArgs(FixMode mode) { Mode = mode; }
    }

    /// A message-only window. The keyboard watcher and RegisterHotKey both post
    /// here rather than to the thread, because WinForms' message pump reliably
    /// dispatches window messages while thread messages can be swallowed.
    internal sealed class MessageWindow : NativeWindow
    {
        /// The program's own hotkey: fix the selection, or work one out.
        public const int WM_TRIGGER = 0x0400 + 77;   // WM_USER + 77
        /// The language-switch key, watched but never consumed. Only ever acts
        /// on a real selection.
        public const int WM_TRIGGER_LANG = 0x0400 + 78;
        /// Caps Lock, likewise watched but never consumed.
        public const int WM_TRIGGER_CASE = 0x0400 + 79;
        public const int HOTKEY_ID_CONVERT = 1;
        public const int HOTKEY_ID_QUIT = 2;

        private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

        /// Carries which key fired, because the three differ in what they are
        /// allowed to do.
        public event EventHandler<TriggerEventArgs> Trigger;
        public event EventHandler QuitRequested;

        private void Fire(FixMode mode)
        {
            if (Trigger != null) Trigger(this, new TriggerEventArgs(mode));
        }

        public MessageWindow()
        {
            CreateParams cp = new CreateParams();
            cp.Caption = "KeyboardLangFixer.MessageWindow";
            cp.Parent = HWND_MESSAGE;
            CreateHandle(cp);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_TRIGGER) { Fire(FixMode.Full); return; }
            if (m.Msg == WM_TRIGGER_LANG) { Fire(FixMode.SelectionOnly); return; }
            if (m.Msg == WM_TRIGGER_CASE) { Fire(FixMode.Case); return; }
            if (m.Msg == Native.WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == HOTKEY_ID_CONVERT) { Fire(FixMode.Full); return; }
                if (id == HOTKEY_ID_QUIT) { if (QuitRequested != null) QuitRequested(this, EventArgs.Empty); return; }
            }
            base.WndProc(ref m);
        }
    }

    internal static class Branding
    {
        public static string AppFolder
        {
            get { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar); }
        }

        public static string ExePath
        {
            get { return System.Reflection.Assembly.GetExecutingAssembly().Location; }
        }

        /// Uses icon.ico from the program folder when it is there, so the icon
        /// can be swapped by replacing one file. Falls back to a drawn
        /// placeholder so a missing file never stops the program.
        public static Icon TrayIcon()
        {
            string path = Path.Combine(AppFolder, "icon.ico");
            if (File.Exists(path))
            {
                try
                {
                    Size small = SystemInformation.SmallIconSize;
                    return new Icon(path, small.Width, small.Height);
                }
                catch { }
            }
            return Drawn();
        }

        public static Icon Drawn()
        {
            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 33, 118, 199)))
                    g.FillEllipse(b, 0, 0, 31, 31);
                using (Font f = new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel))
                using (StringFormat fmt = new StringFormat())
                {
                    fmt.Alignment = StringAlignment.Center;
                    fmt.LineAlignment = StringAlignment.Center;
                    // U+0E01 is Thai "ko kai", the letter sharing the "d" key.
                    // Written as an escape so every source file stays ASCII and
                    // cannot be mangled by the compiler's codepage guess.
                    g.DrawString("\u0E01", f, Brushes.White, new RectangleF(0, 0, 32, 32), fmt);
                }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }

    /// Modifier checkboxes and a key list, rather than "press the combination you
    /// want": capturing raw keystrokes cannot see Win+Space, because the shell
    /// swallows it before any dialog gets a look at it.
    internal sealed class HotkeyDialog : Form
    {
        private readonly Dictionary<string, CheckBox> _boxes = new Dictionary<string, CheckBox>();
        private readonly ComboBox _key = new ComboBox();
        private readonly Label _preview = new Label();
        private readonly Label _note = new Label();
        private readonly Button _save = new Button();

        public string Chosen { get; private set; }

        public HotkeyDialog(string current)
        {
            Text = "Keyboard Language Fixer - hotkey";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            // The program lives in the tray and is never the foreground process,
            // so Windows refuses its SetForegroundWindow calls; without TopMost
            // this dialog can open behind whatever the user was looking at.
            TopMost = true;
            ClientSize = new Size(400, 232);
            Font = new Font("Segoe UI", 9);
            try { Icon = Branding.TrayIcon(); }
            catch { }

            Label intro = new Label();
            intro.Text = "Select text, press this combination, and it gets converted.";
            intro.SetBounds(14, 12, 372, 20);
            Controls.Add(intro);

            GroupBox group = new GroupBox();
            group.Text = "Modifiers";
            group.SetBounds(14, 36, 372, 60);
            Controls.Add(group);

            int x = 14;
            foreach (string name in new string[] { "Ctrl", "Alt", "Shift", "Win" })
            {
                CheckBox cb = new CheckBox();
                cb.Text = name;
                cb.SetBounds(x, 24, 66, 22);
                cb.CheckedChanged += delegate { Rebuild(); };
                group.Controls.Add(cb);
                _boxes[name] = cb;
                x += 76;
            }

            Label keyLabel = new Label();
            keyLabel.Text = "Key";
            keyLabel.SetBounds(14, 108, 30, 20);
            Controls.Add(keyLabel);

            _key.DropDownStyle = ComboBoxStyle.DropDownList;
            _key.SetBounds(48, 104, 160, 24);
            foreach (string k in KeyChoices()) _key.Items.Add(k);
            _key.SelectedIndexChanged += delegate { Rebuild(); };
            Controls.Add(_key);

            _preview.SetBounds(14, 140, 372, 26);
            _preview.Font = new Font("Segoe UI", 12, FontStyle.Bold);
            Controls.Add(_preview);

            _note.SetBounds(14, 166, 372, 30);
            _note.ForeColor = Color.DimGray;
            Controls.Add(_note);

            _save.Text = "Save";
            _save.SetBounds(214, 196, 84, 26);
            _save.DialogResult = DialogResult.OK;
            Controls.Add(_save);
            AcceptButton = _save;

            Button cancel = new Button();
            cancel.Text = "Cancel";
            cancel.SetBounds(302, 196, 84, 26);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);
            CancelButton = cancel;

            // Pre-fill from whatever is in use right now.
            try
            {
                HotkeySpec cur = HotkeySpec.Parse(current);
                _boxes["Ctrl"].Checked = cur.Ctrl;
                _boxes["Alt"].Checked = cur.Alt;
                _boxes["Shift"].Checked = cur.Shift;
                _boxes["Win"].Checked = cur.Win;
                int idx = _key.Items.IndexOf(cur.KeyName);
                _key.SelectedIndex = idx >= 0 ? idx : 0;
            }
            catch { _key.SelectedIndex = 0; }

            Rebuild();
        }

        private static List<string> KeyChoices()
        {
            List<string> keys = new List<string>(new string[] {
                "Space", "Enter", "Tab", "Back", "Escape", "Insert", "Delete",
                "Home", "End", "PageUp", "PageDown", "Pause", "CapsLock",
                "Left", "Right", "Up", "Down"
            });
            for (char c = 'A'; c <= 'Z'; c++) keys.Add(c.ToString());
            for (int d = 0; d <= 9; d++) keys.Add("D" + d);
            for (int f = 1; f <= 12; f++) keys.Add("F" + f);
            keys.AddRange(new string[] {
                "Oem1", "Oem2", "Oem3", "Oem4", "Oem5", "Oem6", "Oem7",
                "OemMinus", "Oemplus", "Oemcomma", "OemPeriod"
            });
            return keys;
        }

        private void Rebuild()
        {
            List<string> parts = new List<string>();
            foreach (string name in new string[] { "Ctrl", "Alt", "Shift", "Win" })
                if (_boxes[name].Checked) parts.Add(name);

            bool hasKey = _key.SelectedItem != null;
            if (hasKey) parts.Add(_key.SelectedItem.ToString());
            Chosen = string.Join("+", parts.ToArray());
            _preview.Text = Chosen;

            if (!hasKey) { _note.Text = ""; _save.Enabled = false; return; }
            if (_boxes["Win"].Checked)
                _note.Text = "Windows keeps this combination, so it can only ever act on text you have " +
                             "selected. A key of its own also fixes the last word you typed.";
            else if (parts.Count < 2)
                _note.Text = "Pick at least one modifier, or this will fire on ordinary typing.";
            else
                _note.Text = "Taken over completely: one press fixes the text.";
            _save.Enabled = _boxes["Win"].Checked || parts.Count >= 2;
        }
    }
}
