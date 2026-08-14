// ---------------------------------------------------------------------------
//  The "start with Windows" shortcut. A plain .lnk in the user's own Startup
//  folder: no admin rights, no registry, nothing outside the user profile.
// ---------------------------------------------------------------------------
using System;
using System.IO;
using System.Reflection;

namespace KbFix
{
    internal static class Startup
    {
        public const string ShortcutName = "Keyboard Language Fixer.lnk";

        public static string Folder
        {
            get { return Environment.GetFolderPath(Environment.SpecialFolder.Startup); }
        }

        public static string ShortcutPath { get { return Path.Combine(Folder, ShortcutName); } }

        public static bool Installed { get { return File.Exists(ShortcutPath); } }

        /// Uses WScript.Shell through late binding rather than a COM reference,
        /// so the build needs nothing beyond the compiler that ships with
        /// Windows.
        public static bool Install(string exePath, out string problem)
        {
            problem = null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) { problem = "WScript.Shell is not available."; return false; }

                object shell = Activator.CreateInstance(shellType);
                object shortcut = shellType.InvokeMember("CreateShortcut",
                    BindingFlags.InvokeMethod, null, shell, new object[] { ShortcutPath });
                Type sc = shortcut.GetType();

                Set(sc, shortcut, "TargetPath", exePath);
                Set(sc, shortcut, "WorkingDirectory", Path.GetDirectoryName(exePath));
                Set(sc, shortcut, "Description", "Fixes text typed with the wrong keyboard layout");
                string icon = Path.Combine(Path.GetDirectoryName(exePath), "icon.ico");
                Set(sc, shortcut, "IconLocation", File.Exists(icon) ? icon + ",0" : exePath + ",0");
                sc.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, new object[0]);
                return true;
            }
            catch (Exception ex)
            {
                problem = ex.Message;
                return false;
            }
        }

        private static void Set(Type t, object target, string property, object value)
        {
            t.InvokeMember(property, BindingFlags.SetProperty, null, target, new object[] { value });
        }

        public static bool Uninstall(out string problem)
        {
            problem = null;
            try
            {
                if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
                return true;
            }
            catch (Exception ex)
            {
                problem = ex.Message;
                return false;
            }
        }
    }
}
