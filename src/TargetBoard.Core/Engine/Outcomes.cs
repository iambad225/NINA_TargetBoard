using System;
using System.Collections.Generic;
using System.Linq;
using TargetBoard.Core.Models;
using TargetBoard.Core.Sky;

namespace TargetBoard.Core.Engine {

    /// <summary>Where an event stands, worked out from the frame log. Shown live; only settling changes the plan.</summary>
    public enum EventStatus {
        Upcoming,
        Running,
        Done,
        Partial,
        Missed,
        /// <summary>The night is over but frames are not being logged, so the board cannot tell.</summary>
        Unknown
    }

    public sealed class EventProgress {
        public string EventId { get; init; } = "";
        public EventStatus Status { get; init; }
        /// <summary>Exposure time taken, plus overhead: comparable with the event's length.</summary>
        public double AchievedSeconds { get; init; }
        public double ExposureSeconds { get; init; }
        public int Frames { get; init; }
        public DateTime? FirstUtc { get; init; }
        public DateTime? LastUtc { get; init; }
        public List<CaptureRecord> Captures { get; init; } = new();
    }

    public static class OutcomeTracker {

        /// <summary>Shortfall allowed before an event counts as only partly done: 5% of its imaging time, at least 5 minutes.</summary>
        public static TimeSpan Tolerance(TimeSpan planned)
            => TimeSpan.FromSeconds(Math.Max(300, planned.TotalSeconds * 0.05));

        /// <summary>
        /// Gives each logged frame to an event of the same target on the same night: the event it falls in, otherwise
        /// the nearest one. Then works out each event's status. Frames for a target with no event that night count for none.
        /// </summary>
        public static Dictionary<string, EventProgress> Evaluate(IEnumerable<PlanEvent> events, IEnumerable<CaptureRecord> captures,
                                                                 double overheadPercent, DateTime nowUtc, TimeZoneInfo zone, bool logging) {
            var list = events.ToList();
            var byKey = list.GroupBy(e => (e.TargetId, e.Night)).ToDictionary(g => g.Key, g => g.OrderBy(e => e.StartUtc).ToList());
            var assigned = list.ToDictionary(e => e.Id, _ => new List<CaptureRecord>());

            foreach (var c in captures) {
                if (c.TargetId == null) continue;
                var night = NightMath.NightOf(c.TimeUtc, zone);
                if (!byKey.TryGetValue((c.TargetId, night), out var candidates)) continue;
                var best = candidates.FirstOrDefault(e => e.Range.Contains(c.TimeUtc))
                           ?? candidates.OrderBy(e => e.Range.DistanceTo(c.TimeUtc)).First();
                assigned[best.Id].Add(c);
            }

            double factor = 1 + Math.Max(0, overheadPercent) / 100.0;
            var result = new Dictionary<string, EventProgress>();
            foreach (var e in list) {
                var mine = assigned[e.Id].OrderBy(c => c.TimeUtc).ToList();
                double exposure = mine.Sum(c => c.ExposureSeconds);
                double achieved = exposure * factor;
                var planned = e.Duration;
                EventStatus status;
                if (nowUtc < e.StartUtc) status = EventStatus.Upcoming;
                else if (nowUtc < e.EndUtc) status = EventStatus.Running;
                else if (e.Outcome is EventOutcome o) status = o switch { EventOutcome.Done => EventStatus.Done, EventOutcome.Partial => EventStatus.Partial, _ => EventStatus.Missed };
                else if (mine.Count == 0 && !logging) status = EventStatus.Unknown;
                else if (mine.Count == 0) status = EventStatus.Missed;
                else if (achieved >= (planned - Tolerance(planned)).TotalSeconds) status = EventStatus.Done;
                else status = EventStatus.Partial;

                result[e.Id] = new EventProgress {
                    EventId = e.Id,
                    Status = status,
                    AchievedSeconds = e.IsSettled && mine.Count == 0 ? e.AchievedSeconds : achieved,
                    ExposureSeconds = exposure,
                    Frames = e.IsSettled && mine.Count == 0 ? e.Frames : mine.Count,
                    FirstUtc = mine.Count > 0 ? mine[0].TimeUtc : null,
                    LastUtc = mine.Count > 0 ? mine.Max(c => c.EndUtc) : null,
                    Captures = mine
                };
            }
            return result;
        }
    }

