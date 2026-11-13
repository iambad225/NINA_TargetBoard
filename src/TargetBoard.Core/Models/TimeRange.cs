using System;
using System.Collections.Generic;
using System.Linq;

namespace TargetBoard.Core.Models {

    /// <summary>A UTC time span, start inclusive, end exclusive.</summary>
    public readonly record struct TimeRange(DateTime StartUtc, DateTime EndUtc) {
        public TimeSpan Duration => EndUtc > StartUtc ? EndUtc - StartUtc : TimeSpan.Zero;
        public bool IsEmpty => EndUtc <= StartUtc;
        public bool Contains(DateTime utc) => utc >= StartUtc && utc < EndUtc;
        public bool Overlaps(TimeRange other) => StartUtc < other.EndUtc && other.StartUtc < EndUtc;

        public TimeRange Intersect(TimeRange other) {
            var s = StartUtc > other.StartUtc ? StartUtc : other.StartUtc;
            var e = EndUtc < other.EndUtc ? EndUtc : other.EndUtc;
            return new TimeRange(s, e < s ? s : e);
        }

        /// <summary>How far an instant lies outside this range (zero when inside).</summary>
        public TimeSpan DistanceTo(DateTime utc) {
            if (utc < StartUtc) return StartUtc - utc;
            if (utc > EndUtc) return utc - EndUtc;
            return TimeSpan.Zero;
        }

        /// <summary>Removes every busy span from a set of windows. Inputs need not be sorted.</summary>
        public static List<TimeRange> Subtract(IEnumerable<TimeRange> windows, IEnumerable<TimeRange> busy) {
            var busySorted = busy.Where(b => !b.IsEmpty).OrderBy(b => b.StartUtc).ToList();
            var result = new List<TimeRange>();
            foreach (var w in windows.Where(w => !w.IsEmpty).OrderBy(w => w.StartUtc)) {
                var cursor = w.StartUtc;
                foreach (var b in busySorted) {
                    if (b.EndUtc <= cursor) continue;
                    if (b.StartUtc >= w.EndUtc) break;
                    if (b.StartUtc > cursor) result.Add(new TimeRange(cursor, b.StartUtc));
                    if (b.EndUtc > cursor) cursor = b.EndUtc;
                    if (cursor >= w.EndUtc) break;
                }
                if (cursor < w.EndUtc) result.Add(new TimeRange(cursor, w.EndUtc));
            }
            return result;
        }

        public static TimeSpan Total(IEnumerable<TimeRange> ranges) {
            var total = TimeSpan.Zero;
            foreach (var r in ranges) total += r.Duration;
            return total;
        }
    }
}
