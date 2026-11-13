using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using TargetBoard.Core;
using TargetBoard.Core.Engine;
using TargetBoard.Core.Models;
using TargetBoard.Util;

namespace TargetBoard.Board {

    /// <summary>One row in the target list.</summary>
    public sealed class TargetItemVM : ObservableBase {
        private string name = "", progressText = "", planText = "", badge = "", toolTip = "", color = "", fileName = "", fileLine = "", copiesText = "";
        private double progress;
        private bool isHidden, isComplete, needsPlacing, isMissing, inGroup, isOverPlanned, isMarkedDone, isSelected;
        private Brush colorBrush = Brushes.Gray;

        public TargetItemVM(string id) {
            Id = id;
        }

        public string Id { get; }
        /// <summary>Set by the board view model only; the row draws it.</summary>
        public bool IsSelected { get => isSelected; set => Set(ref isSelected, value); }
        public string Name { get => name; private set => Set(ref name, value); }
        public string Color { get => color; private set => Set(ref color, value); }
        public Brush ColorBrush { get => colorBrush; private set => Set(ref colorBrush, value); }
        public double Progress { get => progress; private set => Set(ref progress, value); }
        public string ProgressText { get => progressText; private set => Set(ref progressText, value); }
        public string PlanText { get => planText; private set => Set(ref planText, value); }
        public string Badge { get => badge; private set => Set(ref badge, value); }
        public string FileName { get => fileName; private set => Set(ref fileName, value); }
        /// <summary>"M 20.xml · created 3 Jun 2026", or the panel number inside a target set.</summary>
        public string FileLine { get => fileLine; private set => Set(ref fileLine, value); }
        /// <summary>Linked copies of this panel, or what this row is a copy of.</summary>
        public string CopiesText { get => copiesText; private set => Set(ref copiesText, value); }
        /// <summary>Shown indented under its target set's header.</summary>
        public bool InGroup { get => inGroup; private set => Set(ref inGroup, value); }
        public bool IsOverPlanned { get => isOverPlanned; private set => Set(ref isOverPlanned, value); }
        public double PercentValue => Progress;
        public string PercentText => $"{Progress:0}%";
        public string ToolTip { get => toolTip; private set => Set(ref toolTip, value); }
        public bool IsHidden { get => isHidden; private set => Set(ref isHidden, value); }
        public bool IsComplete { get => isComplete; private set => Set(ref isComplete, value); }
        /// <summary>You marked it done on the board (its sequence may still have frames to take).</summary>
        public bool IsMarkedDone { get => isMarkedDone; private set => Set(ref isMarkedDone, value); }
        public bool IsMissing { get => isMissing; private set => Set(ref isMissing, value); }
        public bool NeedsPlacing { get => needsPlacing; private set => Set(ref needsPlacing, value); }

