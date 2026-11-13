using System;
using System.Collections.Generic;
using System.Linq;
using TargetBoard.Core.History;
using TargetBoard.Core.Models;
using TargetBoard.Core.Scheduling;

namespace TargetBoard.Core.Engine {

    /// <summary>Everything the target list shows about one target.</summary>
    public sealed class TargetSummary {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Color { get; init; } = "";
        public string SourceFile { get; init; } = "";
        public double RaHours { get; init; }
        public double DecDegrees { get; init; }
        public bool Hidden { get; init; }
        public string Notes { get; init; } = "";
        /// <summary>The board has events for this target but no scanned file contains it any more.</summary>
        public bool Missing { get; init; }
        public bool IsOpenEnded { get; init; }
        /// <summary>Every frame done, or marked done by you.</summary>
        public bool IsComplete { get; init; }
        /// <summary>You marked it done on the board.</summary>
        public bool MarkedDone { get; init; }
        public DateTime? CompletedUtc { get; init; }
        public DateTime FileWriteUtc { get; init; }
        public DateTime FileCreatedUtc { get; init; }

        public string? GroupId { get; init; }
        public string? GroupName { get; init; }
        public int GroupIndex { get; init; }
        /// <summary>Set when this standalone target repeats a panel of a group; the id of that panel.</summary>
        public string? CopyOfId { get; init; }
        public string? CopyOfName { get; init; }
        /// <summary>Standalone files that repeat this panel.</summary>
        public IReadOnlyList<LinkedCopy> Copies { get; init; } = Array.Empty<LinkedCopy>();

        public double Progress { get; init; }
        public double PlannedSeconds { get; init; }
        public double DoneSeconds { get; init; }
        public double RemainingSeconds { get; init; }
        public double CapturedSeconds { get; init; }
        public int LiveFrames { get; init; }
        public TimeSpan ScheduledAhead { get; init; }
        public TimeSpan Unscheduled { get; init; }
        /// <summary>Planned time ahead beyond what the sequence still needs (after frames were taken since planning).</summary>
        public TimeSpan OverPlanned { get; init; }

        public IReadOnlyList<ExposureLine> Lines { get; init; } = Array.Empty<ExposureLine>();
        public IReadOnlyList<CapturedLine> Captured { get; init; } = Array.Empty<CapturedLine>();
        public IReadOnlyList<ConditionSpec> Conditions { get; init; } = Array.Empty<ConditionSpec>();
        public IReadOnlyList<ConditionSpec> ImportedConditions { get; init; } = Array.Empty<ConditionSpec>();
    }

    public sealed class NightView {
        public DateOnly Night { get; init; }
        public TimeRange Span { get; init; }
        public TimeRange? Dark { get; init; }
        public double? MoonIllumination { get; init; }
        public TimeSpan Planned { get; init; }
        public bool IsTonight { get; init; }
        public bool IsPast { get; init; }
        /// <summary>Events of this night that ended with no frames, and with fewer frames than planned.</summary>
        public int Missed { get; init; }
        public int Partial { get; init; }
        /// <summary>Dark time is over and some events are not settled yet.</summary>
        public bool CanSettle { get; init; }
    }

    public sealed class EventView {
        public string Id { get; init; } = "";
        public string TargetId { get; init; } = "";
        public string Title { get; init; } = "";
        public string Color { get; init; } = "";
        public DateOnly Night { get; init; }
        public DateTime StartUtc { get; init; }
        public DateTime EndUtc { get; init; }
        public bool IsPast { get; init; }
        public bool TargetMissing { get; init; }
        public string Detail { get; init; } = "";
        public string? GroupName { get; init; }

        public bool Forced { get; init; }
        /// <summary>Imaging time that falls outside the target's conditions (still inside dark time).</summary>
        public TimeSpan OutsideConditions { get; init; }
        public EventStatus Status { get; init; }
        public bool IsSettled { get; init; }
        public double AchievedSeconds { get; init; }
        public int Frames { get; init; }
        /// <summary>The block as planned, when settling shrank it.</summary>
        public DateTime? PlannedStartUtc { get; init; }
        public DateTime? PlannedEndUtc { get; init; }
        /// <summary>"Done · 48 frames", "Partly done · 2h 10m of 4h 37m", "Missed".</summary>
        public string StatusText { get; init; } = "";
    }

    public sealed class SessionView {
        public string Key { get; init; } = "";
        public string? TargetId { get; init; }
        public string Title { get; init; } = "";
        public string Color { get; init; } = "";
        public DateOnly Night { get; init; }
        public DateTime StartUtc { get; init; }
        public DateTime EndUtc { get; init; }
        public int Frames { get; init; }
        public string Detail { get; init; } = "";
    }

    public sealed class MilestoneView {
        public string Id { get; init; } = "";
        public string TargetId { get; init; } = "";
        public string TargetName { get; init; } = "";
        public string Color { get; init; } = "";
        public DateOnly Night { get; init; }
        public DateTime TimeUtc { get; init; }
        public MilestoneKind Kind { get; init; }
        public MilestoneSource Source { get; init; }
        public string Text { get; init; } = "";
    }

