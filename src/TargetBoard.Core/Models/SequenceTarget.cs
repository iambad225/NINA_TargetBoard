using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TargetBoard.Core.Models {

    /// <summary>Planned and finished frames for one filter and exposure time, read from a sequence.</summary>
    public sealed class ExposureLine {
        public string Filter { get; set; } = "";
        public double ExposureSeconds { get; set; }
        public int Planned { get; set; }
        public int Done { get; set; }

        public string Key => MakeKey(Filter, ExposureSeconds);
        public int Remaining => Math.Max(0, Planned - Done);
        public double RemainingSeconds => Remaining * ExposureSeconds;
        public bool IsComplete => Planned > 0 && Done >= Planned;

        public static string MakeKey(string filter, double exposureSeconds)
            => (filter ?? "").Trim().ToUpperInvariant() + "|" + Math.Round(exposureSeconds, 3).ToString(CultureInfo.InvariantCulture);

        public ExposureLine Clone() => (ExposureLine)MemberwiseClone();
    }

    /// <summary>Frames NINA reports as taken for a target (the container's ExposureInfoList).</summary>
    public sealed class CapturedLine {
        public string Filter { get; set; } = "";
        public double ExposureSeconds { get; set; }
        public int Count { get; set; }
        public int Gain { get; set; }
        public string Binning { get; set; } = "";
    }

    /// <summary>How a sequence works through its exposure rows.</summary>
    public enum ShootingOrder {
        /// <summary>Each row is finished before the next starts (Simple Sequencer "Standard", most Advanced sequences).</summary>
        RowByRow,
        /// <summary>One frame of each row in turn (Simple Sequencer "Rotate through").</summary>
        Rotate
    }

    /// <summary>A target found in a sequence file. Built fresh on every scan; never written back.</summary>
    public sealed class SequenceTarget {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string ContainerName { get; set; } = "";
        public double RaHours { get; set; }
        public double DecDegrees { get; set; }
        public double PositionAngle { get; set; }
        public string SourceFile { get; set; } = "";
        public DateTime FileWriteUtc { get; set; }
        public DateTime FileCreatedUtc { get; set; }

        /// <summary>Set when the file holds several targets (a target set or a multi-target sequence); they form a linked group.</summary>
        public string? GroupId { get; set; }
        public string? GroupName { get; set; }
        /// <summary>Position in the file, 0-based. Group members are planned in this order.</summary>
        public int GroupIndex { get; set; }

        /// <summary>True when part of the target loops until a condition (time, altitude...) rather than a count.</summary>
        public bool IsOpenEnded { get; set; }

        /// <summary>Order the rows are shot in. Lines are kept in file order.</summary>
        public ShootingOrder Order { get; set; }

        public List<ExposureLine> Lines { get; set; } = new();
        public List<CapturedLine> Captured { get; set; } = new();
        public List<ConditionSpec> ImportedConditions { get; set; } = new();

        public int PlannedFrames => Lines.Sum(l => l.Planned);
        public int DoneFrames => Lines.Sum(l => Math.Min(l.Done, l.Planned));
        public double PlannedSeconds => Lines.Sum(l => l.Planned * l.ExposureSeconds);
        public double DoneSeconds => Lines.Sum(l => Math.Min(l.Done, l.Planned) * l.ExposureSeconds);
        public double RemainingSeconds => Lines.Sum(l => l.RemainingSeconds);
        public double CapturedSeconds => Captured.Sum(c => c.Count * c.ExposureSeconds);
        public bool IsComplete => Lines.Count > 0 && Lines.All(l => l.Planned == 0 || l.IsComplete) && PlannedFrames > 0;
        public double Progress => PlannedSeconds > 0 ? DoneSeconds / PlannedSeconds : 0;
    }

    public sealed class ScanReport {
        public DateTime FinishedUtc { get; set; }
        public int FilesRead { get; set; }
        public int TargetsFound { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<string> FoldersScanned { get; set; } = new();
    }
}