        public void Update(TargetSummary s, bool inGroup = false) {
            Name = s.Name;
            InGroup = inGroup;
            FileName = string.IsNullOrEmpty(s.SourceFile) ? "" : System.IO.Path.GetFileName(s.SourceFile);
            FileLine = inGroup ? $"Panel {s.GroupIndex + 1}"
                : s.FileCreatedUtc == default ? FileName
                : $"{FileName} · created {s.FileCreatedUtc.ToLocalTime():d MMM yyyy}";
            CopiesText = s.Copies.Count > 0
                ? "Also saved as " + string.Join(", ", s.Copies.Select(c => $"{c.FileName} ({c.Progress * 100:0}%)"))
                : s.CopyOfName != null ? $"Copy of {s.CopyOfName}" : "";
            IsOverPlanned = s.OverPlanned > TimeSpan.Zero;
            if (Color != s.Color) {
                Color = s.Color;
                ColorBrush = Colors2.Brush(s.Color);
            }
            Progress = Math.Round(s.Progress * 100, 1);
            ProgressText = s.PlannedSeconds > 0
                ? $"{Fmt.DurationSeconds(s.DoneSeconds)} of {Fmt.DurationSeconds(s.PlannedSeconds)} · {Fmt.DurationSeconds(s.RemainingSeconds)} left"
                : "No light frames in the sequence";
            Raise(nameof(PercentText));
            Raise(nameof(PercentValue));
            NeedsPlacing = s.Unscheduled > TimeSpan.Zero;
            PlanText = s.MarkedDone ? (s.ScheduledAhead > TimeSpan.Zero ? $"Marked done · {Fmt.Duration(s.ScheduledAhead)} still on the calendar" : "")
                : s.OverPlanned > TimeSpan.Zero ? $"Over-planned by {Fmt.Duration(s.OverPlanned)}: shorten an event"
                : s.Unscheduled > TimeSpan.Zero ? $"{Fmt.Duration(s.Unscheduled)} to place"
                : s.ScheduledAhead > TimeSpan.Zero ? $"All placed · {Fmt.Duration(s.ScheduledAhead)} planned"
                : s.IsComplete ? "" : "Nothing left to place";
            Badge = s.Missing ? "File not found"
                : s.MarkedDone && s.CompletedUtc is DateTime marked ? $"Marked done {marked.ToLocalTime():d MMM}"
                : s.IsComplete && s.CompletedUtc is DateTime done ? $"Done {done.ToLocalTime():d MMM}"
                : s.IsComplete ? "Done"
                : s.IsOpenEnded ? "Open-ended loop"
                : s.LiveFrames > 0 ? $"+{s.LiveFrames} live"
                : "";
            IsHidden = s.Hidden;
            IsComplete = s.IsComplete;
            IsMarkedDone = s.MarkedDone;
            IsMissing = s.Missing;
            ToolTip = $"{s.Name}\n{Fmt.RaText(s.RaHours)}  {Fmt.DecText(s.DecDegrees)}\n{s.SourceFile}"
                + (s.IsOpenEnded ? "\nPart of this target loops until a time or altitude, so its total is one pass." : "")
                + "\nDrag onto the calendar to plan it · right-click to mark it done or hide it";
        }
    }

    public sealed class LineVM {
        public string Filter { get; init; } = "";
        public string Exposure { get; init; } = "";
        public string Count { get; init; } = "";
        public double Percent { get; init; }
        public string Time { get; init; } = "";
    }

    public sealed record KindOption(ConditionKind Kind, string Label);

    /// <summary>One editable condition of the selected target.</summary>
    public sealed class ConditionVM : ObservableBase {
        public static IReadOnlyList<KindOption> KindOptions { get; } = new[] {
            new KindOption(ConditionKind.MinAltitude, "Altitude at least"),
            new KindOption(ConditionKind.MaxAltitude, "Altitude at most"),
            new KindOption(ConditionKind.AboveHorizon, "Above custom horizon by"),
            new KindOption(ConditionKind.HourAngle, "Hour angle between"),
            new KindOption(ConditionKind.TimeWindow, "Local time between")
        };

        private readonly Action changed;
        private ConditionKind kind;
        private double value, value2;
        private bool enabled;
        private string valueText = "", value2Text = "";
        private bool hasError;
        private readonly ConditionSource source;

        public ConditionVM(ConditionSpec spec, Action changed) {
            this.changed = changed;
            kind = spec.Kind;
            value = spec.Value;
            value2 = spec.Value2;
            enabled = spec.Enabled;
            source = spec.Source;
            valueText = Format(kind, value);
            value2Text = Format(kind, value2);
        }

        public IReadOnlyList<KindOption> Kinds => KindOptions;

        public ConditionKind Kind {
            get => kind;
            set {
                if (!Set(ref kind, value)) return;
                (this.value, value2) = value switch {
                    ConditionKind.MinAltitude => (30d, 0d),
                    ConditionKind.MaxAltitude => (80d, 0d),
                    ConditionKind.AboveHorizon => (0d, 0d),
                    ConditionKind.HourAngle => (-3d, 3d),
                    _ => (22 * 60d, 2 * 60d)
                };
                ValueText = Format(kind, this.value);
                Value2Text = Format(kind, value2);
                Raise(nameof(HasSecondValue));
                Raise(nameof(Unit));
                Raise(nameof(Unit2));
                changed();
            }
        }

        public bool Enabled {
            get => enabled;
            set { if (Set(ref enabled, value)) changed(); }
        }

        public string ValueText {
            get => valueText;
            set {
                if (!Set(ref valueText, value)) return;
                if (TryParse(kind, value, out var v)) { this.value = v; HasError = false; changed(); } else HasError = true;
            }
        }

