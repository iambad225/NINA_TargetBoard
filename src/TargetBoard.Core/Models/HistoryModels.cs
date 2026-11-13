using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TargetBoard.Core.Models {

    /// <summary>One light frame NINA saved, as logged by the plugin. Stored one per line in captures.jsonl.</summary>
    public sealed class CaptureRecord {
        public DateTime TimeUtc { get; set; }
        public double ExposureSeconds { get; set; }
        public string TargetName { get; set; } = "";
        public double? RaHours { get; set; }
        public double? DecDegrees { get; set; }
        public string Filter { get; set; } = "";
        public int Gain { get; set; } = -1;
        public string Binning { get; set; } = "";
        /// <summary>The board target this frame was matched to, if any.</summary>
        public string? TargetId { get; set; }
        public string? ImagePath { get; set; }
        /// <summary>
        /// The file NINA was running when it saved the frame, when NINA could say: the Advanced Sequencer's .json, or a
        /// Simple Sequencer target loaded from its own .xml. Null for a target loaded from a target set.
        /// </summary>
        public string? SourceFile { get; set; }
        /// <summary>The running sequence's title, as NINA stamps it on each frame: a target set's or a .json's file name.</summary>
        public string? SequenceTitle { get; set; }

        [JsonIgnore]
        public DateTime EndUtc => TimeUtc.AddSeconds(Math.Max(0, ExposureSeconds));
    }

    public enum MilestoneKind {
        /// <summary>The saved sequence file shows more frames done than last time.</summary>
        Progress,
        /// <summary>One filter/exposure line reached its planned count.</summary>
        PartComplete,
        /// <summary>Every line of the target reached its planned count.</summary>
        TargetComplete,
        /// <summary>A target that was complete has frames to do again (its progress was reset).</summary>
        Reopened
    }

    public enum MilestoneSource {
        /// <summary>Seen in the sequence file's counters (time = when NINA saved the file).</summary>
        SequenceFile,
        /// <summary>Seen live from saved frames (time = when the frame was taken).</summary>
        LiveCapture,
        /// <summary>Already true the first time the board read the file (time = file save time, so "on or before").</summary>
        FirstScan,
        /// <summary>You marked the target done or not done on the board (time = when you did it).</summary>
        Manual
    }

    public sealed class Milestone {
        public string Id { get; set; } = "";
        public DateTime TimeUtc { get; set; }
        public MilestoneKind Kind { get; set; }
        public MilestoneSource Source { get; set; }
        public string TargetId { get; set; } = "";
        public string TargetName { get; set; } = "";
        public string? Filter { get; set; }
        public double? ExposureSeconds { get; set; }
        public int FramesAdded { get; set; }
        public int Done { get; set; }
        public int Planned { get; set; }
        public string SourceFile { get; set; } = "";

        public string Describe() {
            string part = Filter == null ? "" : $"{Filter} {Fmt.Seconds(ExposureSeconds ?? 0)}";
            string when = Source == MilestoneSource.FirstScan ? " (on or before this date)" : "";
            if (Source == MilestoneSource.Manual) {
                return Kind == MilestoneKind.Reopened ? $"{TargetName} marked not done" : $"{TargetName} marked done";
            }
            return Kind switch {
                MilestoneKind.TargetComplete => $"{TargetName} complete{when}",
                MilestoneKind.PartComplete => $"{TargetName}: {part} complete {Done}/{Planned}{when}",
                MilestoneKind.Reopened => $"{TargetName} has frames to do again",
                _ => $"{TargetName}: +{FramesAdded} {part} frames ({Done}/{Planned})"
            };
        }
    }

    public sealed class LineSnapshot {
        public int FileDone { get; set; }
        public int EffectiveDone { get; set; }
        public int Planned { get; set; }
    }

    public sealed class TargetSnapshot {
        public string TargetName { get; set; } = "";
        public DateTime FileWriteUtc { get; set; }
        public Dictionary<string, LineSnapshot> Lines { get; set; } = new();
        public bool Complete { get; set; }
        public DateTime? CompletedUtc { get; set; }
    }

    /// <summary>Everything stored in history.json (captures live in captures.jsonl).</summary>
    public sealed class HistoryData {
        public int Version { get; set; } = 1;
        public Dictionary<string, TargetSnapshot> Snapshots { get; set; } = new();
        public List<Milestone> Milestones { get; set; } = new();
    }
}
