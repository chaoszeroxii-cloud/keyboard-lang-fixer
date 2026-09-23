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
    }
}
