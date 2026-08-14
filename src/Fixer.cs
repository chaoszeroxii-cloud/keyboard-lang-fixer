// ---------------------------------------------------------------------------
//  What happens when the hotkey fires: read the selection (or work out one),
//  convert it, paste it back, and leave the input language matching the result.
// ---------------------------------------------------------------------------
using System;
using System.Globalization;
using System.Media;
using System.Threading;
using System.Windows.Forms;

namespace KbFix
{
    internal enum FixOutcome
    {
        /// Nothing was selected and nothing could be worked out: the plain
        /// language switch Windows already performed is the only effect.
        NoSelection,
        Converted,
        /// Something was read but converting it would have been a guess.
        Declined,
        Failed
    }

    internal sealed class Fixer
    {
        /// Window classes where Ctrl+C means "interrupt the running program"
        /// rather than "copy".
        private static readonly string[] ConsoleClasses = new string[] {
            "ConsoleWindowClass",             // conhost / cmd / classic PowerShell
            "CASCADIA_HOSTING_WINDOW_CLASS",  // Windows Terminal
            "VirtualConsoleClass",            // ConEmu / Cmder
            "mintty",                         // Git Bash / MSYS2
            "PuTTY"
        };

        private static readonly UIntPtr Sig = new UIntPtr(Native.SIGNATURE);

        private readonly Layout[] _layouts;
        private readonly Settings _settings;
        private readonly Action<string> _log;
        private readonly IWordJudge _judge;
        private readonly UndoMemo _undo;

        public Fixer(Layout[] layouts, Settings settings, Action<string> log, IWordJudge judge, UndoMemo undo)
        {
            _layouts = layouts;
            _settings = settings;
            _log = log != null ? log : delegate(string s) { };
            _judge = judge;
            _undo = undo;
        }

        // -------------------------------------------------------------------
        //  Keyboard plumbing
        // -------------------------------------------------------------------
        private static void Key(int vk, bool down)
        {
            uint flags = down ? 0u : Native.KEYEVENTF_KEYUP;
            if (Native.IsExtendedKey(vk)) flags |= Native.KEYEVENTF_EXTENDEDKEY;
            Native.keybd_event((byte)vk, 0, flags, Sig);
        }

        private static void Combo(int modVk, int vk)
        {
            Key(modVk, true); Key(vk, true);
            Thread.Sleep(15);
            Key(vk, false); Key(modVk, false);
        }

        private static void Tap(int vk)
        {
            Key(vk, true); Thread.Sleep(5); Key(vk, false);
        }

        /// Repeats a modified key without a pause between presses. They queue in
        /// the target window's input queue in order, so the application applies
        /// them all before it sees the copy that follows.
        private static void ComboRepeat(int modVk, int vk, int times)
        {
            Key(modVk, true);
            for (int i = 0; i < times; i++) { Key(vk, true); Key(vk, false); }
            Key(modVk, false);
        }

        /// The trigger fires while Win (or Ctrl/Alt) is still physically held.
        /// Anything sent now would pick those modifiers up, so wait for the user
        /// to let go before sending anything.
        ///
        /// The Win key is deliberately NOT forced up. Windows commits its own
        /// Win+Space language switch when the Win key is released, and an extra
        /// synthetic release on top of the user's real one makes it drop the
        /// switch -- which breaks the one promise this tool makes, that a plain
        /// Win+Space keeps working exactly as it always did. Ctrl, Alt and Shift
        /// have no such behaviour, so those are still forced up; Win is only
        /// released if it is genuinely stuck after the wait.
        private static void ClearModifiers()
        {
            int[] watched = new int[] { Native.VK_CONTROL, Native.VK_MENU, Native.VK_SHIFT,
                                        Native.VK_LWIN, Native.VK_RWIN };
            int waited = 0;
            while (waited < 900)
            {
                bool anyDown = false;
                foreach (int vk in watched) if (Native.IsDown(vk)) { anyDown = true; break; }
                if (!anyDown) break;
                Thread.Sleep(15);
                waited += 15;
            }
            Key(Native.VK_CONTROL, false);
            Key(Native.VK_MENU, false);
            Key(Native.VK_SHIFT, false);
            if (Native.IsDown(Native.VK_LWIN)) Key(Native.VK_LWIN, false);
            if (Native.IsDown(Native.VK_RWIN)) Key(Native.VK_RWIN, false);
            Thread.Sleep(20);
        }

