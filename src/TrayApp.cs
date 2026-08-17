// ---------------------------------------------------------------------------
//  The resident application: tray icon, menu, trigger registration, and the
//  loop that turns a hotkey press into a conversion.
// ---------------------------------------------------------------------------
using System;
using System.Globalization;
using System.IO;
using System.Threading;
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
        private ToolStripMenuItem _langKeyItem, _caseKeyItem;
        private HotkeySpec _hotkey;
        private HotkeySpec _quitHotkey;
        private bool _hotkeyClaimed, _quitClaimed;
        private bool _busy;
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
            Watcher.Unwatch(MessageWindow.WM_TRIGGER);
            if (_hotkeyClaimed)
            {
                Native.UnregisterHotKey(_window.Handle, MessageWindow.HOTKEY_ID_CONVERT);
                _hotkeyClaimed = false;
            }
        }

        /// Starts or stops watching one of the two keys that belong to Windows.
        ///
        /// Neither can be claimed with RegisterHotKey: claiming Win+Space would
        /// take the language switch away from the user, and claiming Caps Lock
        /// would stop it toggling at all. Both are therefore watched with a
        /// pass-through hook that consumes nothing, and both are held to the
        /// rule that makes sharing safe -- they act only on a real selection.
        /// Returns null on success, or why it could not be done.
        private string WatchShared(uint message, string spec, out HotkeySpec parsed)
        {
            parsed = null;
            if (string.IsNullOrEmpty(spec) || spec.Trim().Length == 0)
            {
                Watcher.Unwatch(message);
                return null;
            }
            HotkeySpec hk;
            try { hk = HotkeySpec.Parse(spec); }
            catch (Exception ex) { Watcher.Unwatch(message); return ex.Message; }

            if (!Watcher.Watch(_window.Handle, message, hk.Vk, hk.Ctrl, hk.Alt, hk.Shift, hk.Win, false))
                return "Windows refused to install the keyboard hook.";
            parsed = hk;
            return null;
        }

        private HotkeySpec _langKey, _caseKey;

        private void ApplySharedKeys()
        {
            string problem = WatchShared(MessageWindow.WM_TRIGGER_LANG, _settings.LangKey, out _langKey);
            if (problem != null)
            {
                Log("could not watch '" + _settings.LangKey + "': " + problem);
                _settings.LangKey = "";
            }
            problem = WatchShared(MessageWindow.WM_TRIGGER_CASE, _settings.CaseKey, out _caseKey);
            if (problem != null)
            {
                Log("could not watch '" + _settings.CaseKey + "': " + problem);
                _settings.CaseKey = "";
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
                if (!Watcher.Watch(_window.Handle, MessageWindow.WM_TRIGGER, hk.Vk,
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
            get
            {
                return (_hotkeyClaimed ? "RegisterHotKey (exclusive)" : "keyboard hook (pass-through)") +
                       (Watcher.Installed ? " + hook for " + SharedKeyList : "");
            }
        }

        private string SharedKeyList
        {
            get
            {
                string s = _langKey != null ? _langKey.Display : "";
                if (_caseKey != null) s += (s.Length > 0 ? ", " : "") + _caseKey.Display;
                return s.Length > 0 ? s : "nothing";
            }
        }

        // -------------------------------------------------------------------
        //  Startup
        // -------------------------------------------------------------------
        public void Run()
        {
            _window = new MessageWindow();
            _window.Trigger += OnTrigger;
            _window.Finished += OnFinished;
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

            ApplySharedKeys();

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
            Console.WriteLine("With a selection: " +
                              (_langKey != null ? _langKey.Display + " converts, " : "") +
                              (_caseKey != null ? _caseKey.Display + " swaps case, " : "") +
                              _hotkey.Display + " converts");
            Console.WriteLine("Quit: " + _quitHotkey.Display +
                              (_quitClaimed ? "" : " (unavailable - use the tray icon)") +
                              (_settings.IgnoreApps.Length > 0
                                 ? "   Ignoring: " + string.Join(", ", _settings.IgnoreApps) : ""));

            Log("started, hotkey " + _hotkey.Display + ", mode " + ModeDescription +
                ", smart selection " + (_settings.SmartSelection ? "on" : "off") +
                ", max " + _settings.MaxSmartWords + " word(s)" +
                ", spell check " + (_judge != null ? "on" : "unavailable") +
                // Noticing that the user has carried on typing needs the hook,
                // which is only seated while one of the shared keys is watched.
                // With both turned off a fix runs to completion regardless.
                ", abort-on-typing " + (Watcher.Installed ? "on" : "off (no shared key watched)"));

            Application.Run(new ApplicationContext());
        }

        public string Gesture
        {
            get { return _hotkey.Display; }
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

            // The two keys that belong to Windows. Both are listed by name so it
            // is obvious what is being shared, and both only ever act on a
            // selection -- which is the whole reason sharing them is safe.
            _langKeyItem = new ToolStripMenuItem("Win+Space converts a selection too");
            _langKeyItem.Checked = _langKey != null;
            _langKeyItem.Click += delegate { ToggleSharedKey(true); };
            menu.Items.Add(_langKeyItem);

            _caseKeyItem = new ToolStripMenuItem("Caps Lock swaps the case of a selection");
            _caseKeyItem.Checked = _caseKey != null;
            _caseKeyItem.Click += delegate { ToggleSharedKey(false); };
            menu.Items.Add(_caseKeyItem);

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
                "Ready. Press " + Gesture + " to fix the last word, or select text and press " +
                (_langKey != null ? _langKey.Display : Gesture) +
                (_caseKey != null ? " - or " + _caseKey.Display + " to swap its case." : "."),
                ToolTipIcon.Info);
        }

        /// Turns Win+Space or Caps Lock on or off as a second trigger. Turning
        /// one off restores it to being nothing but the key Windows made it.
        private void ToggleSharedKey(bool lang)
        {
            string dflt = lang ? "Win+Space" : "CapsLock";
            string current = lang ? _settings.LangKey : _settings.CaseKey;
            string wanted = string.IsNullOrEmpty(current) ? dflt : "";

            if (lang) _settings.LangKey = wanted; else _settings.CaseKey = wanted;
            ApplySharedKeys();

            // ApplySharedKeys clears the setting if Windows refused, so read back
            // what actually happened rather than what was asked for.
            if (lang) _saved.LangKey = _settings.LangKey; else _saved.CaseKey = _settings.CaseKey;
            _langKeyItem.Checked = _langKey != null;
            _caseKeyItem.Checked = _caseKey != null;
            SaveSettings();
            Log((lang ? "Win+Space" : "Caps Lock") + " trigger " +
                ((lang ? _langKey : _caseKey) != null ? "on" : "off"));
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
        /// A press arrived. The work it starts happens on a thread of its own.
        ///
        /// It used to run right here, and that was the single worst thing this
        /// program did to the machine it lives on. A fix spends the best part of
        /// a second waiting on the clipboard, on the language flyout and on the
        /// application it is driving -- and the low-level keyboard hook is
        /// dispatched on THIS thread, so for that whole second every key the
        /// user pressed sat waiting for a hook that could not answer. Windows
        /// gives a hook LowLevelHooksTimeout (300 ms by default) before it gives
        /// up and lets the key through, so keys arrived late, in the wrong order,
        /// and interleaved with the keystrokes the fixer was injecting. Typing
        /// straight after a language switch came out scrambled.
        ///
        /// Handing the work to a worker leaves this thread free to answer the
        /// hook within microseconds, which is all it ever needed to do.
        private void OnTrigger(object sender, TriggerEventArgs e)
        {
            // A stray re-entry would fight over the clipboard, so triggers that
            // arrive mid-fix are ignored. Holding Caps Lock down long enough to
            // auto-repeat lands here.
            if (_busy) return;
            _busy = true;

            FixMode mode = e.Mode;
            int baseline = e.TypedBaseline;
            int startedAt = Environment.TickCount;
            Thread worker = new Thread(delegate()
            {
                FixOutcome outcome = FixOutcome.Failed;
                try
                {
                    Fixer fixer = new Fixer(_layouts, _settings, Log, _judge, _undo);
                    outcome = fixer.Run(mode, baseline);
                }
                catch (Exception ex)
                {
                    Log("convert failed: " + ex.Message);
                }
                finally
                {
                    // Everything left to do belongs to the message loop's
                    // thread: the queued presses are in its queue and the hook
                    // is owned by it. The result rides along on the message so
                    // no field is shared between the two threads.
                    Native.PostMessage(_window.Handle, MessageWindow.WM_DONE,
                                       new IntPtr((int)outcome),
                                       new IntPtr(unchecked(Environment.TickCount - startedAt)));
                }
            });
            worker.IsBackground = true;
            // System.Windows.Forms.Clipboard refuses to run on anything else.
            worker.SetApartmentState(ApartmentState.STA);
            worker.Name = "KbFix fix";
            worker.Start();
        }

        /// The worker has finished. Runs on the message loop's own thread.
        private void OnFinished(object sender, FinishedEventArgs e)
        {
            // Presses that arrived during the fix are dropped rather than
            // replayed: by now the selection they were aimed at has already been
            // replaced, so acting on them would convert the conversion.
            int queued = Native.DrainMessages(_window.Handle, MessageWindow.WM_TRIGGER) +
                         Native.DrainMessages(_window.Handle, MessageWindow.WM_TRIGGER_LANG) +
                         Native.DrainMessages(_window.Handle, MessageWindow.WM_TRIGGER_CASE);
            if (queued > 0) Log("  discarded " + queued + " press(es) that arrived while busy");

            // Windows silently stops calling a hook that once took too long to
            // return. This thread no longer blocks, so that should not happen at
            // all any more -- but a dropped hook is silent and the re-seat costs
            // microseconds, so it stays as insurance.
            if (Watcher.Installed && !Watcher.Reinstall())
            {
                Log("hook re-seat failed, retrying");
                if (!Watcher.Reinstall())
                {
                    Log("hook could not be re-seated; Win+Space and Caps Lock are no longer watched");
                    if (_tray != null)
                        _tray.ShowBalloonTip(5000, "Keyboard Language Fixer",
                            "Windows dropped the keyboard hook. Quit and start the program again.",
                            ToolTipIcon.Warning);
                }
            }
            _busy = false;

            // Logged last, and from this thread, so the line means exactly one
            // thing: the program is idle again and will act on the next press.
            // Anything watching the log -- the end-to-end suite does -- can wait
            // for this instead of guessing at a sleep long enough to cover it.
            Log("done: " + e.Outcome + " in " + e.ElapsedMs + " ms");
        }

        public void Dispose()
        {
            Unregister();
            Watcher.Unwatch(MessageWindow.WM_TRIGGER_LANG);
            Watcher.Unwatch(MessageWindow.WM_TRIGGER_CASE);
            Watcher.Uninstall();
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
