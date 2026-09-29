// ---------------------------------------------------------------------------
//  Per-press stage timestamps, written to the log so a latency probe can see
//  where the time went instead of only how much of it there was.
// ---------------------------------------------------------------------------
using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace KbFix
{
    internal sealed class Timeline
    {
        private const int Capacity = 32;
        private readonly long _origin;
        private readonly string[] _labels = new string[Capacity];
        private readonly long[] _ticks = new long[Capacity];
        private int _count;

        public Timeline(long originTicks)
        {
            _origin = originTicks;
        }

        public void Mark(string label)
        {
            if (_count >= Capacity) return;
            _ticks[_count] = Stopwatch.GetTimestamp();
            _labels[_count] = label;
            _count++;
        }

        /// The origin goes out as raw QPC ticks, which every process on the
        /// machine shares, so a probe in another process can line its own
        /// timestamps up against these without guessing at clock offsets.
        public string Format()
        {
            StringBuilder sb = new StringBuilder("  timing: qpc=");
            sb.Append(_origin.ToString(CultureInfo.InvariantCulture));
            sb.Append(" freq=").Append(Stopwatch.Frequency.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < _count; i++)
            {
                double ms = (_ticks[i] - _origin) * 1000.0 / Stopwatch.Frequency;
                sb.Append(' ').Append(_labels[i]).Append('=')
                  .Append(ms.ToString("0.000", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }
}