        /// Copies the current selection, if there is one, without disturbing the
        /// clipboard when there is not.
        ///
        /// Ctrl+Insert is tried first because in a console window Ctrl+C would
        /// interrupt the running program instead of copying. Ctrl+C is only a
        /// fallback, and only outside consoles, for the few applications that
        /// ignore Ctrl+Insert.
        private string CopySelection(bool isConsole)
        {
            uint seq = Native.GetClipboardSequenceNumber();
            Combo(Native.VK_CONTROL, Native.VK_INSERT);
            if (ClipboardSafe.WaitForWrite(seq, 260))
            {
                _log("  copy: Ctrl+Insert");
                return ClipboardSafe.GetText();
            }
            if (isConsole) return null;

            Combo(Native.VK_CONTROL, Native.VK_C);
            if (ClipboardSafe.WaitForWrite(seq, 320))
            {
                _log("  copy: Ctrl+C");
                return ClipboardSafe.GetText();
            }
            return null;
        }

        private static void Paste(bool isConsole)
        {
            if (isConsole) Combo(Native.VK_SHIFT, Native.VK_INSERT);
            else Combo(Native.VK_CONTROL, Native.VK_V);
        }

        /// Shrinks a leftward selection from its left edge by moving the active
        /// end right, which leaves the anchor -- the caret the user was at --
        /// exactly where it was.
        ///
        /// Pressing Right instead would be wrong: in an edit control an arrow key
        /// moves from the ACTIVE end of the selection, and after Shift+Home that
        /// end is the start of the line, so Right lands near position 1 rather
        /// than back at the caret.
        private static void ShrinkFromLeft(int times)
        {
            if (times > 0) ComboRepeat(Native.VK_SHIFT, Native.VK_RIGHT, times);
        }

        // -------------------------------------------------------------------
        //  Input language
        // -------------------------------------------------------------------
        /// Asks the focused window to switch layout, then checks it happened.
        /// Windows commits its own Win+Space switch from the language flyout a
        /// moment after the key is released, and if that lands after this request
        /// it silently undoes it -- so the result is confirmed, re-requested if
        /// it did not stick, and confirmed again once the flyout has settled.
        private bool SetInputLanguage(int langId)
        {
            IntPtr target = IntPtr.Zero;
            foreach (IntPtr hkl in Native.InstalledLayouts())
                if ((int)(hkl.ToInt64() & 0xFFFF) == langId) { target = hkl; break; }
            if (target == IntPtr.Zero) return false;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                Native.PostMessage(Native.GetForegroundWindow(),
                                   (uint)Native.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, target);
                Thread.Sleep(120);
                if (Native.ForegroundLangId() != langId) continue;
                Thread.Sleep(220);
                if (Native.ForegroundLangId() == langId) return true;
            }
            return false;
        }

        private static void Complain()
        {
            try { SystemSounds.Hand.Play(); }
            catch { }
        }

