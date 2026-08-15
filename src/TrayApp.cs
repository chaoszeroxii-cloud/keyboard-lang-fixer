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
        /// The values in use, including command-line overrides and runtime
        /// fallbacks. Never written to disk.
        private readonly Settings _settings;
        /// The values the user chose, and the only ones that get saved.
        private readonly Settings _saved;
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
        private int _lastTriggerTick;
        private bool _hookWanted;
        private bool _pendingSecondPress;
        private IWordJudge _judge;
        private readonly UndoMemo _undo = new UndoMemo();

        public TrayApp(Settings settings, Settings saved, Layout[] layouts, bool usedBuiltIn, Options options)
        {
            _settings = settings;
            _saved = saved;
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
                _hookWanted = true;
            }
            else
            {
                if (!Native.RegisterHotKey(_window.Handle, MessageWindow.HOTKEY_ID_CONVERT, hk.Mods, (uint)hk.Vk))
                    return "'" + spec + "' is already taken by another program.";
                _hotkeyClaimed = true;
                _hookWanted = false;
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
                string fallback = "Ctrl+Alt+Space";
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

            // Without a dictionary there is no way to tell where a mistyped run
            // ends, so the walk falls back to a single word rather than guessing.
            if (_settings.UseSpellCheck)
            {
                SpellWordJudge spell = SpellWordJudge.TryCreate();
                if (spell != null) _judge = spell;
            }
            if (_judge == null && _settings.MaxSmartWords > 1)
            {
                Log("no spell checker available, limiting Smart Selection to one word");
                _settings.MaxSmartWords = 1;
            }

            if (!_options.NoTray) BuildTray();

            Console.WriteLine(HeaderText());
            Program.PrintLayouts(_layouts, _usedBuiltIn);
            Console.WriteLine("Mode: " + ModeDescription +
                              "   Smart selection: " + (_settings.SmartSelection ? "on" : "off") +
                              " (" + _settings.MaxSmartWords + " word max)" +
                              "   Spell check: " + (_judge != null ? "on" : "unavailable") +
                              "   Undo window: " + _settings.UndoWindowSeconds + "s");
            Console.WriteLine("Quit: " + _quitHotkey.Display +
                              (_quitClaimed ? "" : " (unavailable - use the tray icon)") +
                              (_settings.IgnoreApps.Length > 0
                                 ? "   Ignoring: " + string.Join(", ", _settings.IgnoreApps) : ""));

            Log("started, hotkey " + _hotkey.Display + ", mode " + ModeDescription +
                ", smart selection " + (_settings.SmartSelection ? "on" : "off") +
                ", max " + _settings.MaxSmartWords + " word(s)" +
                ", spell check " + (_judge != null ? "on" : "unavailable"));

            Application.Run(new ApplicationContext());
        }

        public string Gesture
        {
            get { return _hookWanted ? _hotkey.Display + " twice" : _hotkey.Display; }
        }

        private string HeaderText()
        {
            return "Keyboard Language Fixer  -  " + Gesture;
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
                _saved.SmartSelection = _settings.SmartSelection;
                _smartItem.Checked = _settings.SmartSelection;
                SaveSettings();
            };
            menu.Items.Add(_smartItem);

            _langItem = new ToolStripMenuItem("Switch input language after converting");
            _langItem.Checked = _settings.SwitchLanguage;
            _langItem.Click += delegate
            {
                _settings.SwitchLanguage = !_settings.SwitchLanguage;
                _saved.SwitchLanguage = _settings.SwitchLanguage;
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
                "Ready. Press " + Gesture + " to fix the last word, or select text first." +
                (_hookWanted ? " Win+Space on its own still just changes language." : ""),
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
            if (!_saved.Save(Branding.AppFolder, out problem))
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
                string rollback = Register(previous);
                if (rollback != null)
                {
                    // Register() unregisters first, so a failed rollback would
                    // leave nothing listening at all.
                    Log("rollback to " + previous + " also failed: " + rollback);
                    MessageBox.Show("The previous hotkey could not be restored either:\r\n" + rollback +
                                    "\r\n\r\nQuit and start the program again.",
                                    "Keyboard Language Fixer", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return;
            }
            _settings.Hotkey = chosen;
            _saved.Hotkey = chosen;
            SaveSettings();
            UpdateTrayText();
            Log("hotkey changed to " + chosen);
        }

        // -------------------------------------------------------------------
        //  The trigger
        // -------------------------------------------------------------------
        /// How close together two presses have to be to count as one gesture.
        ///
        /// The system double-click time is the user's own stated timing, but the
        /// floor is higher than a mouse double-click on purpose: the first press
        /// spends about half a second probing for a selection, and a person who
        /// releases the Win key between presses is easily slower than 500 ms.
        /// Being generous costs nothing at all: a single press returns without
        /// touching anything, so a window that is too wide can only ever mean a
        /// deliberate second press is honoured.
        private int DoublePressWindowMs
        {
            get
            {
                if (_settings.DoublePressMs > 0) return _settings.DoublePressMs;
                int systemMs = Native.GetDoubleClickTime();
                if (systemMs < 900) return 900;
                if (systemMs > 1500) return 1500;
                return systemMs;
            }
        }

        private void OnTrigger(object sender, EventArgs e)
        {
            // Converting pumps no messages, but a stray re-entry would fight
            // over the clipboard, so ignore triggers that arrive mid-conversion.
            if (_busy) return;
            _busy = true;

            // A combination this program owns outright is unambiguous: pressing
            // it can only mean "fix this", so one press acts. The two-press
            // gesture exists solely for a hotkey shared with Windows, where a
            // single press has to stay inert because it also switches language.
            bool secondPress;
            if (!_hookWanted)
            {
                secondPress = true;
                _lastTriggerTick = 0;
                _pendingSecondPress = false;
            }
            else if (_pendingSecondPress)
            {
                // The user pressed again while the previous press was still
                // working. That press could not be handled then, because the
                // message loop was blocked, so it is being honoured now.
                secondPress = true;
                _pendingSecondPress = false;
            }
            else
            {
                int now = Environment.TickCount;
                secondPress = _lastTriggerTick != 0 &&
                              unchecked(now - _lastTriggerTick) >= 0 &&
                              unchecked(now - _lastTriggerTick) <= DoublePressWindowMs;
                // A press that completed a gesture must not also start one, or a
                // third press would keep acting on the document.
                _lastTriggerTick = secondPress ? 0 : now;
            }

            try
            {
                Fixer fixer = new Fixer(_layouts, _settings, Log, _judge, _undo);
                fixer.Run(secondPress);
            }
            catch (Exception ex)
            {
                Log("convert failed: " + ex.Message);
            }
            finally
            {
                // A conversion takes about a second, and the whole point of the
                // double press is that the two come close together -- so the
                // second one almost always arrives while the loop is blocked and
                // is sitting in the queue right now. Keep exactly one of those
                // as the second half of the gesture and discard any repeats.
                int queued = Native.DrainMessages(_window.Handle, MessageWindow.WM_TRIGGER);
                if (queued > 0) Log("  " + queued + " press(es) arrived while busy");
                if (queued > 0 && !secondPress)
                {
                    _pendingSecondPress = true;
                    _lastTriggerTick = 0;
                    Native.PostMessage(_window.Handle, MessageWindow.WM_TRIGGER, IntPtr.Zero, IntPtr.Zero);
                }

                // Converting blocks the loop long enough that Windows may have
                // started skipping the hook, so it is re-seated afterwards -- but
                // only then. Re-seating means unhooking and hooking again, and a
                // key pressed inside that gap is not seen at all: doing it after
                // every press swallowed the second half of the double press,
                // which is precisely the key this program needs to catch.
                if (secondPress && _hookWanted && !Watcher.Reinstall())
                {
                    Log("hook re-seat failed, retrying");
                    if (!Watcher.Reinstall())
                    {
                        Log("hook could not be re-seated; the hotkey is dead");
                        if (_tray != null)
                            _tray.ShowBalloonTip(5000, "Keyboard Language Fixer",
                                "Windows dropped the keyboard hook. Quit and start the program again.",
                                ToolTipIcon.Warning);
                    }
                }
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
