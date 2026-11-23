using System;
using System.Diagnostics;

namespace LazerTagHostLibrary
{
    // Existing packet deadlines are DateTimes. Only the origin is a wall-clock
    // reading; every subsequent interval advances with the monotonic timer.
    public sealed class MatchClock
    {
        private readonly DateTime _origin;
        private readonly Func<TimeSpan> _elapsed;

        public MatchClock(DateTime origin, Func<TimeSpan> elapsed)
        {
            _origin = origin;
            _elapsed = elapsed ?? throw new ArgumentNullException(nameof(elapsed));
        }

        public DateTime Now => _origin.Add(_elapsed());

        public static MatchClock Start()
        {
            var origin = DateTime.UtcNow;
            var timer = Stopwatch.StartNew();
            return new MatchClock(origin, () => timer.Elapsed);
        }
    }
}
