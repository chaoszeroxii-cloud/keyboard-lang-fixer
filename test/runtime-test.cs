using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
using System.Runtime.InteropServices;

namespace KbFix
{
    internal static class RuntimeTest
    {
        private static int _failures;
        [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr window);
        [DllImport("user32.dll")] private static extern bool CloseClipboard();
        private static void Check(string name, bool ok)
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
            if (!ok) _failures++;
        }

        private static bool PumpUntil(Func<bool> condition, int timeout)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (!condition() && watch.ElapsedMilliseconds < timeout)
            {
                Application.DoEvents();
                Thread.Sleep(1);
            }
            return condition();
        }

        [STAThread]
        public static int Main()
        {
            Application.EnableVisualStyles();
            MessageWindow window = new MessageWindow();
            ClipboardSafe.OwnerWindow = window.Handle;
            ClipboardSnapshot original = ClipboardSnapshot.Take();
            using (StaWorker worker = new StaWorker())
            {
                ClipboardRestore.Start(worker);
                try
                {
                    int workerId = 0, completed = 0;
                    bool sameThread = true, sta = true;
                    worker.Invoke(delegate { workerId = Thread.CurrentThread.ManagedThreadId; });
                    for (int i = 0; i < 100; i++)
                        worker.Post(delegate
                        {
                            sameThread &= workerId == Thread.CurrentThread.ManagedThreadId;
                            sta &= Thread.CurrentThread.GetApartmentState() == ApartmentState.STA;
                            Interlocked.Increment(ref completed);
                        });
                    Check("100 jobs reuse one STA in order", PumpUntil(delegate { return completed == 100; }, 3000) && sameThread && sta);

                    ClipboardSafe.SetText("before-runtime-test");
                    uint before = Native.GetClipboardSequenceNumber();
                    string result = null;
                    int finished = 0;
                    worker.Post(delegate
                    {
                        result = ClipboardSafe.WaitForText(before, 1500);
                        Interlocked.Exchange(ref finished, 1);
                    });
                    // The main UI loop receives WM_CLIPBOARDUPDATE while the
                    // worker waits. No manual signal is used for this check.
                    using (System.Windows.Forms.Timer writer = new System.Windows.Forms.Timer())
                    {
                        writer.Interval = 30;
                        writer.Tick += delegate { writer.Stop(); ClipboardSafe.SetText("event-arrived"); };
                        writer.Start();
                        Check("real clipboard event wakes waiting worker", PumpUntil(delegate { return finished != 0; }, 2000) && result == "event-arrived");
                    }

                    // A copy can finish before the waiter even starts.
                    before = Native.GetClipboardSequenceNumber();
                    ClipboardSafe.SetText("already-written");
                    worker.Invoke(delegate { result = ClipboardSafe.WaitForText(before, 100); });
                    Check("write before wait is retained", result == "already-written");

                    before = Native.GetClipboardSequenceNumber();
                    ClipboardSafe.NotifyWrite();
                    worker.Invoke(delegate { result = ClipboardSafe.WaitForText(before, 35); });
                    Check("stale notification does not copy stale text", result == null);

                    // Failure to register a listener must retain bounded polling.
                    ClipboardSafe.StopListening(window.Handle);
                    before = Native.GetClipboardSequenceNumber();
                    finished = 0;
                    worker.Post(delegate
                    {
                        result = ClipboardSafe.WaitForText(before, 500);
                        Interlocked.Exchange(ref finished, 1);
                    });
                    ClipboardSafe.SetText("fallback-written");
                    Check("missing listener retains clipboard support", PumpUntil(delegate { return finished != 0; }, 1000) && result == "fallback-written");
                    ClipboardSafe.StartListening(window.Handle);

                    // Model the paste consumer owning the clipboard already.
                    // Scheduling must neither open it nor miss restoration.
                    ClipboardSnapshot saved = new ClipboardSnapshot();
                    saved.Text = "restored-after-consumer";
                    Check("stage consumer text", ClipboardSafe.SetText("staged-for-consumer"));
                    bool locked = false;
                    PumpUntil(delegate { locked = OpenClipboard(window.Handle); return locked; }, 1000);
                    Check("paste consumer can own clipboard", locked);
                    uint staged = Native.GetClipboardSequenceNumber();
                    try { worker.Invoke(delegate { ClipboardRestore.Schedule(saved, staged); }); }
                    finally { if (locked) CloseClipboard(); }
                    // Pump without repeatedly opening the clipboard while the
                    // restorer is trying to write it. Check after the timer.
                    Stopwatch restoreWatch = Stopwatch.StartNew();
                    PumpUntil(delegate { return restoreWatch.ElapsedMilliseconds > 900; }, 1100);
                    Check("restore scheduling never opens consumer clipboard", ClipboardSafe.GetText() == saved.Text);

                    // A failed job must not retire the resident apartment.
                    bool caught = false;
                    try { worker.Invoke(delegate { throw new InvalidOperationException("test"); }); }
                    catch (Exception) { caught = true; }
                    int nextId = 0;
                    worker.Invoke(delegate { nextId = Thread.CurrentThread.ManagedThreadId; });
                    Check("worker survives a failed synchronous job", caught && nextId == workerId);

                    DirectReadCases(worker);
                    CapsCaptureCases(worker, window);

                    // Cancellation discards queued work, without blocking the UI
                    // on the running job or its COM calls.
                    using (ManualResetEvent entered = new ManualResetEvent(false))
                    using (ManualResetEvent release = new ManualResetEvent(false))
                    {
                        bool queuedRan = false;
                        Thread running = null;
                        worker.Post(delegate { running = Thread.CurrentThread; entered.Set(); release.WaitOne(); });
                        Check("worker accepts final job", entered.WaitOne(1000));
                        worker.Post(delegate { queuedRan = true; });
                        worker.Dispose();
                        release.Set();
                        Check("shutdown exits worker and drops queued job", running != null && running.Join(1000) && !queuedRan);
                    }
                }
                finally
                {
                    ClipboardRestore.Stop();
                    original.Restore();
                    ClipboardSafe.StopListening(window.Handle);
                    window.DestroyHandle();
                }
            }
            Console.WriteLine("runtime: " + _failures + " failure(s)");
            return _failures;
        }

        private static void Settle() { PumpUntil(delegate { return false; }, 150); }

        private static void PressCaps(bool stamped)
        {
            INPUT down = Native.KeyInput(Native.VK_CAPITAL, true), up = Native.KeyInput(Native.VK_CAPITAL, false);
            if (!stamped) { down.u.ki.dwExtraInfo = UIntPtr.Zero; up.u.ki.dwExtraInfo = UIntPtr.Zero; }
            Native.Send(new INPUT[] { down, up });
        }

        /// The Caps Lock state a case fix ends on is captured by the hook at
        /// the press, because no other thread sees the toggle until the
        /// foreground app has processed it. The hook runs on the worker here,
        /// as it runs on its own loop in the fixer.
        private static void CapsCaptureCases(StaWorker worker, MessageWindow window)
        {
            bool seated = false;
            IntPtr target = window.Handle;
            worker.Invoke(delegate
            {
                seated = Watcher.Watch(target, MessageWindow.WM_TRIGGER_CASE, Native.VK_CAPITAL,
                                       false, false, false, false, false);
            });
            Check("caps: hook seated", seated);
            Settle();
            int initial = Native.CapsLockState();
            try
            {
                for (int i = 0; i < 4; i++)
                {
                    int before = Native.CapsLockState();
                    Watcher.CapsAfterPress = -1;
                    PressCaps(false);
                    PumpUntil(delegate { return Watcher.CapsAfterPress != -1; }, 500);
                    int captured = Watcher.CapsAfterPress;
                    Settle();
                    int after = Native.CapsLockState();
                    Check("caps: press " + (i + 1) + " captured where the toggle ended (" + captured + "/" + after + ")",
                          after == (before ^ 1) && captured == after);
                }
                int mark = Watcher.CapsAfterPress;
                PressCaps(true);
                Settle();
                Check("caps: a press the fixer sent itself is not captured", Watcher.CapsAfterPress == mark);
            }
            finally
            {
                worker.Invoke(delegate { Watcher.Unwatch(MessageWindow.WM_TRIGGER_CASE); });
                Settle();
                if (Native.CapsLockState() != initial) { PressCaps(true); Settle(); }
            }
        }

        /// Reads the selection from the worker, as the fixer does, while this
        /// thread pumps -- the control answers EM_GETSEL on this thread.
        private static bool ReadDirect(StaWorker worker, Form form, out string text)
        {
            string got = null;
            bool ok = false;
            int done = 0;
            IntPtr handle = form.Handle;
            worker.Post(delegate
            {
                ok = DirectSelection.TryRead(handle, false, out got);
                Interlocked.Exchange(ref done, 1);
            });
            PumpUntil(delegate { return done != 0; }, 2000);
            text = got;
            return ok;
        }

        private static void DirectReadCases(StaWorker worker)
        {
            string th = new string(new char[] {
                (char)0x0E2A, (char)0x0E27, (char)0x0E31, (char)0x0E2A, (char)0x0E14, (char)0x0E35 });
            Form form = new Form();
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new System.Drawing.Point(-2000, -2000);
            TextBox box = new TextBox();
            box.Multiline = true;
            RichTextBox rich = new RichTextBox();
            TextBox secret = new TextBox();
            secret.UseSystemPasswordChar = true;
            Button button = new Button();
            foreach (Control c in new Control[] { box, rich, secret, button }) form.Controls.Add(c);
            form.Show();
            try
            {
                string text;
                box.Focus();
                box.Text = "hello l;ylfu";
                box.Select(6, 6);
                Check("edit: exact selected text", ReadDirect(worker, form, out text) && text == "l;ylfu");

                box.Select(3, 0);
                Check("edit: nothing selected is an answer, not a failure", ReadDirect(worker, form, out text) && text == "");

                box.Text = "abc " + th;
                box.Select(4, th.Length);
                Check("edit: Thai selection comes back byte for byte", ReadDirect(worker, form, out text) && text == th);

                box.Text = "line one\r\nsay l;ylfu";
                box.Select(14, 6);
                Check("edit: offsets after a line break", ReadDirect(worker, form, out text) && text == "l;ylfu");

                rich.Focus();
                rich.Text = "abc l;ylfu";
                rich.Select(4, 6);
                Check("rich edit: single-line selection", ReadDirect(worker, form, out text) && text == "l;ylfu");

                rich.Text = "line one\nsay l;ylfu";
                rich.Select(13, 6);
                Check("rich edit: declines once a line break could shift offsets", !ReadDirect(worker, form, out text));

                secret.Focus();
                secret.Text = "hunter2";
                secret.SelectAll();
                Check("password box is never read", !ReadDirect(worker, form, out text));

                button.Focus();
                Check("a non-text control falls back to the copy probe", !ReadDirect(worker, form, out text));
            }
            finally { form.Close(); form.Dispose(); }
        }
    }
}
