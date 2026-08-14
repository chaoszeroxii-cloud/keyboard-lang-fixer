// ---------------------------------------------------------------------------
//  The resident application: tray icon, menu, trigger registration, and the
//  loop that turns a hotkey press into a conversion.
// ---------------------------------------------------------------------------
using System;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace KbFix
{
    internal sealed class TrayApp : IDisposable
    {
        private readonly Settings _settings;
        private readonly Layout[] _layouts;
        private readonly bool _usedBuiltIn;
        private readonly Options _options;

        private MessageWindow _window;
        private NotifyIcon _tray;
        private ToolStripItem _header;
        private ToolStripMenuItem _smartItem, _langItem, _startupItem;
        private HotkeySpec _hotkey;
        private HotkeySpec _quitHotkey;
        private bool _hotkeyClaimed, _quitClaimed;
        private bool _busy;

        public TrayApp(Settings settings, Layout[] layouts, bool usedBuiltIn, Options options)
        {
            _settings = settings;
            _layouts = layouts;
            _usedBuiltIn = usedBuiltIn;
            _options = options;
        }

        // -------------------------------------------------------------------
        //  Logging
        // -------------------------------------------------------------------
        private void Log(string message)
        {
            if (string.IsNullOrEmpty(_options.LogPath)) return;
            try
            {
                File.AppendAllText(_options.LogPath,
                    DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + message +
                    Environment.NewLine);
            }
            catch { }
        }

        // -------------------------------------------------------------------
        //  Trigger registration
        // -------------------------------------------------------------------
        private void Unregister()
        {
            Watcher.Uninstall();
            if (_hotkeyClaimed)
            {
                Native.UnregisterHotKey(_window.Handle, MessageWindow.HOTKEY_ID_CONVERT);
                _hotkeyClaimed = false;
            }
        }

        /// Win+<key> belongs to the Windows shell and cannot be claimed with
        /// RegisterHotKey, so those are watched with a pass-through hook and the
        /// native language switch keeps happening. Anything else is claimed
        /// exclusively. Returns null on success, or why it could not be done.
        private string Register(string spec)
        {
            HotkeySpec hk;
            try { hk = HotkeySpec.Parse(spec); }
            catch (Exception ex) { return ex.Message; }

            bool useHook;
            string mode = string.IsNullOrEmpty(_options.Mode) ? "auto" : _options.Mode.ToLowerInvariant();
            if (mode == "hook") useHook = true;
            else if (mode == "hotkey") useHook = false;
            else useHook = hk.Win;

            Unregister();
            if (useHook)
            {
                if (!Watcher.Install(_window.Handle, MessageWindow.WM_TRIGGER, hk.Vk,
                                     hk.Ctrl, hk.Alt, hk.Shift, hk.Win, false))
                    return "Windows refused to install the keyboard hook.";
            }
            else
            {
                if (!Native.RegisterHotKey(_window.Handle, MessageWindow.HOTKEY_ID_CONVERT, hk.Mods, (uint)hk.Vk))
                    return "'" + spec + "' is already taken by another program.";
                _hotkeyClaimed = true;
            }
            _hotkey = hk;
            return null;
        }

        public string ModeDescription
        {
            get { return Watcher.Installed ? "keyboard hook (pass-through)" : "RegisterHotKey (exclusive)"; }
        }

        // -------------------------------------------------------------------
        //  Startup
        // -------------------------------------------------------------------
        public void Run()
        {
            _window = new MessageWindow();
            _window.Trigger += OnTrigger;
            _window.QuitRequested += delegate { Application.ExitThread(); };

            string problem = Register(_settings.Hotkey);
            if (problem != null)
            {
                // A saved hotkey that no longer works must not leave the tool
                // dead; fall back to the default and say so.
                string fallback = "Win+Space";
                string second = _settings.Hotkey == fallback ? problem : Register(fallback);
                if (second != null) throw new Exception("Could not listen for '" + _settings.Hotkey + "': " + problem);
                MessageBox.Show("Could not use " + _settings.Hotkey + ":\r\n" + problem +
                                "\r\n\r\nFalling back to " + fallback + ".",
                                "Keyboard Language Fixer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _settings.Hotkey = fallback;
            }

            try { _quitHotkey = HotkeySpec.Parse(string.IsNullOrEmpty(_options.QuitHotkey)
                                                ? "Ctrl+Alt+Shift+X" : _options.QuitHotkey); }
            catch { _quitHotkey = HotkeySpec.Parse("Ctrl+Alt+Shift+X"); }
            _quitClaimed = Native.RegisterHotKey(_window.Handle, MessageWindow.HOTKEY_ID_QUIT,
                                                 _quitHotkey.Mods, (uint)_quitHotkey.Vk);

            if (!_options.NoTray) BuildTray();

            Console.WriteLine(HeaderText());
            Program.PrintLayouts(_layouts, _usedBuiltIn);
            Console.WriteLine("Mode: " + ModeDescription +
                              "   Smart selection: " + (_settings.SmartSelection ? "on" : "off") +
                              "   Quit: " + _quitHotkey.Display +
                              (_quitClaimed ? "" : " (unavailable - use the tray icon)"));

            Log("started, hotkey " + _hotkey.Display + ", mode " + ModeDescription +
                ", smart selection " + (_settings.SmartSelection ? "on" : "off"));

            Application.Run(new ApplicationContext());
        }

        private string HeaderText()
        {
            return "Keyboard Language Fixer  -  select text + " + _hotkey.Display;
        }

        private void BuildTray()
        {
            _tray = new NotifyIcon();
            _tray.Icon = Branding.TrayIcon();
            _tray.Visible = true;

            ContextMenuStrip menu = new ContextMenuStrip();
            _header = menu.Items.Add("");
            _header.Enabled = false;
            menu.Items.Add(new ToolStripSeparator());

            ToolStripItem hotkeyItem = menu.Items.Add("Change hotkey...");
            hotkeyItem.Click += OnChangeHotkey;

            _smartItem = new ToolStripMenuItem("Fix the last word when nothing is selected");
            _smartItem.Checked = _settings.SmartSelection;
            _smartItem.Click += delegate
            {
                _settings.SmartSelection = !_settings.SmartSelection;
                _smartItem.Checked = _settings.SmartSelection;
                SaveSettings();
            };
            menu.Items.Add(_smartItem);

            _langItem = new ToolStripMenuItem("Switch input language after converting");
            _langItem.Checked = _settings.SwitchLanguage;
            _langItem.Click += delegate
            {
                _settings.SwitchLanguage = !_settings.SwitchLanguage;
                _langItem.Checked = _settings.SwitchLanguage;
                SaveSettings();
            };
            menu.Items.Add(_langItem);

            _startupItem = new ToolStripMenuItem("Start with Windows");
            _startupItem.Checked = Startup.Installed;
            _startupItem.Click += delegate
            {
                string why;
                bool ok = Startup.Installed ? Startup.Uninstall(out why)
                                            : Startup.Install(Branding.ExePath, out why);
                if (!ok)
                    MessageBox.Show("Could not change the startup shortcut:\r\n" + why,
                                    "Keyboard Language Fixer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _startupItem.Checked = Startup.Installed;
            };
            menu.Items.Add(_startupItem);

            menu.Items.Add(new ToolStripSeparator());
            ToolStripItem exit = menu.Items.Add("Exit");
            exit.Click += delegate { Application.ExitThread(); };

            _tray.ContextMenuStrip = menu;
            UpdateTrayText();

            _tray.ShowBalloonTip(3000, "Keyboard Language Fixer",
                "Ready. " + _hotkey.Display + " still changes language; with text selected it also fixes the layout.",
                ToolTipIcon.Info);
        }

        private void UpdateTrayText()
        {
            string text = HeaderText();
            if (_tray != null)
            {
                // NotifyIcon.Text throws above 63 characters, and a long hotkey
                // name can reach that.
                _tray.Text = text.Length > 63 ? text.Substring(0, 60) + "..." : text;
            }
            if (_header != null) _header.Text = text;
        }

        private void SaveSettings()
        {
            string problem;
            if (!_settings.Save(Branding.AppFolder, out problem))
                MessageBox.Show("Could not save settings:\r\n" + problem, "Keyboard Language Fixer",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void OnChangeHotkey(object sender, EventArgs e)
        {
            string previous = _hotkey.Display;
            string chosen;
            using (HotkeyDialog dlg = new HotkeyDialog(previous))
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;
                chosen = dlg.Chosen;
            }
            if (string.IsNullOrEmpty(chosen) || chosen == previous) return;

            string problem = Register(chosen);
            if (problem != null)
            {
                MessageBox.Show("Could not use " + chosen + ":\r\n" + problem +
                                "\r\n\r\nKeeping " + previous + ".",
                                "Keyboard Language Fixer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Register(previous);          // put the working one back
                return;
            }
            _settings.Hotkey = chosen;
            SaveSettings();
            UpdateTrayText();
            Log("hotkey changed to " + chosen);
        }

        // -------------------------------------------------------------------
        //  The trigger
        // -------------------------------------------------------------------
        private void OnTrigger(object sender, EventArgs e)
        {
            // Converting pumps no messages, but a stray re-entry would fight
            // over the clipboard, so ignore triggers that arrive mid-conversion.
            if (_busy) return;
            _busy = true;
            try
            {
                Fixer fixer = new Fixer(_layouts, _settings, Log);
                fixer.Run();
            }
            catch (Exception ex)
            {
                Log("convert failed: " + ex.Message);
            }
            finally
            {
                Native.DrainMessages(_window.Handle, MessageWindow.WM_TRIGGER);
                // Converting blocked the loop long enough that Windows may have
                // started skipping the hook. Re-seat it.
                Watcher.Reinstall();
                _busy = false;
            }
        }

        public void Dispose()
        {
            Unregister();
            if (_window != null && _quitClaimed)
                Native.UnregisterHotKey(_window.Handle, MessageWindow.HOTKEY_ID_QUIT);
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
            if (_window != null)
            {
                _window.DestroyHandle();
                _window = null;
            }
        }
    }
}
