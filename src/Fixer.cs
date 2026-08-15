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
    /// What a press is allowed to do, which depends entirely on which key was
    /// pressed.
    ///
    /// Win+Space and Caps Lock belong to Windows. This program watches them
    /// without consuming them, so whatever they already did still happens, and
    /// in exchange they are held to a much stricter rule than the program's own
    /// hotkey: they act ONLY on text the user deliberately selected. Nothing
    /// they do can ever be a guess, because the user was not necessarily talking
    /// to this program when they pressed the key.
    internal enum FixMode
    {
        /// The program's own hotkey. Converts a selection, and with nothing
        /// selected works out what the user just typed (Smart Selection) or
        /// offers to undo the last conversion.
        Full,
        /// Convert a selection between keyboard layouts, and do nothing at all
        /// without one.
        SelectionOnly,
        /// Swap the case of a selection, and do nothing at all without one.
        Case
    }

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
        /// The Win key is never released synthetically. Windows commits its own
        /// Win+Space language switch when the key is released, and an extra
        /// release on top of the user's real one makes it drop the switch --
        /// which would break the promise this tool makes about every key it
        /// shares: whatever Windows already did on that key still happens.
        ///
        /// Ctrl, Alt and Shift have no such behaviour, so those are still forced
        /// up after the wait in case one is genuinely stuck.
        private static void ClearModifiers()
        {
            int[] watched = new int[] { Native.VK_CONTROL, Native.VK_MENU, Native.VK_SHIFT,
                                        Native.VK_LWIN, Native.VK_RWIN };
            bool sawWin = false;
            int waited = 0;
            while (waited < 900)
            {
                bool anyDown = false;
                foreach (int vk in watched)
                {
                    if (!Native.IsDown(vk)) continue;
                    anyDown = true;
                    if (vk == Native.VK_LWIN || vk == Native.VK_RWIN) sawWin = true;
                }
                if (!anyDown) break;
                Thread.Sleep(15);
                waited += 15;
            }
            Key(Native.VK_CONTROL, false);
            Key(Native.VK_MENU, false);
            Key(Native.VK_SHIFT, false);

            // Waiting for the Win key to come up is not enough on its own.
            // Windows commits the Win+Space language switch on that release, and
            // the flyout it draws holds the foreground while it does -- so the
            // copy that follows raced both. Measured across full runs: the
            // language sometimes never switched at all, and a selection
            // sometimes came back empty because its window was not in front yet
            // when Ctrl+Insert arrived. Letting the flyout finish costs a
            // quarter of a second on the one trigger that shares a Win key.
            Thread.Sleep(sawWin ? 260 : 20);
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

        /// A single burst of arrow keys is capped: the recovery paths below run
        /// on the ordinary "nothing to fix" press, and a caret at the end of a
        /// minified-JSON line would otherwise mean hundreds of thousands of
        /// injected events, wedging the target application and this one with it.
        private const int MaxPresses = 600;

        /// Shrinks a leftward selection from its left edge by moving the active
        /// end right, which leaves the anchor -- the caret the user was at --
        /// exactly where it was.
        ///
        /// Pressing Right instead would be wrong: in an edit control an arrow key
        /// moves from the ACTIVE end of the selection, and after Shift+Home that
        /// end is the start of the line, so Right lands near position 1 rather
        /// than back at the caret.
        ///
        /// $presses is a count of grapheme clusters, not characters; see
        /// TextUnits for why the difference matters.
        private bool ShrinkFromLeft(int presses)
        {
            if (presses <= 0) return true;
            if (presses > MaxPresses)
            {
                _log("  refusing to send " + presses + " keystrokes (cap " + MaxPresses + ")");
                return false;
            }
            ComboRepeat(Native.VK_SHIFT, Native.VK_RIGHT, presses);
            return true;
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

        /// $mode says which key fired and therefore how much this press is
        /// allowed to do; see FixMode.
        public FixOutcome Run(FixMode mode)
        {
            // Before anything else, including the synthetic modifier releases:
            // an ignored program must receive nothing whatsoever.
            string ignoredApp;
            if (ForegroundIsIgnored(out ignoredApp))
            {
                _log("trigger: '" + ignoredApp + "' is on the ignore list, doing nothing");
                return FixOutcome.NoSelection;
            }

            ClearModifiers();

            string cls = Native.ForegroundWindowClass();
            bool isConsole = Array.IndexOf(ConsoleClasses, cls) >= 0;
            int caps = Native.CapsLockState();

            _log("trigger: " + mode + ", class '" + cls + "'" + (isConsole ? " (console)" : "") +
                 (caps != 0 ? ", CapsLock on" : ""));

            // Read the clipboard before touching it. This is a read only: if
            // nothing turns out to be selected, the clipboard is never written.
            ClipboardSnapshot snapshot = ClipboardSnapshot.Take();

            string selection = CopySelection(isConsole);
            if (mode == FixMode.Case) return FlipCase(snapshot, isConsole, selection);

            bool smart = false;
            int smartSelected = 0;

            // No undo branch is needed when something is selected: the character
            // mapping is a bijection, so converting the selection again produces
            // the original text anyway, byte for byte. Undo only earns its keep
            // where the text has to be found again first.
            bool undoOffered = mode == FixMode.Full && _undo != null &&
                               _undo.IsOffered(DateTime.UtcNow, _settings.UndoWindowSeconds,
                                               Native.GetForegroundWindow());

            if (string.IsNullOrEmpty(selection))
            {
                if (mode != FixMode.Full)
                {
                    // A key that belongs to Windows, pressed with nothing
                    // selected, means what it has always meant. Nothing was
                    // copied, so the clipboard was never written either and
                    // there is nothing to put back.
                    _log("  nothing selected -> the key does its own job only");
                    return FixOutcome.NoSelection;
                }
                if (isConsole || (!_settings.SmartSelection && !undoOffered))
                {
                    snapshot.Restore();
                    _log("  nothing selected -> language switch only");
                    return FixOutcome.NoSelection;
                }

                // Nothing is selected, so re-select what was just pasted and see
                // whether it is still there before offering to restore it.
                if (undoOffered)
                {
                    int presses = TextUnits.PressCount(_undo.Converted);
                    if (presses > 0 && presses <= MaxPresses)
                    {
                        ComboRepeat(Native.VK_SHIFT, Native.VK_LEFT, presses);
                        string atCaret = CopySelection(isConsole);
                        if (_undo.Matches(atCaret)) return Undo(snapshot, isConsole, atCaret, true);
                        ReleaseSelection(atCaret, presses);
                        _log("  undo not offered: the text at the caret has changed");
                    }
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
                if (smart) ReleaseSelection(selection, smartSelected);
                snapshot.Restore();
                _log("  read '" + Shorten(selection) + "' but no layout explains it");
                if (!smart) Complain();     // an explicit selection deserves feedback
                return FixOutcome.Declined;
            }

            // Shift+Insert at a shell prompt submits every line but the last, so
            // a multi-line selection would run as commands rather than be edited.
            if (isConsole && (selection.IndexOf((char)10) >= 0 || selection.IndexOf((char)13) >= 0))
            {
                snapshot.Restore();
                _log("  console selection spans lines, left alone");
                Complain();
                return FixOutcome.Declined;
            }

            string converted = Converter.Convert(selection, source, target, caps);
            _log("  '" + Shorten(selection) + "' -> '" + Shorten(converted) + "'  [" +
                 source.Name + " -> " + target.Name + "]" + (smart ? "  (smart)" : ""));

            if (converted == selection)
            {
                if (smart) ReleaseSelection(selection, smartSelected);
                snapshot.Restore();
                _log("  nothing convertible, left alone");
                if (!smart) Complain();
                return FixOutcome.Declined;
            }

            if (!ClipboardSafe.SetText(converted))
            {
                if (smart) ReleaseSelection(selection, smartSelected);
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
                if (smart) ReleaseSelection(selection, smartSelected);
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

            if (_undo != null) _undo.Remember(selection, converted, source.LangId, Native.GetForegroundWindow());
            return FixOutcome.Converted;
        }

        /// Caps Lock was pressed. It has already toggled on its own -- this
        /// program watches the key without consuming it -- so if there is a
        /// selection, swap its case.
        ///
        /// $selection is whatever the copy probe found, which is empty when the
        /// user simply pressed Caps Lock to turn it on or off.
        private FixOutcome FlipCase(ClipboardSnapshot snapshot, bool isConsole, string selection)
        {
            if (string.IsNullOrEmpty(selection))
            {
                // The overwhelmingly common press. Nothing was copied, so the
                // clipboard was never written; the toggle Windows just performed
                // is the only effect, exactly as before this program existed.
                _log("  nothing selected -> Caps Lock toggled as usual");
                return FixOutcome.NoSelection;
            }

            // Shift+Insert at a shell prompt submits every line but the last.
            if (isConsole && (selection.IndexOf((char)10) >= 0 || selection.IndexOf((char)13) >= 0))
            {
                snapshot.Restore();
                _log("  console selection spans lines, left alone");
                Complain();
                return FixOutcome.Declined;
            }

            string flipped = CaseFix.Flip(selection);
            _log("  case: '" + Shorten(selection) + "' -> '" + Shorten(flipped) + "'");

            if (flipped == selection)
            {
                snapshot.Restore();
                _log("  no letters with a case, left alone" +
                     (CaseFix.HasCasedLetter(selection) ? " (nothing maps)" : ""));
                Complain();     // an explicit selection deserves feedback
                return FixOutcome.Declined;
            }

            if (!ClipboardSafe.SetText(flipped))
            {
                snapshot.Restore();
                _log("  could not write the clipboard, aborted without pasting");
                Complain();
                return FixOutcome.Failed;
            }

            // Pasting a stale clipboard would wipe the selection instead of
            // fixing it, so confirm the write landed first.
            Thread.Sleep(40);
            if (ClipboardSafe.GetText() != flipped)
            {
                snapshot.Restore();
                _log("  clipboard write did not stick, aborted without pasting");
                Complain();
                return FixOutcome.Failed;
            }

            Paste(isConsole);
            _log("  pasted");
            NormaliseCapsLock();

            Thread.Sleep(250);
            snapshot.Restore();
            return FixOutcome.Converted;
        }

        /// Leaves Caps Lock off after a case fix.
        ///
        /// Pressing the key with something selected means "fix this text", but
        /// the key also toggled, and which way it went decides whether that
        /// helped. Ending off is right both times. Text that needed fixing was
        /// almost always typed with Caps Lock stuck on, so the toggle turned it
        /// off and there is nothing to do -- the user gets the state they wanted
        /// anyway. In the other direction the toggle just turned it ON, which
        /// would break the next word the user types, so it is undone here.
        private void NormaliseCapsLock()
        {
            if (Native.CapsLockState() == 0) return;
            Tap(Native.VK_CAPITAL);
            Thread.Sleep(30);
            _log("  Caps Lock turned back off");
        }

        /// Puts the text from before the last conversion back, exactly as it was.
        /// The caller has already confirmed that what is selected is what this
        /// tool pasted, so this only has to write and tidy up.
        private FixOutcome Undo(ClipboardSnapshot snapshot, bool isConsole, string selectedText, bool reselected)
        {
            string original = _undo.Original;
            int langId = _undo.SourceLangId;

            if (!ClipboardSafe.SetText(original))
            {
                if (reselected) ReleaseSelection(selectedText, 0);
                snapshot.Restore();
                Complain();
                _log("  undo: could not write the clipboard");
                return FixOutcome.Failed;
            }
            Thread.Sleep(40);
            if (ClipboardSafe.GetText() != original)
            {
                if (reselected) ReleaseSelection(selectedText, 0);
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
        ///
        /// $selectedText is what is actually selected when it is known, because
        /// the press count has to come from its grapheme clusters. When it is not
        /// known the caller passes the count it used to build the selection,
        /// which is the best available guess.
        private void ReleaseSelection(string selectedText, int fallbackPresses)
        {
            int presses = string.IsNullOrEmpty(selectedText)
                        ? fallbackPresses
                        : TextUnits.PressCount(selectedText);
            ShrinkFromLeft(presses);
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
                ReleaseSelection(linePrefix, 0);
                _log("  smart: line is longer than " + MaxLinePrefix + " characters, left alone");
                return null;
            }

            TailResult tail = SmartSelection.ComputeTail(linePrefix, _layouts, caps,
                                                         _settings.MaxSmartChars, _settings.MaxSmartWords,
                                                         _judge);
            if (tail.CharCount <= 0)
            {
                ReleaseSelection(linePrefix, 0);
                _log("  smart: declined (" + tail.Reason + ")");
                return null;
            }

            string wanted = linePrefix.Substring(linePrefix.Length - tail.CharCount);
            int selected = linePrefix.Length;

            // Shift+Home already selected exactly the right span when the whole
            // line needs converting, which is the common case.
            if (tail.CharCount < linePrefix.Length)
            {
                // Counted in grapheme clusters, not characters: an arrow key
                // crosses a Thai vowel mark and its base together, so a character
                // count would run past the caret and leave text after it selected.
                int shrink = TextUnits.PressCountForPrefix(linePrefix, linePrefix.Length - tail.CharCount);
                if (!ShrinkFromLeft(shrink))
                {
                    ReleaseSelection(linePrefix, 0);
                    return null;
                }
                selected = tail.CharCount;

                // Confirm the selection really is what the arithmetic said before
                // anything gets overwritten. Applications differ enough about
                // caret handling that this check has already earned its keep.
                string check = CopySelection(isConsole);
                if (check != wanted)
                {
                    ReleaseSelection(check, TextUnits.PressCount(wanted));
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
