// ---------------------------------------------------------------------------
//  Entry point, command line, and the resident tray application.
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace KbFix
{
    internal sealed class Options
    {
        public bool SelfTest, ListLayouts, ConfigureHotkey, InstallStartup, UninstallStartup, Help;
        public bool NoTray, NoLangSwitch, NoSmart;
        public string Hotkey, QuitHotkey, LogPath, Mode;
        public List<string> Errors = new List<string>();

        public bool TextMode { get { return SelfTest || ListLayouts || Help || InstallStartup || UninstallStartup; } }

        public static Options Parse(string[] argv)
        {
            Options o = new Options();
            for (int i = 0; i < argv.Length; i++)
            {
                string a = argv[i];
                string flag = a.TrimStart('-', '/').ToLowerInvariant();
                switch (flag)
                {
                    case "self-test": case "selftest": o.SelfTest = true; break;
                    case "list-layouts": case "listlayouts": o.ListLayouts = true; break;
                    case "configure-hotkey": case "configurehotkey": o.ConfigureHotkey = true; break;
                    case "install-startup": o.InstallStartup = true; break;
                    case "uninstall-startup": o.UninstallStartup = true; break;
                    case "no-tray": o.NoTray = true; break;
                    case "no-lang-switch": o.NoLangSwitch = true; break;
                    case "no-smart": o.NoSmart = true; break;
                    case "h": case "help": case "?": o.Help = true; break;
                    case "hotkey": o.Hotkey = Next(argv, ref i, o, "hotkey"); break;
                    case "quit-hotkey": o.QuitHotkey = Next(argv, ref i, o, "quit-hotkey"); break;
                    case "log": o.LogPath = Next(argv, ref i, o, "log"); break;
                    case "mode": o.Mode = Next(argv, ref i, o, "mode"); break;
                    default: o.Errors.Add("unknown option '" + a + "'"); break;
                }
            }
            return o;
        }

        private static string Next(string[] argv, ref int i, Options o, string name)
        {
            if (i + 1 >= argv.Length) { o.Errors.Add("--" + name + " needs a value"); return null; }
            return argv[++i];
        }
    }

    internal static class Program
    {
        private const string MutexName = "Local\\KeyboardLangFixer.SingleInstance";

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);

        [STAThread]
        private static int Main(string[] argv)
        {
            Options o = Options.Parse(argv);
            if (o.TextMode) AttachHostConsole();

            if (o.Errors.Count > 0)
            {
                foreach (string e in o.Errors) Console.Error.WriteLine("error: " + e);
                PrintUsage();
                return 64;
            }
            if (o.Help) { PrintUsage(); return 0; }

            string folder = Branding.AppFolder;
            string problem;
            Settings settings = Settings.Load(folder, out problem);
            if (problem != null) Console.Error.WriteLine("settings.json: " + problem);

            // An explicit --hotkey wins over the saved one; the saved one wins
            // over the default.
            if (!string.IsNullOrEmpty(o.Hotkey)) settings.Hotkey = o.Hotkey;
            if (o.NoLangSwitch) settings.SwitchLanguage = false;
            if (o.NoSmart) settings.SmartSelection = false;

            if (o.SelfTest) return SelfTest.Run();

            if (o.InstallStartup)
            {
                string why;
                bool ok = Startup.Install(Branding.ExePath, out why);
                Console.WriteLine(ok ? "installed: " + Startup.ShortcutPath : "failed: " + why);
                return ok ? 0 : 1;
            }
            if (o.UninstallStartup)
            {
                string why;
                bool ok = Startup.Uninstall(out why);
                Console.WriteLine(ok ? "removed: " + Startup.ShortcutPath : "failed: " + why);
                return ok ? 0 : 1;
            }

            bool usedBuiltIn;
            Layout[] layouts = Layouts.Resolve(out usedBuiltIn);

            if (o.ListLayouts) { PrintLayouts(layouts, usedBuiltIn); return 0; }

            if (o.ConfigureHotkey) return ConfigureHotkey(folder, settings);

            // ---- resident mode ----------------------------------------------
            bool isFirst;
            using (Mutex mutex = new Mutex(true, MutexName, out isFirst))
            {
                if (!isFirst)
                {
                    // Two copies at once is worse than none: both react to the
                    // same keypress, convert the same text one after the other,
                    // and hand back the original.
                    const string already = "Keyboard Language Fixer is already running.\r\n\r\n" +
                                           "Use its tray icon to change settings or quit it.";
                    // A person who double-clicked the program gets a dialog; a
                    // script that captured our output gets text it can read,
                    // because a modal dialog would simply hang it.
                    if (Console.IsOutputRedirected) Console.WriteLine(already.Replace("\r\n", " "));
                    else MessageBox.Show(already, "Keyboard Language Fixer",
                                         MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 2;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try
                {
                    using (TrayApp app = new TrayApp(settings, layouts, usedBuiltIn, o))
                    {
                        app.Run();
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Keyboard Language Fixer could not start:\r\n\r\n" + ex.Message,
                                    "Keyboard Language Fixer", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
                finally
                {
                    try { mutex.ReleaseMutex(); }
                    catch { }
                }
            }
            return 0;
        }

        private static int ConfigureHotkey(string folder, Settings settings)
        {
            Application.EnableVisualStyles();
            using (HotkeyDialog dlg = new HotkeyDialog(settings.Hotkey))
            {
                if (dlg.ShowDialog() != DialogResult.OK) return 0;
                settings.Hotkey = dlg.Chosen;
            }
            string problem;
            if (!settings.Save(folder, out problem))
            {
                MessageBox.Show("Could not save settings:\r\n" + problem, "Keyboard Language Fixer",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }

            // Restart a running copy so the new hotkey takes effect at once.
            int restarted = 0;
            foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcessesByName(
                         Path.GetFileNameWithoutExtension(Branding.ExePath)))
            {
                if (p.Id == System.Diagnostics.Process.GetCurrentProcess().Id) continue;
                try { p.Kill(); restarted++; }
                catch { }
            }
            if (restarted > 0)
            {
                Thread.Sleep(700);
                try { System.Diagnostics.Process.Start(Branding.ExePath); }
                catch { }
            }

            MessageBox.Show("Hotkey set to " + settings.Hotkey + "." +
                            (restarted > 0 ? "\r\n\r\nKeyboard Language Fixer has been restarted."
                                           : "\r\n\r\nIt will be used the next time the tool starts."),
                            "Keyboard Language Fixer", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        /// A windowed program has no console of its own. When it was launched
        /// from one, borrow it so --self-test and --list-layouts can be read.
        /// Redirected output already works without this.
        private static void AttachHostConsole()
        {
            try
            {
                if (Console.IsOutputRedirected) return;
                if (!AttachConsole(-1)) return;   // ATTACH_PARENT_PROCESS
                StreamWriter w = new StreamWriter(Console.OpenStandardOutput());
                w.AutoFlush = true;
                Console.SetOut(w);
                StreamWriter e = new StreamWriter(Console.OpenStandardError());
                e.AutoFlush = true;
                Console.SetError(e);
            }
            catch { }
        }

        public static void PrintLayouts(Layout[] layouts, bool usedBuiltIn)
        {
            Console.WriteLine("Layouts available for conversion:");
            foreach (Layout l in layouts)
                Console.WriteLine("  0x" + l.LangId.ToString("X4", CultureInfo.InvariantCulture) +
                                  "  " + l.Name.PadRight(36) + " " + l.KeyCount + " keys" +
                                  (l.BuiltIn ? "  (built-in table)" : ""));
            if (usedBuiltIn)
            {
                Console.WriteLine("Windows reported fewer than two keyboard layouts, so the built-in Thai table is in use.");
                Console.WriteLine("Add a second keyboard layout in Windows Settings to convert between other languages.");
            }
            Console.WriteLine("This machine switches input language with:");
            foreach (string h in LanguageSwitchKeys()) Console.WriteLine("  " + h);
        }

        /// Reports which keys THIS machine uses to switch input language, which
        /// differs between machines and matters when choosing what to bind to.
        ///
        /// Win+Space is a shell shortcut built into Windows 8 and later. It is
        /// not listed in the "Input language hot keys" dialog and cannot be
        /// unassigned there, so it is the one combination available everywhere.
        /// The legacy toggles under HKCU\Keyboard Layout\Toggle are the
        /// configurable ones, and their value really does vary per machine.
        public static List<string> LanguageSwitchKeys()
        {
            List<string> found = new List<string>();
            found.Add("Win+Space (built into Windows 8+, cannot be unassigned)");
            try
            {
                using (Microsoft.Win32.RegistryKey key =
                       Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Keyboard Layout\Toggle"))
                {
                    if (key == null)
                    {
                        found.Add("Left Alt+Shift (Windows default when the toggle is unset)");
                    }
                    else
                    {
                        Dictionary<string, string> names = new Dictionary<string, string>();
                        names["1"] = "Left Alt+Shift";
                        names["2"] = "Ctrl+Shift";
                        names["4"] = "Grave accent (`)";
                        foreach (string valueName in new string[] { "Language Hotkey", "Layout Hotkey" })
                        {
                            object v = key.GetValue(valueName);
                            if (v == null) continue;
                            string label;
                            if (names.TryGetValue(v.ToString(), out label) && !found.Contains(label)) found.Add(label);
                        }
                    }
                }
            }
            catch { }
            return found;
        }

        private static void PrintUsage()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Keyboard Language Fixer - fixes text typed with the wrong keyboard layout.");
            sb.AppendLine();
            sb.AppendLine("  KeyboardLangFixer.exe                 run in the system tray (default)");
            sb.AppendLine("  KeyboardLangFixer.exe --self-test     check the conversion tables and exit");
            sb.AppendLine("  KeyboardLangFixer.exe --list-layouts   show convertible layouts and exit");
            sb.AppendLine("  KeyboardLangFixer.exe --configure-hotkey   open the hotkey picker and exit");
            sb.AppendLine();
            sb.AppendLine("  --hotkey <combo>      e.g. \"Win+Space\", \"Ctrl+Alt+X\" (overrides settings.json)");
            sb.AppendLine("  --quit-hotkey <combo> default Ctrl+Alt+Shift+X");
            sb.AppendLine("  --mode Auto|Hook|Hotkey   how the combination is claimed (default Auto)");
            sb.AppendLine("  --no-smart            do not work out a selection when nothing is selected");
            sb.AppendLine("  --no-lang-switch      convert text only, leave the input language alone");
            sb.AppendLine("  --no-tray             run without a tray icon");
            sb.AppendLine("  --log <file>          append a line per trigger, for troubleshooting");
            sb.AppendLine("  --install-startup / --uninstall-startup   manage the login shortcut");
            Console.WriteLine(sb.ToString());
        }
    }
}