    /// <summary>All the data one calendar view needs, built away from the UI thread.</summary>
    public sealed class BoardSnapshot {
        public BoardViewMode Mode { get; init; }
        public DateOnly Start { get; init; }
        public IReadOnlyList<NightView> Nights { get; init; } = Array.Empty<NightView>();
        public IReadOnlyList<EventView> Events { get; init; } = Array.Empty<EventView>();
        public IReadOnlyList<SessionView> Sessions { get; init; } = Array.Empty<SessionView>();
        public IReadOnlyList<MilestoneView> Milestones { get; init; } = Array.Empty<MilestoneView>();
        public string? ActiveTargetId { get; init; }
        public string ActiveTargetColor { get; init; } = "";
        /// <summary>Color of every target, for drawing drops of targets that have no events yet.</summary>
        public IReadOnlyDictionary<string, string> TargetColors { get; init; } = new Dictionary<string, string>();
        public IReadOnlyDictionary<string, string> TargetNames { get; init; } = new Dictionary<string, string>();
        public IReadOnlyDictionary<DateOnly, List<TimeRange>> ActiveWindows { get; init; } = new Dictionary<DateOnly, List<TimeRange>>();
        public IReadOnlyDictionary<DateOnly, List<(DateTime Utc, double Altitude)>> ActiveAltitude { get; init; } = new Dictionary<DateOnly, List<(DateTime, double)>>();
        /// <summary>Moon altitude through each night, when the Moon curve is switched on.</summary>
        public IReadOnlyDictionary<DateOnly, List<(DateTime Utc, double Altitude)>> MoonAltitude { get; init; } = new Dictionary<DateOnly, List<(DateTime, double)>>();
        public bool ShowMoon { get; init; }
        public bool CanUndo { get; init; }
        public string UndoLabel { get; init; } = "";
        public TimeZoneInfo Zone { get; init; } = TimeZoneInfo.Utc;
        public DateTime NowUtc { get; init; }
        /// <summary>Time axis for Night and Week views, as wall-clock hours after local noon (6 = 18:00, 18 = 06:00).</summary>
        public double AxisStartHours { get; init; } = 5;
        public double AxisEndHours { get; init; } = 20;
        public string Title { get; init; } = "";

        public static BoardSnapshot Empty { get; } = new();
    }

    public enum DragKind { New, Move, Resize }

    /// <summary>The result of trying a drop, shown live while dragging and committed on release.</summary>
    public sealed class PlacementPreview {
        public DragKind Kind { get; init; }
        public string TargetId { get; init; } = "";
        public string? EventId { get; init; }
        public DateOnly Night { get; init; }
        public PlacementResult Result { get; init; } = PlacementResult.Reject(TimeSpan.Zero, "");
        /// <summary>Placed with Shift held: the target's conditions were ignored (dark time still counts).</summary>
        public bool Forced { get; init; }
        /// <summary>Time of the placed block outside the target's conditions.</summary>
        public TimeSpan OutsideConditions { get; init; }
        /// <summary>Set when a missed past event is planned again: the original stays as the record of the missed night.</summary>
        public string? ReplanOfEventId { get; init; }
    }

    /// <summary>Several selected events dragged together: where each would land.</summary>
    public sealed class MultiMovePreview {
        public DateOnly Night { get; init; }
        /// <summary>One preview per event that moves (or, for a missed event, is planned again).</summary>
        public List<PlacementPreview> Items { get; init; } = new();
        /// <summary>Events that stay where they are (already happened, or running now).</summary>
        public List<string> Skipped { get; init; } = new();
        public PlacementOutcome Outcome { get; init; } = PlacementOutcome.Rejected;
        public string Message { get; init; } = "";
        public bool CanCommit => Outcome != PlacementOutcome.Rejected && Items.Count > 0 && Items.All(i => i.Result.CanCommit);
    }

    /// <summary>The frames of one event, for the filter view: forecast for the future, taken for the past.</summary>
    public sealed class FramePlanView {
        public string EventId { get; init; } = "";
        public string TargetId { get; init; } = "";
        public string Title { get; init; } = "";
        public string Color { get; init; } = "";
        public DateOnly Night { get; init; }
        public TimeRange Block { get; init; }
        /// <summary>Frames taken so far (past events, and the done part of a running one).</summary>
        public List<FrameSegment> Taken { get; init; } = new();
        /// <summary>Frames still expected in this event.</summary>
        public List<FrameSegment> Planned { get; init; } = new();
        public string Note { get; init; } = "";
    }

    /// <summary>What a settle, shift or carry changed, for the status line.</summary>
    public sealed class PlanChangeReport {
        public string Summary { get; set; } = "";
        public List<string> Details { get; } = new();
        public int Changed { get; set; }
        public override string ToString() => Details.Count == 0 ? Summary : Summary + " " + string.Join(" ", Details);
    }
}
