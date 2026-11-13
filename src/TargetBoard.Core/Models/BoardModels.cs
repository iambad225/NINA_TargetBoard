using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TargetBoard.Core.Models {

    /// <summary>What counts as "dark" when building a night.</summary>
    public enum DarkTimeMode {
        /// <summary>Sun below -18 degrees.</summary>
        Astronomical,
        /// <summary>Sun below -12 degrees. Useful at high latitudes in summer.</summary>
        Nautical
    }

    public enum ConditionKind {
        /// <summary>Target altitude at or above Value degrees.</summary>
        MinAltitude,
        /// <summary>Target altitude at or below Value degrees.</summary>
        MaxAltitude,
        /// <summary>Target at least Value degrees above the profile's custom horizon (or 0 degrees when none is set).</summary>
        AboveHorizon,
        /// <summary>Hour angle between Value and Value2 hours (-12..12, negative = east of meridian).</summary>
        HourAngle,
        /// <summary>Local clock time between Value and Value2, as minutes after midnight. Wraps past midnight when Value &gt; Value2.</summary>
        TimeWindow
    }

    public enum ConditionSource { Default, Sequence, User }

    /// <summary>A rule that limits when a target may be shot. All enabled rules must pass.</summary>
    public sealed class ConditionSpec {
        public ConditionKind Kind { get; set; }
        public double Value { get; set; }
        public double Value2 { get; set; }
        public bool Enabled { get; set; } = true;
        public ConditionSource Source { get; set; } = ConditionSource.User;

        public ConditionSpec Clone() => (ConditionSpec)MemberwiseClone();

        public static ConditionSpec MinAltitude(double degrees, ConditionSource source = ConditionSource.User)
            => new ConditionSpec { Kind = ConditionKind.MinAltitude, Value = degrees, Source = source };

        public static ConditionSpec AboveHorizon(double offset, ConditionSource source = ConditionSource.User)
            => new ConditionSpec { Kind = ConditionKind.AboveHorizon, Value = offset, Source = source };

        public override string ToString() {
            string text = Kind switch {
                ConditionKind.MinAltitude => $"Altitude ≥ {Value:0.#}°",
                ConditionKind.MaxAltitude => $"Altitude ≤ {Value:0.#}°",
                ConditionKind.AboveHorizon => Value == 0 ? "Above horizon" : $"Horizon + {Value:0.#}°",
                ConditionKind.HourAngle => $"Hour angle {Value:+0.##;-0.##;0}h to {Value2:+0.##;-0.##;0}h",
                ConditionKind.TimeWindow => $"Between {Fmt.ClockFromMinutes(Value)} and {Fmt.ClockFromMinutes(Value2)}",
                _ => Kind.ToString()
            };
            return Enabled ? text : text + " (off)";
        }

        /// <summary>A short key used to tell whether two condition lists differ.</summary>
        [JsonIgnore]
        public string CacheKey => $"{(int)Kind}:{Value:R}:{Value2:R}:{(Enabled ? 1 : 0)}";
    }

    public sealed class BoardSettings {
        /// <summary>Read NINA's default sequence folder and targets folder from the active profile.</summary>
        public bool UseProfileFolders { get; set; } = true;
        public List<string> ExtraFolders { get; set; } = new();
        public List<string> ExtraFiles { get; set; } = new();
        public bool IncludeSubfolders { get; set; } = true;

        public DarkTimeMode DarkTime { get; set; } = DarkTimeMode.Astronomical;

        /// <summary>Time added on top of pure exposure time for downloads, dithers, focus runs and flips.</summary>
        public double OverheadPercent { get; set; } = 15;

        /// <summary>Conditions given to a target the first time the board sees it.</summary>
        public double DefaultMinAltitude { get; set; } = 30;
        public bool DefaultAboveHorizon { get; set; } = true;
        public bool ImportSequenceConditions { get; set; } = true;

        public int SnapMinutes { get; set; } = 5;
        public int MinimumEventMinutes { get; set; } = 15;

        /// <summary>When false, events on the same night may not overlap (one scope).</summary>
        public bool AllowOverlap { get; set; }

        /// <summary>Log each saved light frame so finished work shows on the calendar.</summary>
        public bool LogCaptures { get; set; } = true;

        /// <summary>Captures further apart than this start a new "done" session.</summary>
        public int SessionGapMinutes { get; set; } = 45;

        public BoardViewMode LastView { get; set; } = BoardViewMode.Week;
        public TargetSort Sort { get; set; } = TargetSort.CreatedNewest;
        /// <summary>Show standalone files that copy a panel of a target set as their own rows.</summary>
        public bool ShowLinkedCopies { get; set; }

        /// <summary>Mouse wheel speed in the target list: 1 = Windows' usual step, smaller is slower.</summary>
        public double ListScrollSpeed { get; set; } = 1.0;
        /// <summary>Glide to the new position instead of jumping.</summary>
        public bool SmoothScrolling { get; set; } = true;

        /// <summary>
        /// Settle each finished night once, the next morning: fit partly done events to what was done and pass the
        /// missing time on. Nothing is changed during the night.
        /// </summary>
        public bool AutoSettle { get; set; } = true;
        /// <summary>Local time of day, in minutes after midnight, when last night is settled (default 08:00).</summary>
        public int SettleAtMinutes { get; set; } = 8 * 60;

        /// <summary>Draw the Moon's altitude through each night.</summary>
        public bool ShowMoon { get; set; }

        /// <summary>Width of the target list column, in pixels.</summary>
        public double ListPanelWidth { get; set; } = 370;
        /// <summary>How the left side is shared between the list and the details when they are open (as proportions).</summary>
        public double ListShare { get; set; } = 1;
        public double DetailsShare { get; set; } = 1;

        public BoardSettings Clone() {
            var c = (BoardSettings)MemberwiseClone();
            c.ExtraFolders = new List<string>(ExtraFolders);
            c.ExtraFiles = new List<string>(ExtraFiles);
            return c;
        }
    }

    public enum BoardViewMode { Night, Week, Month }

    public enum TargetSort {
        /// <summary>By the date the file was created, newest first.</summary>
        CreatedNewest,
        CreatedOldest,
        /// <summary>By the date NINA last saved the file.</summary>
        SavedNewest,
        Name,
        /// <summary>Furthest along first.</summary>
        Progress,
        /// <summary>Most time still to place on the calendar first.</summary>
        ToPlace
    }

    /// <summary>What the board remembers about a target between scans.</summary>
    public sealed class TargetRecord {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public double RaHours { get; set; }
        public double DecDegrees { get; set; }
        public string SourceFile { get; set; } = "";
        public string Color { get; set; } = "";
        public bool Hidden { get; set; }
        /// <summary>
        /// When you marked this target done on the board, whatever its sequence says. Null when not marked.
        /// Only the board stores this; the sequence file is never changed.
        /// </summary>
        public DateTime? MarkedDoneUtc { get; set; }
        public string Notes { get; set; } = "";
        public List<ConditionSpec> Conditions { get; set; } = new();
        public bool ConditionsInitialized { get; set; }
        public DateTime FirstSeenUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
    }

    /// <summary>One step of the undo history: the plan as it was before a change.</summary>
    public sealed class UndoStep {
        public string Label { get; set; } = "";
        public List<PlanEvent> Events { get; set; } = new();
    }

    /// <summary>The undo history, kept in its own file so it survives NINA restarts.</summary>
    public sealed class UndoData {
        public int Version { get; set; } = 1;
        public List<UndoStep> Steps { get; set; } = new();
    }

    /// <summary>What happened to an event once its night was over.</summary>
    public enum EventOutcome {
        /// <summary>The planned imaging time was done (within a small margin).</summary>
        Done,
        /// <summary>Some frames were taken, fewer than planned.</summary>
        Partial,
        /// <summary>No frames were taken for it.</summary>
        Missed
    }

    /// <summary>
    /// A planned block of imaging time on one night. Times are UTC. Slewing, centering, focusing and the like are
    /// covered by the overhead percentage, not by the block.
    /// </summary>
    public sealed class PlanEvent {
        public string Id { get; set; } = "";
        public string TargetId { get; set; } = "";
        public DateOnly Night { get; set; }
        public DateTime StartUtc { get; set; }
        public DateTime EndUtc { get; set; }
        public string Note { get; set; } = "";

        /// <summary>Placed outside the target's conditions on purpose (Shift held). Settling never moves it.</summary>
        public bool Forced { get; set; }

        /// <summary>Set when the night was settled; the event is a record from then on.</summary>
        public EventOutcome? Outcome { get; set; }
        public DateTime? SettledUtc { get; set; }
        /// <summary>Imaging time the frames taken stand for (exposure time plus overhead), when settled.</summary>
        public double AchievedSeconds { get; set; }
        public int Frames { get; set; }
        /// <summary>The block as planned, before settling shrank it to what was done.</summary>
        public DateTime? PlannedStartUtc { get; set; }
        public DateTime? PlannedEndUtc { get; set; }

        [JsonIgnore]
        public TimeSpan Duration => EndUtc - StartUtc;
        [JsonIgnore]
        public TimeRange Range => new TimeRange(StartUtc, EndUtc);
        [JsonIgnore]
        public bool IsSettled => Outcome != null;

        public PlanEvent Clone() => (PlanEvent)MemberwiseClone();
    }

    /// <summary>Everything stored in board.json.</summary>
    public sealed class BoardData {
        public int Version { get; set; } = 1;
        public BoardSettings Settings { get; set; } = new();
        public Dictionary<string, TargetRecord> Targets { get; set; } = new();
        public List<PlanEvent> Events { get; set; } = new();
        /// <summary>
        /// The last night settled automatically. Nights up to here are never settled again by themselves (an undo
        /// stays undone). Set to the night before the first run, so older plans are left as they were.
        /// </summary>
        public DateOnly? AutoSettledThrough { get; set; }
    }
}