    /// <summary>A run of frames with one filter and exposure, planned or taken.</summary>
    public sealed record FrameSegment(string Filter, double ExposureSeconds, int Frames, DateTime StartUtc, DateTime EndUtc);

    /// <summary>One upcoming slot of imaging time for a target.</summary>
    public sealed record FrameSlot(string Id, DateTime StartUtc, DateTime EndUtc);

    /// <summary>
    /// Works out which frames each upcoming event of a target will take, in the order the sequence shoots them:
    /// row by row (each row finished before the next) or rotating (one frame of each row in turn).
    /// A frame costs its exposure plus overhead; only whole frames fit.
    /// </summary>
    public static class FramePlanner {

        public static Dictionary<string, List<FrameSegment>> Forecast(IReadOnlyList<ExposureLine> lines, ShootingOrder order,
                                                                       double overheadPercent, IEnumerable<FrameSlot> slots) {
            double factor = 1 + Math.Max(0, overheadPercent) / 100.0;
            var remaining = lines.Select(l => l.Remaining).ToArray();
            var result = new Dictionary<string, List<FrameSegment>>();
            int next = 0;

            foreach (var slot in slots.OrderBy(s => s.StartUtc)) {
                var segments = new List<FrameSegment>();
                var t = slot.StartUtc;
                while (true) {
                    int i = Pick(remaining, order, next);
                    if (i < 0) break;
                    var cost = TimeSpan.FromSeconds(Math.Max(1, lines[i].ExposureSeconds) * factor);
                    if (t + cost > slot.EndUtc + TimeSpan.FromSeconds(1)) break;
                    var end = t + cost;
                    var last = segments.Count > 0 ? segments[^1] : null;
                    if (last != null && Same(last, lines[i])) {
                        segments[^1] = last with { Frames = last.Frames + 1, EndUtc = end };
                    } else {
                        segments.Add(new FrameSegment(lines[i].Filter, lines[i].ExposureSeconds, 1, t, end));
                    }
                    remaining[i]--;
                    t = end;
                    if (order == ShootingOrder.Rotate) next = (i + 1) % lines.Count;
                }
                result[slot.Id] = segments;
            }
            return result;
        }

        /// <summary>Frames actually taken, as runs of the same filter and exposure.</summary>
        public static List<FrameSegment> FromCaptures(IEnumerable<CaptureRecord> captures) {
            var segments = new List<FrameSegment>();
            foreach (var c in captures.OrderBy(c => c.TimeUtc)) {
                var filter = string.IsNullOrWhiteSpace(c.Filter) ? Sequences.SequenceReader.NoFilter : c.Filter;
                var last = segments.Count > 0 ? segments[^1] : null;
                if (last != null && string.Equals(last.Filter, filter, StringComparison.OrdinalIgnoreCase) && Math.Abs(last.ExposureSeconds - c.ExposureSeconds) < 0.01) {
                    segments[^1] = last with { Frames = last.Frames + 1, EndUtc = c.EndUtc > last.EndUtc ? c.EndUtc : last.EndUtc };
                } else {
                    segments.Add(new FrameSegment(filter, c.ExposureSeconds, 1, c.TimeUtc, c.EndUtc));
                }
            }
            return segments;
        }

        private static int Pick(int[] remaining, ShootingOrder order, int from) {
            int n = remaining.Length;
            if (n == 0) return -1;
            if (order == ShootingOrder.RowByRow) {
                for (int i = 0; i < n; i++) if (remaining[i] > 0) return i;
                return -1;
            }
            for (int k = 0; k < n; k++) {
                int i = (from + k) % n;
                if (remaining[i] > 0) return i;
            }
            return -1;
        }

        private static bool Same(FrameSegment s, ExposureLine l)
            => string.Equals(s.Filter, l.Filter, StringComparison.OrdinalIgnoreCase) && Math.Abs(s.ExposureSeconds - l.ExposureSeconds) < 0.01;
    }
}
