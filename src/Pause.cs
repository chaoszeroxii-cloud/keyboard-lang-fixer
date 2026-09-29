// ---------------------------------------------------------------------------
//  Short waits that actually are short.
// ---------------------------------------------------------------------------
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace KbFix
{
    /// Thread.Sleep(1) sleeps until the next system timer tick, which is
    /// 15.6 ms unless something raised the resolution -- and on Windows 11
    /// another process raising it no longer counts for this one. Every poll
    /// loop in a fix used it, so noticing that the user had let go of the
    /// hotkey cost anywhere from 0 to 15.6 ms, measured at a median of ~8 ms
    /// on a fix that took ~28 ms end to end.
    ///
    /// A high-resolution waitable timer (Windows 10 1803+) wakes within about
    /// half a millisecond without changing the resolution for anyone else.
    internal static class Pause
    {
        private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
        private const uint TIMER_ALL_ACCESS = 0x1F0003;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string name, uint flags, uint access);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period,
                                                    IntPtr completion, IntPtr arg, bool resume);
        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(IntPtr handle, uint ms);

        [ThreadStatic] private static IntPtr _timer;
        [ThreadStatic] private static bool _unavailable;

        /// Waits roughly $ms milliseconds, fractions included. Falls back to
        /// Thread.Sleep where the timer cannot be created (older Windows).
        public static void For(double ms)
        {
            if (!_unavailable && _timer == IntPtr.Zero)
            {
                _timer = CreateWaitableTimerExW(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
                                                TIMER_ALL_ACCESS);
                if (_timer == IntPtr.Zero) _unavailable = true;
            }
            if (_unavailable)
            {
                Thread.Sleep(Math.Max(1, (int)Math.Ceiling(ms)));
                return;
            }
            // Negative = relative, in 100 ns units.
            long due = -(long)(ms * 10000.0);
            if (!SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                Thread.Sleep(Math.Max(1, (int)Math.Ceiling(ms)));
                return;
            }
            WaitForSingleObject(_timer, 1000);
        }

        /// The poll interval for "wait until this becomes true" loops.
        public static void Poll() { For(0.5); }
    }
}