        // -------------------------------------------------------------------
        //  The action
        // -------------------------------------------------------------------
        /// True when the program in front is on the ignore list, in which case
        /// the hotkey does nothing at all.
        ///
        /// The check lives here rather than in the hook callback on purpose. The
        /// callback runs for every keystroke on the machine and its only job is
        /// to compare one virtual-key code; asking Windows for the foreground
        /// program's name there would turn the cheapest possible test into a
        /// process query on every key the user presses.
        private bool ForegroundIsIgnored(out string appName)
        {
            appName = "";
            if (_settings.IgnoreApps == null || _settings.IgnoreApps.Length == 0) return false;
            appName = Native.ForegroundProcessName();
            if (appName.Length == 0) return false;

            string bare = appName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        ? appName.Substring(0, appName.Length - 4) : appName;
            foreach (string entry in _settings.IgnoreApps)
            {
                if (string.IsNullOrEmpty(entry)) continue;
                string want = entry.Trim();
                if (want.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    want = want.Substring(0, want.Length - 4);
                if (string.Equals(bare, want, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public FixOutcome Run()
        {
            ClearModifiers();

            string cls = Native.ForegroundWindowClass();
            bool isConsole = Array.IndexOf(ConsoleClasses, cls) >= 0;
            int caps = Native.CapsLockState();

            string ignoredApp;
            if (ForegroundIsIgnored(out ignoredApp))
            {
                _log("trigger: '" + ignoredApp + "' is on the ignore list, doing nothing");
                return FixOutcome.NoSelection;
            }

            _log("trigger: class '" + cls + "'" + (isConsole ? " (console)" : "") +
                 (caps != 0 ? ", CapsLock on" : ""));

            // Read the clipboard before touching it. This is a read only: if
            // nothing turns out to be selected, the clipboard is never written.
            ClipboardSnapshot snapshot = ClipboardSnapshot.Take();

            string selection = CopySelection(isConsole);
            bool smart = false;
            int smartSelected = 0;

            // Pressing the hotkey again straight after a conversion means "put it
            // back", so that is checked before anything is converted again.
            bool undoOffered = _undo != null && _undo.IsOffered(DateTime.UtcNow, _settings.UndoWindowSeconds);
            if (undoOffered && !string.IsNullOrEmpty(selection) && _undo.Matches(selection))
            {
                return Undo(snapshot, isConsole, selection.Length, false);
            }

            if (string.IsNullOrEmpty(selection))
            {
                if (isConsole || (!_settings.SmartSelection && !undoOffered))
                {
                    _log("  nothing selected -> language switch only");
                    return FixOutcome.NoSelection;
                }

                // Nothing is selected, so re-select what was just pasted and see
                // whether it is still there before offering to restore it.
                if (undoOffered)
                {
                    int len = _undo.Converted.Length;
                    ComboRepeat(Native.VK_SHIFT, Native.VK_LEFT, len);
                    string atCaret = CopySelection(isConsole);
                    if (_undo.Matches(atCaret)) return Undo(snapshot, isConsole, len, true);
                    ReleaseSelection(len);
                    _log("  undo not offered: the text at the caret has changed");
                }

                if (!_settings.SmartSelection)
                {
                    snapshot.Restore();
                    _log("  nothing selected -> language switch only");
                    return FixOutcome.NoSelection;
                }
                SmartResult sr = TrySmartSelection(caps, isConsole);
                if (sr == null)
                {
                    snapshot.Restore();
                    return FixOutcome.NoSelection;
                }
                selection = sr.Text;
                smartSelected = sr.SelectionLength;
                smart = true;
            }

            Layout source = Converter.SelectSource(selection, _layouts, caps);
            Layout target = source != null ? Converter.SelectTarget(source, _layouts) : null;
            if (source == null || target == null)
            {
                if (smart) ReleaseSelection(smartSelected);
                snapshot.Restore();
                _log("  read '" + Shorten(selection) + "' but no layout explains it");
                if (!smart) Complain();     // an explicit selection deserves feedback
                return FixOutcome.Declined;
            }

            string converted = Converter.Convert(selection, source, target, caps);
            _log("  '" + Shorten(selection) + "' -> '" + Shorten(converted) + "'  [" +
                 source.Name + " -> " + target.Name + "]" + (smart ? "  (smart)" : ""));

            if (converted == selection)
            {
                if (smart) ReleaseSelection(smartSelected);
                snapshot.Restore();
                _log("  nothing convertible, left alone");
                if (!smart) Complain();
                return FixOutcome.Declined;
            }

            if (!ClipboardSafe.SetText(converted))
            {
                if (smart) ReleaseSelection(smartSelected);
                snapshot.Restore();
                _log("  could not write the clipboard, aborted without pasting");
                Complain();
                return FixOutcome.Failed;
            }

            // Pasting an empty or stale clipboard would wipe the selection
            // instead of fixing it, so confirm the write landed first.
            Thread.Sleep(40);
            string staged = ClipboardSafe.GetText();
            if (staged != converted)
            {
                if (smart) ReleaseSelection(smartSelected);
                snapshot.Restore();
                _log("  clipboard write did not stick, aborted without pasting");
                Complain();
                return FixOutcome.Failed;
            }

            Paste(isConsole);
            _log("  pasted");

            if (_settings.SwitchLanguage && target.LangId != 0)
            {
                Thread.Sleep(60);
                if (SetInputLanguage(target.LangId))
                    _log("  input language set to 0x" + target.LangId.ToString("X4", CultureInfo.InvariantCulture));
                else
                    _log("  could not settle input language on 0x" +
                         target.LangId.ToString("X4", CultureInfo.InvariantCulture));
            }

            // Give the target application time to read the clipboard before
            // putting the old contents back.
            Thread.Sleep(250);
            snapshot.Restore();

            if (_undo != null) _undo.Remember(selection, converted, source.LangId);
            return FixOutcome.Converted;
        }

        /// Puts the text from before the last conversion back, exactly as it was.
        /// The caller has already confirmed that what is selected is what this
        /// tool pasted, so this only has to write and tidy up.
        private FixOutcome Undo(ClipboardSnapshot snapshot, bool isConsole, int selectionLength, bool reselected)
        {
            string original = _undo.Original;
            int langId = _undo.SourceLangId;

            if (!ClipboardSafe.SetText(original))
            {
                if (reselected) ReleaseSelection(selectionLength);
                snapshot.Restore();
                Complain();
                _log("  undo: could not write the clipboard");
                return FixOutcome.Failed;
            }
            Thread.Sleep(40);
            if (ClipboardSafe.GetText() != original)
            {
                if (reselected) ReleaseSelection(selectionLength);
                snapshot.Restore();
                Complain();
                _log("  undo: clipboard write did not stick");
                return FixOutcome.Failed;
            }

            Paste(isConsole);
            _log("  undo: restored '" + Shorten(original) + "'");

            // The language was moved to match the converted text, so put it back
            // to the one the original was typed on.
            if (_settings.SwitchLanguage && langId != 0)
            {
                Thread.Sleep(60);
                SetInputLanguage(langId);
            }

            Thread.Sleep(250);
            snapshot.Restore();

            // One undo per conversion: a second press should convert again
            // rather than bounce the text back and forth.
            _undo.Clear();
            return FixOutcome.Converted;
        }

        private sealed class SmartResult
        {
            public string Text;
            /// How many characters are selected, so a later decision to give up
            /// can put the caret back by shrinking the selection to nothing.
            public int SelectionLength;
        }

        /// Undoes a selection this class made, leaving the caret where the user
        /// left it and nothing selected.
        private static void ReleaseSelection(int selectionLength)
        {
            ShrinkFromLeft(selectionLength);
        }

        /// Nothing was selected, so work out what the user just typed.
        ///
        /// Shift+Home is used to grab the text before the caret because it is a
        /// single keystroke no application interprets ambiguously, and it cannot
        /// cross a line. The span to convert is then re-selected by CHARACTER
        /// count: Ctrl+Shift+Left disagrees between applications about whether
        /// punctuation splits a word (so "l;ylfu" is one word in some and three
        /// in others), and any count based on it would select the wrong text.
        ///
        /// Declining is silent. This runs on every Win+Space with no selection,
        /// which is overwhelmingly just someone switching language, and beeping
        /// at them for that would be intolerable.
        private const int MaxLinePrefix = 2000;

        private SmartResult TrySmartSelection(int caps, bool isConsole)
        {
            Combo(Native.VK_SHIFT, Native.VK_HOME);
            string linePrefix = CopySelection(isConsole);
            if (string.IsNullOrEmpty(linePrefix))
            {
                // Either the caret was already at the start of the line, in which
                // case Shift+Home selected nothing and there is nothing to undo,
                // or the application refused to copy and its selection state is
                // unknown. Guessing a keystroke count here would move the caret,
                // so do nothing.
                _log("  smart: nothing before the caret");
                return null;
            }
            if (linePrefix.Length > MaxLinePrefix)
            {
                ReleaseSelection(linePrefix.Length);
                _log("  smart: line is longer than " + MaxLinePrefix + " characters, left alone");
                return null;
            }

            TailResult tail = SmartSelection.ComputeTail(linePrefix, _layouts, caps,
                                                         _settings.MaxSmartChars, _settings.MaxSmartWords,
                                                         _judge);
            if (tail.CharCount <= 0)
            {
                ReleaseSelection(linePrefix.Length);
                _log("  smart: declined (" + tail.Reason + ")");
                return null;
            }

            string wanted = linePrefix.Substring(linePrefix.Length - tail.CharCount);
            int selected = linePrefix.Length;

            // Shift+Home already selected exactly the right span when the whole
            // line needs converting, which is the common case.
            if (tail.CharCount < linePrefix.Length)
            {
                ShrinkFromLeft(linePrefix.Length - tail.CharCount);
                selected = tail.CharCount;

                // Confirm the selection really is what the arithmetic said before
                // anything gets overwritten. Applications differ enough about
                // caret handling that this check has already earned its keep.
                string check = CopySelection(isConsole);
                if (check != wanted)
                {
                    ReleaseSelection(selected);
                    _log("  smart: reselect mismatch, left alone (wanted '" + Shorten(wanted) +
                         "', got '" + Shorten(check) + "')");
                    return null;
                }
            }

            _log("  smart: selected " + tail.CharCount + " char(s) of " + linePrefix.Length);
            SmartResult r = new SmartResult();
            r.Text = wanted;
            r.SelectionLength = selected;
            return r;
        }

        private static string Shorten(string s)
        {
            if (s == null) return "<null>";
            s = s.Replace("\r", "").Replace("\n", "\\n").Replace("\t", "\\t");
            return s.Length <= 60 ? s : s.Substring(0, 57) + "...";
        }
    }
}
