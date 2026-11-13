using System;
using System.Collections.Generic;
using System.Linq;
using TargetBoard.Core.Models;

namespace TargetBoard.Core.Scheduling {

    /// <summary>One event the cascade may move.</summary>
    public sealed class ReflowItem {
        public string Id { get; init; } = "";
        public string TargetId { get; init; } = "";
        public DateOnly Night { get; init; }
        /// <summary>Where it would like to start (its current start, or where a shift asked for).</summary>
        public DateTime DesiredStartUtc { get; init; }
        public TimeSpan Length { get; init; }
        /// <summary>Wins a tie for the same start time (events the user just moved).</summary>
        public bool Priority { get; init; }
    }

    public sealed class ReflowOptions {
        /// <summary>Nothing may start before this (usually "now").</summary>
        public DateTime NotBeforeUtc { get; init; } = DateTime.MinValue;
        /// <summary>An event may be shortened to fit its night, but never below this.</summary>
        public TimeSpan MinimumLength { get; init; } = TimeSpan.FromMinutes(15);
        /// <summary>How many nights ahead an event may be pushed before it is given up.</summary>
        public int HorizonNights { get; init; } = 30;
        public bool AllowOverlap { get; init; }
    }

    /// <param name="Overflow">Time cut off to make the event fit; it goes back to "to place".</param>
    public sealed record ReflowPlacement(string Id, DateOnly Night, TimeRange Block, bool Moved, TimeSpan Overflow);

    public sealed class ReflowResult {
        public List<ReflowPlacement> Placed { get; } = new();
        /// <summary>Events that found no slot within the horizon. Their time goes back to "to place".</summary>
        public List<string> Unplaced { get; } = new();
    }

    /// <summary>
    /// The cascade: events are placed one at a time, earliest wanted start first. An event nothing pushes stays exactly
    /// where it is. A pushed event takes the first slot at or after where it wants to be on its own night where it fits
    /// whole; if there is none, it is shortened to the first slot that still holds the minimum length (windows shrink a
    /// few minutes a night, so a full-window event shifted a day would otherwise never fit). Only when its night has no
    /// room at all does it go to the following nights, and it may push others in turn. Events are never split.
    /// </summary>
    public static class Reflow {

        /// <param name="windows">Where a target may be shot on a night (dark time clipped by its conditions).</param>
        /// <param name="fixedBusy">Blocks on a night that never move (past, running, forced, records).</param>
        public static ReflowResult Run(IEnumerable<ReflowItem> items,
                                       Func<string, DateOnly, IReadOnlyList<TimeRange>> windows,
                                       Func<DateOnly, IEnumerable<TimeRange>> fixedBusy,
                                       ReflowOptions options) {
            var result = new ReflowResult();
            var placedByNight = new Dictionary<DateOnly, List<TimeRange>>();

            List<TimeRange> Busy(DateOnly night) {
                if (!placedByNight.TryGetValue(night, out var list)) {
                    list = fixedBusy(night).Where(b => !b.IsEmpty).ToList();
                    placedByNight[night] = list;
                }
                return list;
            }

            foreach (var item in items.OrderBy(i => i.DesiredStartUtc).ThenByDescending(i => i.Priority).ThenBy(i => i.Id, StringComparer.Ordinal)) {
                bool done = false;
                for (int k = 0; k <= options.HorizonNights && !done; k++) {
                    var night = item.Night.AddDays(k);
                    var from = k == 0 ? item.DesiredStartUtc : DateTime.MinValue;
                    if (from < options.NotBeforeUtc) from = options.NotBeforeUtc;
                    var w = windows(item.TargetId, night);
                    var busy = options.AllowOverlap ? (IEnumerable<TimeRange>)Array.Empty<TimeRange>() : Busy(night);
                    var length = item.Length;
                    var start = EarliestFit(w, busy, from, item.Length);
                    if (start == null) {
                        var cut = FirstRoom(w, busy, from, options.MinimumLength < item.Length ? options.MinimumLength : item.Length);
                        if (cut is not (DateTime cs, TimeSpan room)) continue;
                        start = cs;
                        length = room < item.Length ? room : item.Length;
                    }
                    var s = start.Value;
                    var block = new TimeRange(s, s + length);
                    if (!options.AllowOverlap) Busy(night).Add(block);
                    result.Placed.Add(new ReflowPlacement(item.Id, night, block, k > 0 || s != item.DesiredStartUtc || length != item.Length, item.Length - length));
                    done = true;
                }
                if (!done) result.Unplaced.Add(item.Id);
            }
            return result;
        }

        /// <summary>The first free slot at or after <paramref name="from"/> that holds at least <paramref name="minimum"/>, and how much it holds.</summary>
        public static (DateTime Start, TimeSpan Room)? FirstRoom(IReadOnlyList<TimeRange> windows, IEnumerable<TimeRange> busyBlocks, DateTime from, TimeSpan minimum) {
            foreach (var free in TimeRange.Subtract(windows, busyBlocks)) {
                var s = free.StartUtc > from ? free.StartUtc : from;
                if (free.EndUtc - s >= minimum) return (s, free.EndUtc - s);
            }
            return null;
        }

        /// <summary>The earliest start at or after <paramref name="from"/> where the whole length fits inside a window and touches no busy block.</summary>
        public static DateTime? EarliestFit(IReadOnlyList<TimeRange> windows, IEnumerable<TimeRange> busyBlocks, DateTime from, TimeSpan length) {
            foreach (var free in TimeRange.Subtract(windows, busyBlocks)) {
                var s = free.StartUtc > from ? free.StartUtc : from;
                if (s + length <= free.EndUtc) return s;
            }
            return null;
        }
    }
}
