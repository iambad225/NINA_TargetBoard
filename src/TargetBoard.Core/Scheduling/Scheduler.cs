using System;
using System.Collections.Generic;
using System.Linq;
using TargetBoard.Core.Models;

namespace TargetBoard.Core.Scheduling {

    public enum PlacementOutcome {
        /// <summary>The whole requested length fits.</summary>
        Fits,
        /// <summary>Part fits; the rest is overflow and stays unscheduled for another night.</summary>
        Clipped,
        /// <summary>Nothing can go here.</summary>
        Rejected
    }

    public sealed class PlacementOptions {
        public int SnapMinutes { get; init; } = 5;
        public TimeSpan MinimumLength { get; init; } = TimeSpan.FromMinutes(15);
        public bool AllowOverlap { get; init; }
        /// <summary>Nothing may start before this (usually "now").</summary>
        public DateTime? NotBeforeUtc { get; init; }
    }

    public sealed record PlacementResult(PlacementOutcome Outcome, TimeRange? Placed, TimeSpan Requested, TimeSpan Overflow, string Message) {
        public bool CanCommit => Outcome != PlacementOutcome.Rejected && Placed != null;

        public static PlacementResult Reject(TimeSpan requested, string message)
            => new(PlacementOutcome.Rejected, null, requested, requested, message);
    }

    /// <summary>
    /// Decides where a block of time lands on one night.
    /// Rules: it must sit inside the target's valid windows (dark time clipped by its conditions),
    /// it may not overlap other events unless allowed, and whatever does not fit becomes overflow.
    /// The block starts where it was dropped; it never jumps to a later window to swallow the overflow.
    /// </summary>
    public static class Scheduler {

        public static List<TimeRange> FreeWindows(IEnumerable<TimeRange> validWindows, IEnumerable<TimeRange> busy, PlacementOptions options) {
            var free = options.AllowOverlap ? validWindows.Where(w => !w.IsEmpty).OrderBy(w => w.StartUtc).ToList() : TimeRange.Subtract(validWindows, busy);
            if (options.NotBeforeUtc is DateTime notBefore) {
                free = TimeRange.Subtract(free, new[] { new TimeRange(DateTime.MinValue, SkyMathUtc(notBefore)) });
            }
            return free.Where(w => w.Duration >= options.MinimumLength).ToList();
        }

        /// <summary>
        /// Places <paramref name="duration"/> starting near <paramref name="desiredStartUtc"/>.
        /// With no desired start (a drop on a whole day, as in month view) it takes the first free slot.
        /// </summary>
        public static PlacementResult Place(DateTime? desiredStartUtc, TimeSpan duration, IReadOnlyList<TimeRange> validWindows, IEnumerable<TimeRange> busy, PlacementOptions options) {
            if (duration <= TimeSpan.Zero) return PlacementResult.Reject(duration, "Nothing left to schedule for this target.");
            if (validWindows.Count == 0) return PlacementResult.Reject(duration, "The target never meets its conditions during dark time this night.");

            var free = FreeWindows(validWindows, busy, options);
            if (free.Count == 0) {
                bool past = options.NotBeforeUtc != null && validWindows.All(w => w.EndUtc <= options.NotBeforeUtc.Value);
                return PlacementResult.Reject(duration, past ? "That time has passed." : "No free dark time left that meets the conditions.");
            }

            TimeRange window;
            DateTime start;
            if (desiredStartUtc is DateTime wanted) {
                wanted = Snap(SkyMathUtc(wanted), options.SnapMinutes);
                // Nearest free slot; on a tie (dropped in the middle of another event) take the one after it.
                window = free.OrderBy(w => w.DistanceTo(wanted)).ThenByDescending(w => w.StartUtc).First();
                if (window.Contains(wanted)) {
                    start = wanted;
                } else if (wanted < window.StartUtc) {
                    start = window.StartUtc;
                } else {
                    // Dropped after the window: end at the window's close and start as early as the length needs.
                    start = window.EndUtc - duration;
                    if (start < window.StartUtc) start = window.StartUtc;
                    start = SnapUpInside(start, window, options.SnapMinutes);
                }
            } else {
                window = free[0];
                start = window.StartUtc;
            }

            var end = start + duration;
            if (end > window.EndUtc) end = window.EndUtc;
            if (end - start < options.MinimumLength) {
                // Too short where it was dropped; slide back so at least the minimum fits in this window.
                start = window.EndUtc - options.MinimumLength;
                if (start < window.StartUtc) start = window.StartUtc;
                end = start + (duration < options.MinimumLength ? duration : options.MinimumLength);
                if (end > window.EndUtc) end = window.EndUtc;
            }

            var placed = new TimeRange(start, end);
            var overflow = duration - placed.Duration;
            if (overflow < TimeSpan.FromSeconds(30)) {
                return new PlacementResult(PlacementOutcome.Fits, placed, duration, TimeSpan.Zero, $"Fits: {Fmt.Duration(placed.Duration)}");
            }
            string why = WhyClipped(placed, validWindows, free);
            return new PlacementResult(PlacementOutcome.Clipped, placed, duration, overflow,
                $"{Fmt.Duration(placed.Duration)} fits. {Fmt.Duration(overflow)} {why} — drag the rest onto another night.");
        }

        private static string WhyClipped(TimeRange placed, IReadOnlyList<TimeRange> valid, List<TimeRange> free) {
            var validWindow = valid.FirstOrDefault(v => v.StartUtc <= placed.StartUtc && v.EndUtc >= placed.EndUtc);
            if (!validWindow.IsEmpty && validWindow.EndUtc > placed.EndUtc + TimeSpan.FromSeconds(30)) return "runs into another event";
            return "runs past dawn or the target's limits";
        }

        public static DateTime Snap(DateTime utc, int minutes) {
            if (minutes <= 1) return utc;
            long step = TimeSpan.FromMinutes(minutes).Ticks;
            long ticks = (utc.Ticks + step / 2) / step * step;
            return new DateTime(ticks, DateTimeKind.Utc);
        }

        private static DateTime SnapUpInside(DateTime utc, TimeRange window, int minutes) {
            if (minutes <= 1) return utc;
            long step = TimeSpan.FromMinutes(minutes).Ticks;
            var up = new DateTime((utc.Ticks + step - 1) / step * step, DateTimeKind.Utc);
            return up < window.EndUtc ? up : utc;
        }

        private static DateTime SkyMathUtc(DateTime t) => Sky.SkyMath.AsUtc(t);

        /// <summary>
        /// Planned time still ahead of now for a target (past parts of events count as done or missed; settled events are records).
        /// </summary>
        public static TimeSpan ScheduledAhead(IEnumerable<PlanEvent> events, string targetId, DateTime nowUtc) {
            var total = TimeSpan.Zero;
            foreach (var e in events) {
                if (e.TargetId != targetId || e.IsSettled || e.EndUtc <= nowUtc) continue;
                var start = e.StartUtc > nowUtc ? e.StartUtc : nowUtc;
                total += e.EndUtc - start;
            }
            return total;
        }

        /// <summary>Time a target still needs, with overhead, that no future event covers yet.</summary>
        public static TimeSpan Unscheduled(double remainingExposureSeconds, double overheadPercent, TimeSpan scheduledAhead) {
            var needed = TimeSpan.FromSeconds(Math.Max(0, remainingExposureSeconds) * (1 + Math.Max(0, overheadPercent) / 100.0));
            var left = needed - scheduledAhead;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }
}
