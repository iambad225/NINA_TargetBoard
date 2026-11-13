using System;
using System.Collections.Generic;
using System.Linq;
using TargetBoard.Core.History;
using TargetBoard.Core.Models;

namespace TargetBoard.Core.Engine {

    /// <summary>A standalone file that repeats a panel of a target set.</summary>
    public sealed record LinkedCopy(string Id, string FileName, double Progress, bool Hidden);

    public static class TargetLinks {
        /// <summary>Two positions closer than this (1 arcminute) are the same pointing.</summary>
        public const double SamePointingDeg = 1.0 / 60.0;

        /// <summary>
        /// Finds standalone targets that copy a member of a group: same name (ignoring spaces and case) and the
        /// same pointing. Returns copy id → group member id.
        /// </summary>
        public static Dictionary<string, string> FindCopies(IReadOnlyList<SequenceTarget> targets) {
            var result = new Dictionary<string, string>();
            var members = targets.Where(t => t.GroupId != null)
                                 .GroupBy(t => TargetMatcher.Normalize(t.Name))
                                 .ToDictionary(g => g.Key, g => g.ToList());
            foreach (var t in targets.Where(t => t.GroupId == null)) {
                if (!members.TryGetValue(TargetMatcher.Normalize(t.Name), out var candidates)) continue;
                var best = candidates
                    .Select(m => (m, sep: TargetMatcher.SeparationDeg(m.RaHours, m.DecDegrees, t.RaHours, t.DecDegrees)))
                    .Where(x => x.sep <= SamePointingDeg)
                    .OrderBy(x => x.sep)
                    .Select(x => x.m)
                    .FirstOrDefault();
                if (best != null) result[t.Id] = best.Id;
            }
            return result;
        }
    }

    /// <summary>One entry in the target list: a single target, or a group (target set) with its panels.</summary>
    public sealed class TargetUnit {
        public string? GroupId { get; init; }
        public string Name { get; init; } = "";
        public List<TargetSummary> Members { get; init; } = new();

        public bool IsGroup => GroupId != null;
        public string SourceFile => Members.FirstOrDefault()?.SourceFile ?? "";
        public DateTime FileCreatedUtc => Members.Count == 0 ? default : Members.Max(m => m.FileCreatedUtc);
        public DateTime FileWriteUtc => Members.Count == 0 ? default : Members.Max(m => m.FileWriteUtc);
        public double PlannedSeconds => Members.Sum(m => m.PlannedSeconds);
        public double DoneSeconds => Members.Sum(m => m.DoneSeconds);
        public double RemainingSeconds => Members.Sum(m => m.RemainingSeconds);
        public double Progress => PlannedSeconds > 0 ? DoneSeconds / PlannedSeconds : 0;
        public TimeSpan Unscheduled => TimeSpan.FromTicks(Members.Sum(m => m.Unscheduled.Ticks));
        public TimeSpan ScheduledAhead => TimeSpan.FromTicks(Members.Sum(m => m.ScheduledAhead.Ticks));
        public bool IsComplete => Members.Count > 0 && Members.All(m => m.IsComplete);
        public bool Hidden => Members.Count > 0 && Members.All(m => m.Hidden);
    }

    public static class TargetOrdering {
        /// <summary>Folds group members into their group and sorts the result. Members keep their file order.</summary>
        public static List<TargetUnit> Arrange(IEnumerable<TargetSummary> targets, TargetSort sort) {
            var units = new List<TargetUnit>();
            foreach (var g in targets.GroupBy(t => t.GroupId ?? "single:" + t.Id)) {
                var first = g.First();
                if (first.GroupId != null) {
                    units.Add(new TargetUnit {
                        GroupId = first.GroupId,
                        Name = first.GroupName ?? first.Name,
                        Members = g.OrderBy(m => m.GroupIndex).ToList()
                    });
                } else {
                    units.Add(new TargetUnit { Name = first.Name, Members = new List<TargetSummary> { first } });
                }
            }

            var byName = StringComparer.CurrentCultureIgnoreCase;
            IOrderedEnumerable<TargetUnit> ordered = sort switch {
                TargetSort.CreatedOldest => units.OrderBy(u => u.FileCreatedUtc),
                TargetSort.SavedNewest => units.OrderByDescending(u => u.FileWriteUtc),
                TargetSort.Name => units.OrderBy(u => u.Name, byName),
                TargetSort.Progress => units.OrderByDescending(u => u.Progress),
                TargetSort.ToPlace => units.OrderByDescending(u => u.Unscheduled),
                _ => units.OrderByDescending(u => u.FileCreatedUtc)
            };
            return ordered.ThenBy(u => u.Name, byName).ToList();
        }
    }

    /// <summary>A whole group dropped at once: its panels placed one after another.</summary>
    public sealed class GroupPlacementPreview {
        public string GroupId { get; init; } = "";
        public string GroupName { get; init; } = "";
        public DateOnly Night { get; init; }
        public List<PlacementPreview> Panels { get; init; } = new();
        public List<string> NotPlaced { get; init; } = new();
        public TimeSpan Overflow { get; init; }
        public string Message { get; init; } = "";
        public bool CanCommit => Panels.Any(p => p.Result.CanCommit);
    }
}