        public string Value2Text {
            get => value2Text;
            set {
                if (!Set(ref value2Text, value)) return;
                if (TryParse(kind, value, out var v)) { value2 = v; HasError = false; changed(); } else HasError = true;
            }
        }

        public bool HasError { get => hasError; private set => Set(ref hasError, value); }
        public bool HasSecondValue => kind == ConditionKind.HourAngle || kind == ConditionKind.TimeWindow;

        public string Unit => kind switch {
            ConditionKind.HourAngle => "h and",
            ConditionKind.TimeWindow => "and",
            _ => "°"
        };

        public string Unit2 => kind == ConditionKind.HourAngle ? "h" : "";

        public string SourceText => source switch {
            ConditionSource.Sequence => "from sequence",
            ConditionSource.Default => "default",
            _ => ""
        };

        public ConditionSpec ToSpec() => new() { Kind = kind, Value = value, Value2 = value2, Enabled = enabled, Source = source };

        private static string Format(ConditionKind k, double v) => k == ConditionKind.TimeWindow
            ? Fmt.ClockFromMinutes(v)
            : v.ToString("0.##", CultureInfo.CurrentCulture);

        private static bool TryParse(ConditionKind k, string text, out double v) {
            v = 0;
            if (k == ConditionKind.TimeWindow) return Fmt.TryParseClock(text, out v);
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out v)
                && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return false;
            return k switch {
                ConditionKind.HourAngle => v >= -12 && v <= 12,
                ConditionKind.AboveHorizon => v >= -10 && v <= 60,
                _ => v >= -10 && v <= 90
            };
        }
    }

    public sealed record DropSizeOption(string Label, TimeSpan? Size);

    public sealed record SortOption(TargetSort Value, string Label);

    /// <summary>Header row of a target set (or a sequence with several targets) in the list.</summary>
    public sealed class GroupItemVM : ObservableBase {
        private readonly Action<string> toggle;
        private string name = "", progressText = "", planText = "", fileLine = "", panelsText = "";
        private double progress;
        private bool isExpanded = true, isSelected;

        public GroupItemVM(string groupId, Action<string> toggle) {
            GroupId = groupId;
            this.toggle = toggle;
            ToggleCommand = new BoardCommand(_ => this.toggle(GroupId));
        }

        public string GroupId { get; }
        /// <summary>Set by the board view model only; the row draws it.</summary>
        public bool IsSelected { get => isSelected; set => Set(ref isSelected, value); }
        public System.Windows.Input.ICommand ToggleCommand { get; }
        public string Name { get => name; private set => Set(ref name, value); }
        public string PanelsText { get => panelsText; private set => Set(ref panelsText, value); }
        public double Progress { get => progress; private set => Set(ref progress, value); }
        public string ProgressText { get => progressText; private set => Set(ref progressText, value); }
        public string PlanText { get => planText; private set => Set(ref planText, value); }
        public string FileLine { get => fileLine; private set => Set(ref fileLine, value); }
        public bool IsExpanded { get => isExpanded; private set { if (Set(ref isExpanded, value)) Raise(nameof(Chevron)); } }
        public string Chevron => isExpanded ? "▾" : "▸";
        public string PercentText => $"{Progress:0}%";

        public void Update(TargetUnit u, bool expanded) {
            Name = u.Name;
            IsExpanded = expanded;
            PanelsText = u.Members.Count == 1 ? "1 target" : $"{u.Members.Count} targets";
            Progress = Math.Round(u.Progress * 100, 1);
            Raise(nameof(PercentText));
            ProgressText = $"{Fmt.DurationSeconds(u.DoneSeconds)} of {Fmt.DurationSeconds(u.PlannedSeconds)} · {Fmt.DurationSeconds(u.RemainingSeconds)} left";
            PlanText = u.Unscheduled > TimeSpan.Zero ? $"{Fmt.Duration(u.Unscheduled)} to place"
                : u.ScheduledAhead > TimeSpan.Zero ? $"All placed · {Fmt.Duration(u.ScheduledAhead)} planned"
                : u.IsComplete ? "Done" : "Nothing left to place";
            var file = System.IO.Path.GetFileName(u.SourceFile);
            FileLine = u.FileCreatedUtc == default ? file : $"{file} · created {u.FileCreatedUtc.ToLocalTime():d MMM yyyy}";
        }
    }
}
