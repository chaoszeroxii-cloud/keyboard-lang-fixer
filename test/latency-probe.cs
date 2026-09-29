// Stage-by-stage latency probe: real hotkey, real fixer process, a target that
// pumps messages the moment they arrive (Application.Run in this process, or a
// real Chromium window), and every timestamp taken from the same QPC clock the
// fixer logs with. See latency-probe.ps1 for how it is run.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace LatencyProbe
{
    internal static class W
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Explicit)]
        public struct UNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT { public uint type; public UNION u; }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        public delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
        [DllImport("user32.dll")] public static extern short GetKeyState(int vk);
        [DllImport("kernel32.dll")] public static extern void GetSystemTimePreciseAsFileTime(out long ft);

        /// Wall-clock milliseconds since 1970, the clock a page's
        /// performance.timeOrigin + performance.now() is expressed in.
        public static double EpochMs()
        {
            long ft;
            GetSystemTimePreciseAsFileTime(out ft);
            return (ft - 116444736000000000L) / 10000.0;
        }

        public static INPUT Key(int vk, bool down)
        {
            INPUT i = new INPUT();
            i.type = 1;
            i.u.ki.wVk = (ushort)vk;
            i.u.ki.dwFlags = down ? 0u : 2u;
            return i;
        }

        public static long Send(params INPUT[] keys)
        {
            SendInput((uint)keys.Length, keys, Marshal.SizeOf(typeof(INPUT)));
            return Stopwatch.GetTimestamp();
        }

        public static string Title(IntPtr h)
        {
            StringBuilder sb = new StringBuilder(512);
            GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }

        public static bool Foreground(IntPtr h)
        {
            for (int i = 0; i < 10; i++)
            {
                if (GetForegroundWindow() == h) return true;
                uint fg = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
                uint me = GetCurrentThreadId();
                AttachThreadInput(me, fg, true);
                BringWindowToTop(h);
                SetForegroundWindow(h);
                AttachThreadInput(me, fg, false);
                Thread.Sleep(50);
            }
            return GetForegroundWindow() == h;
        }
    }

    /// Records when the expected text lands and when the control next paints.
    internal sealed class Watch
    {
        public volatile string Expected;
        public long TextAt, PaintAt;
        public readonly ManualResetEvent Painted = new ManualResetEvent(false);
        public readonly ManualResetEvent Texted = new ManualResetEvent(false);

        public void Arm(string expected)
        {
            TextAt = 0; PaintAt = 0;
            Painted.Reset(); Texted.Reset();
            Expected = expected;
        }

        public void OnText(string text)
        {
            if (Expected != null && TextAt == 0 && text == Expected)
            {
                TextAt = Stopwatch.GetTimestamp();
                Texted.Set();
            }
        }

        public void OnPaint()
        {
            if (TextAt != 0 && PaintAt == 0)
            {
                PaintAt = Stopwatch.GetTimestamp();
                Painted.Set();
            }
        }
    }

    internal sealed class ProbeBox : TextBox
    {
        public Watch Watch;
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Watch.OnText(Text); }
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x000F) Watch.OnPaint();
        }
    }

    internal sealed class ProbeRich : RichTextBox
    {
        public Watch Watch;
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Watch.OnText(Text); }
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x000F) Watch.OnPaint();
        }
    }

    internal sealed class Sample
    {
        public string Target, Trigger;
        public int Index;
        public double Visible = double.NaN, Paint = double.NaN;
        public List<KeyValuePair<string, double>> Stages = new List<KeyValuePair<string, double>>();
        public string Note = "";
    }

    internal static class Program
    {
        const string EN = "l;ylfu";
        static readonly string TH = new string(new char[] {
            (char)0x0E2A, (char)0x0E27, (char)0x0E31, (char)0x0E2A, (char)0x0E14, (char)0x0E35 });

        static string _log;
        static readonly List<Sample> Results = new List<Sample>();

        [STAThread]
        static int Main(string[] args)
        {
            string targets = "textbox,richtextbox", triggers = "ctrlaltspace,winspace", browser = null, page = null, outFile = null;
            int samples = 10;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--log": _log = args[++i]; break;
                    case "--targets": targets = args[++i]; break;
                    case "--triggers": triggers = args[++i]; break;
                    case "--samples": samples = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--browser": browser = args[++i]; break;
                    case "--page": page = args[++i]; break;
                    case "--out": outFile = args[++i]; break;
                }
            }
            if ((W.GetKeyState(0x14) & 1) != 0) { Console.WriteLine("Caps Lock is on; turn it off first."); return 2; }

            foreach (string target in targets.Split(','))
                foreach (string trigger in triggers.Split(','))
                {
                    if (target == "textbox" || target == "richtextbox") RunInternal(target, trigger, samples);
                    else RunBrowser(target, trigger, samples, browser, page);
                }

            Report(outFile);
            return 0;
        }

        // ------------------------------------------------------------ triggers
        static double _releaseEpoch;

        static long Press(string trigger)
        {
            long qpc = PressKeys(trigger);
            _releaseEpoch = W.EpochMs();
            return qpc;
        }

        static long PressKeys(string trigger)
        {
            if (trigger == "winspace")
            {
                W.Send(W.Key(0x5B, true));
                Thread.Sleep(80);
                W.Send(W.Key(0x20, true));
                Thread.Sleep(80);
                W.Send(W.Key(0x20, false));
                Thread.Sleep(120);
                return W.Send(W.Key(0x5B, false));
            }
            W.Send(W.Key(0x11, true), W.Key(0x12, true), W.Key(0x20, true));
            Thread.Sleep(60);
            return W.Send(W.Key(0x20, false), W.Key(0x12, false), W.Key(0x11, false));
        }

        // ------------------------------------------------------------ fixer log
        static string ReadLog()
        {
            try
            {
                using (FileStream fs = new FileStream(_log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader sr = new StreamReader(fs, Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch { return ""; }
        }

        static int DoneCount()
        {
            string t = ReadLog();
            int n = 0, i = 0;
            while ((i = t.IndexOf("done: ", i, StringComparison.Ordinal)) >= 0) { n++; i += 6; }
            return n;
        }

        static bool WaitDone(int before, int ms)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                if (DoneCount() > before) return true;
                Thread.Sleep(20);
            }
            return false;
        }

        /// The timing line of the most recent press, re-based on $release.
        static void AttachStages(Sample s, long release)
        {
            string t = ReadLog();
            int at = t.LastIndexOf("  timing: qpc=", StringComparison.Ordinal);
            if (at < 0) { s.Note += " no-timing"; return; }
            int end = t.IndexOf('\n', at);
            string line = (end < 0 ? t.Substring(at) : t.Substring(at, end - at)).Trim();
            long origin = 0; double freq = Stopwatch.Frequency;
            foreach (string part in line.Split(' '))
            {
                int eq = part.IndexOf('=');
                if (eq < 0) continue;
                string k = part.Substring(0, eq), v = part.Substring(eq + 1);
                if (k == "qpc") origin = long.Parse(v, CultureInfo.InvariantCulture);
                else if (k == "freq") freq = double.Parse(v, CultureInfo.InvariantCulture);
                else
                {
                    double ms = double.Parse(v, CultureInfo.InvariantCulture);
                    double rel = (origin + ms * freq / 1000.0 - release) * 1000.0 / freq;
                    s.Stages.Add(new KeyValuePair<string, double>(k, rel));
                }
            }
        }

        static double Ms(long from, long to) { return (to - from) * 1000.0 / Stopwatch.Frequency; }

        // ------------------------------------------------------------ in-process targets
        static void RunInternal(string target, string trigger, int samples)
        {
            Watch watch = new Watch();
            Form form = null;
            Control box = null;
            ManualResetEvent shown = new ManualResetEvent(false);
            Thread ui = new Thread(delegate()
            {
                form = new Form();
                form.Text = "KbFix latency " + target;
                form.TopMost = true;
                form.Width = 600; form.Height = 200;
                form.StartPosition = FormStartPosition.CenterScreen;
                form.Font = new System.Drawing.Font("Segoe UI", 16f);
                if (target == "richtextbox") { ProbeRich r = new ProbeRich(); r.Watch = watch; box = r; }
                else { ProbeBox b = new ProbeBox(); b.Watch = watch; b.Multiline = true; box = b; }
                box.Dock = DockStyle.Fill;
                form.Controls.Add(box);
                form.Shown += delegate { shown.Set(); };
                Application.Run(form);
            });
            ui.SetApartmentState(ApartmentState.STA);
            ui.Start();
            shown.WaitOne();
            Thread.Sleep(300);

            for (int i = 0; i < samples; i++)
            {
                string from = i % 2 == 0 ? EN : TH;
                string to = i % 2 == 0 ? TH : EN;
                form.Invoke((MethodInvoker)delegate
                {
                    watch.Expected = null;
                    box.Text = from;
                    ((TextBoxBase)box).SelectAll();
                    box.Focus();
                });
                W.Foreground(form.Handle);
                Thread.Sleep(250);

                Sample s = new Sample();
                s.Target = target; s.Trigger = trigger; s.Index = i + 1;
                int before = DoneCount();
                watch.Arm(to);
                long release = Press(trigger);
                if (watch.Texted.WaitOne(3000))
                {
                    s.Visible = Ms(release, watch.TextAt);
                    if (watch.Painted.WaitOne(300)) s.Paint = Ms(release, watch.PaintAt);
                }
                else s.Note += " text-never-changed";
                if (!WaitDone(before, 5000)) s.Note += " no-done";
                AttachStages(s, release);
                Results.Add(s);
                Print(s);
                Thread.Sleep(200);
            }
            form.Invoke((MethodInvoker)delegate { form.Close(); });
            ui.Join();
        }

        // ------------------------------------------------------------ Chromium
        static IntPtr FindTitled(string prefix, uint pidFilter)
        {
            IntPtr found = IntPtr.Zero;
            W.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (!W.IsWindowVisible(h)) return true;
                if (!W.Title(h).StartsWith(prefix, StringComparison.Ordinal)) return true;
                found = h;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        static double TitleField(string title, string key)
        {
            int at = title.IndexOf(" " + key + "=", StringComparison.Ordinal);
            if (at < 0) return double.NaN;
            at += key.Length + 2;
            int end = title.IndexOf(' ', at);
            double v;
            return double.TryParse(end < 0 ? title.Substring(at) : title.Substring(at, end - at),
                                   NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : double.NaN;
        }

        static int TitleCount(string title)
        {
            int at = title.IndexOf(" n=", StringComparison.Ordinal);
            if (at < 0) return -1;
            int end = title.IndexOf(' ', at + 3);
            int n;
            return int.TryParse(end < 0 ? title.Substring(at + 3) : title.Substring(at + 3, end - at - 3),
                                NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : -1;
        }

        static void RunBrowser(string target, string trigger, int samples, string browser, string page)
        {
            string profile = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(page)), "_browser-profile-" + target);
            string url = new Uri(Path.GetFullPath(page)).AbsoluteUri;
            ProcessStartInfo psi = new ProcessStartInfo(browser,
                "--user-data-dir=\"" + profile + "\" --no-first-run --no-default-browser-check " +
                "--disable-features=Translate --window-size=700,300 --app=\"" + url + "\"");
            psi.UseShellExecute = false;
            Process proc = Process.Start(psi);
            try
            {
                IntPtr h = IntPtr.Zero;
                Stopwatch sw = Stopwatch.StartNew();
                while (h == IntPtr.Zero && sw.ElapsedMilliseconds < 20000)
                {
                    Thread.Sleep(200);
                    h = FindTitled("KbFix latency", 0);
                }
                if (h == IntPtr.Zero) { Console.WriteLine(target + ": window never appeared"); return; }
                Thread.Sleep(1500);

                for (int i = 0; i < samples; i++)
                {
                    W.Foreground(h);
                    // Wait for the page to report it has re-selected its text.
                    Stopwatch ready = Stopwatch.StartNew();
                    string title = W.Title(h);
                    while (title.IndexOf(" ready ", StringComparison.Ordinal) < 0 && ready.ElapsedMilliseconds < 3000)
                    {
                        Thread.Sleep(20);
                        title = W.Title(h);
                    }
                    Thread.Sleep(250);
                    title = W.Title(h);
                    string current = title.Substring(title.IndexOf(" v=", StringComparison.Ordinal) + 3);
                    int startN = TitleCount(title);
                    string to = current == EN ? TH : EN;

                    Sample s = new Sample();
                    s.Target = target; s.Trigger = trigger; s.Index = i + 1;
                    int before = DoneCount();
                    long release = Press(trigger);
                    // Wait for the page to settle, then read its own timestamps out
                    // of the caption; how late the caption itself updates is irrelevant.
                    string want = " v=" + to;
                    string final = null;
                    Stopwatch wait = Stopwatch.StartNew();
                    while (wait.ElapsedMilliseconds < 3000)
                    {
                        string t = W.Title(h);
                        if (t.EndsWith(want, StringComparison.Ordinal) && TitleCount(t) > startN &&
                            t.IndexOf(" ready ", StringComparison.Ordinal) >= 0) { final = t; break; }
                        Thread.Sleep(5);
                    }
                    if (final != null)
                    {
                        double at = TitleField(final, "t"), painted = TitleField(final, "p");
                        s.Visible = at - _releaseEpoch;
                        if (painted > at) s.Paint = painted - _releaseEpoch;
                        // Zero means the page never saw that key in this run.
                        double kc = TitleField(final, "c"), kv = TitleField(final, "k");
                        if (kc > _releaseEpoch - 5000) s.Stages.Add(new KeyValuePair<string, double>("page-ctrl-c", kc - _releaseEpoch));
                        if (kv > _releaseEpoch - 5000) s.Stages.Add(new KeyValuePair<string, double>("page-ctrl-v", kv - _releaseEpoch));
                    }
                    else s.Note += " text-never-changed (title '" + W.Title(h) + "', wanted '" + want + "')";
                    if (!WaitDone(before, 5000)) s.Note += " no-done";
                    AttachStages(s, release);
                    Results.Add(s);
                    Print(s);
                }
            }
            finally
            {
                try
                {
                    Process k = Process.Start(new ProcessStartInfo("taskkill", "/F /T /PID " + proc.Id)
                                              { UseShellExecute = false, CreateNoWindow = true });
                    k.WaitForExit(5000);
                }
                catch { }
                try { if (!proc.HasExited) proc.Kill(); } catch { }
            }
        }

        // ------------------------------------------------------------ output
        static string F(double v) { return double.IsNaN(v) ? "-" : v.ToString("0.0", CultureInfo.InvariantCulture); }
        static string J(double v) { return double.IsNaN(v) ? "null" : v.ToString("0.000", CultureInfo.InvariantCulture); }

        static void Print(Sample s)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("{0,-12} {1,-13} #{2,-3} visible {3,7} paint {4,7} |", s.Target, s.Trigger, s.Index, F(s.Visible), F(s.Paint));
            foreach (KeyValuePair<string, double> kv in s.Stages) sb.Append(' ').Append(kv.Key).Append('=').Append(F(kv.Value));
            sb.Append(s.Note);
            Console.WriteLine(sb.ToString());
        }

        static double Median(List<double> v)
        {
            if (v.Count == 0) return double.NaN;
            v.Sort();
            return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
        }

        static void Report(string outFile)
        {
            Console.WriteLine();
            Console.WriteLine("median ms after key release (n = samples with a value)");
            Dictionary<string, List<Sample>> groups = new Dictionary<string, List<Sample>>();
            List<string> order = new List<string>();
            foreach (Sample s in Results)
            {
                string key = s.Target + " / " + s.Trigger;
                if (!groups.ContainsKey(key)) { groups[key] = new List<Sample>(); order.Add(key); }
                groups[key].Add(s);
            }
            StringBuilder json = new StringBuilder("[\n");
            foreach (string key in order)
            {
                List<Sample> g = groups[key];
                List<double> vis = new List<double>(), paint = new List<double>();
                List<string> labels = new List<string>();
                Dictionary<string, List<double>> stages = new Dictionary<string, List<double>>();
                double min = double.MaxValue, max = 0;
                foreach (Sample s in g)
                {
                    if (!double.IsNaN(s.Visible)) { vis.Add(s.Visible); min = Math.Min(min, s.Visible); max = Math.Max(max, s.Visible); }
                    if (!double.IsNaN(s.Paint)) paint.Add(s.Paint);
                    Dictionary<string, bool> seen = new Dictionary<string, bool>();
                    foreach (KeyValuePair<string, double> kv in s.Stages)
                    {
                        if (seen.ContainsKey(kv.Key)) continue;   // first occurrence only
                        seen[kv.Key] = true;
                        if (!stages.ContainsKey(kv.Key)) { stages[kv.Key] = new List<double>(); labels.Add(kv.Key); }
                        stages[kv.Key].Add(kv.Value);
                    }
                }
                int nVis = vis.Count;
                StringBuilder line = new StringBuilder();
                line.AppendFormat("{0,-28} n={1,-3} visible {2,6} (min {3}, max {4})  paint {5,6} |", key, nVis,
                                  F(Median(vis)), F(nVis > 0 ? min : double.NaN), F(nVis > 0 ? max : double.NaN), F(Median(paint)));
                json.Append("  {\"group\":\"").Append(key).Append("\",\"n\":").Append(nVis)
                    .Append(",\"visible\":").Append(J(Median(new List<double>(vis))))
                    .Append(",\"paint\":").Append(J(Median(new List<double>(paint))))
                    .Append(",\"min\":").Append(J(nVis > 0 ? min : double.NaN))
                    .Append(",\"max\":").Append(J(nVis > 0 ? max : double.NaN));
                foreach (string l in labels)
                {
                    double m = Median(stages[l]);
                    line.Append(' ').Append(l).Append('=').Append(F(m));
                    json.Append(",\"").Append(l).Append("\":").Append(J(m));
                }
                json.Append("},\n");
                Console.WriteLine(line.ToString());
            }
            if (json.Length > 2) json.Length -= 2;
            json.Append("\n]\n");
            if (outFile != null) File.WriteAllText(outFile, json.ToString());
        }
    }
}
