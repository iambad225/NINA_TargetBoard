using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using TargetBoard.Core;
using TargetBoard.Core.Engine;
using TargetBoard.Core.Models;
using TargetBoard.Core.Scheduling;
using TargetBoard.Core.Sky;
using TargetBoard.Settings;
using TargetBoard.Util;

namespace TargetBoard.Board {

    /// <summary>What the calendar control needs from its view model while the user drags things around.</summary>
    public interface IBoardController {
        BoardSnapshot Snapshot { get; }
        string? SelectedTargetId { get; }
        /// <param name="force">Shift held: ignore the target's conditions (dark time and the horizon still count).</param>
        PlacementPreview PreviewNew(string targetId, DateOnly night, DateTime? desiredStartUtc, bool force);
        PlacementPreview PreviewMove(string eventId, DateOnly night, DateTime? desiredStartUtc, bool force);
        PlacementPreview PreviewResize(string eventId, DateTime desiredEndUtc, bool force);
        void Commit(PlacementPreview preview);
        void RemoveEvents(IReadOnlyCollection<string> eventIds);
        void SplitEvent(string eventId);
        /// <summary>Moves events whole nights at the same clock time; clashes are sorted out by the cascade.</summary>
        void ShiftEvents(IReadOnlyCollection<string> eventIds, int nights);
        /// <summary>That night's missed events and everything after move one night later.</summary>
        void ShiftPlanFrom(DateOnly night);
        void AddMissedTime(IReadOnlyCollection<string> eventIds);
        void SettleNight(DateOnly night);
        void Undo();
        /// <summary>Moves the calendar by whole nights (the mouse wheel).</summary>
        void ScrollDays(int days);
        FramePlanView? FramePlan(string eventId);
        /// <summary>Several selected events dragged together, by the anchor's change of night and clock time.</summary>
        MultiMovePreview PreviewMoveMany(IReadOnlyCollection<string> eventIds, string anchorId, DateOnly night, DateTime? desiredAnchorStartUtc, bool force);
        void CommitMany(MultiMovePreview preview);
        void SelectTarget(string? targetId);
        GroupPlacementPreview PreviewGroup(string groupId, DateOnly night, DateTime? desiredStartUtc);
        void CommitGroup(GroupPlacementPreview preview);
        void OpenNight(DateOnly night);
        void ShowMessage(string text);
    }

    public sealed class BoardViewModel : ObservableBase, IBoardController {
        private readonly BoardContext context;
        private readonly Dispatcher dispatcher;
        private readonly DispatcherTimer clockTimer;
        private readonly DispatcherTimer conditionTimer;
        private int refreshQueued;

        // Read by the background refresh; plain fields so no UI object is touched off the UI thread.
        private volatile string? selectedId;
        private BoardViewMode mode;
        private DateOnly anchor;

        private BoardSnapshot snapshot = BoardSnapshot.Empty;
        private TargetItemVM? selectedTarget;
        private TargetSummary? selected;
        private string statusText = "";
        private bool showHidden, showComplete;   // both off when the board opens
        private DropSizeOption dropSize;
        private List<TargetSummary> lastTargets = new();

        public BoardViewModel(BoardContext context) {
            this.context = context;
            dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            mode = context.Engine.Settings.LastView;
            anchor = mode == BoardViewMode.Week ? WeekStart(Tonight) : Tonight;

            DropSizes = new[] {
                new DropSizeOption("Everything left to place", null),
                new DropSizeOption("30 minutes", TimeSpan.FromMinutes(30)),
                new DropSizeOption("1 hour", TimeSpan.FromHours(1)),
                new DropSizeOption("2 hours", TimeSpan.FromHours(2)),
                new DropSizeOption("3 hours", TimeSpan.FromHours(3)),
                new DropSizeOption("4 hours", TimeSpan.FromHours(4))
            };
            dropSize = DropSizes[0];

            TodayCommand = new BoardCommand(_ => AnchorNight = mode == BoardViewMode.Week ? WeekStart(Tonight) : Tonight);
            PreviousCommand = new BoardCommand(_ => Step(-1));
            NextCommand = new BoardCommand(_ => Step(+1));
            RescanCommand = new BoardCommand(_ => _ = context.RescanAsync());
            AddConditionCommand = new BoardCommand(_ => AddCondition(), _ => selected != null);
            RemoveConditionCommand = new BoardCommand(p => { if (p is ConditionVM c) { Conditions.Remove(c); PushConditions(); } });
            ResetConditionsCommand = new BoardCommand(_ => { if (selectedId != null) context.Engine.ResetConditions(selectedId); LoadSelected(); }, _ => selected != null);
            SetColorCommand = new BoardCommand(p => { if (p is string hex && selectedId != null) context.Engine.UpdateTarget(selectedId, r => r.Color = hex); });
            ShowFileCommand = new BoardCommand(_ => ShowFile(), _ => selected != null && System.IO.File.Exists(selected.SourceFile));
            OpenDataFolderCommand = new BoardCommand(_ => SettingsViewModel.OpenFolder(context.Store.Folder));
            ClearSelectionCommand = new BoardCommand(_ => SelectedListItem = null);
            CloseDetailsCommand = new BoardCommand(_ => DetailsOpen = false);
            UndoCommand = new BoardCommand(_ => Undo());

            Settings = new SettingsViewModel(context);

            clockTimer = new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, (_, _) => QueueRefresh(), dispatcher);
            conditionTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Safe.Run("saving conditions", FlushConditions), dispatcher);
            conditionTimer.Stop();

            context.Engine.Changed += (_, _) => QueueRefresh();
            context.Engine.MilestonesRecorded += OnMilestones;
            context.Engine.PlanSettled += OnPlanSettled;
            context.ScanStateChanged += (_, _) => dispatcher.BeginInvoke(new Action(() => Safe.Run("scan status", () => { Raise(nameof(IsScanning)); Raise(nameof(ScanSummary)); })));
            QueueRefresh();
        }

        private static DateOnly Tonight => NightMath.NightOf(DateTime.UtcNow, TimeZoneInfo.Local);

        // ------------------------------------------------------------------ toolbar

        public SettingsViewModel Settings { get; }
        public ICommand TodayCommand { get; }
        public ICommand PreviousCommand { get; }
        public ICommand NextCommand { get; }
        public ICommand RescanCommand { get; }
        public ICommand AddConditionCommand { get; }
        public ICommand RemoveConditionCommand { get; }
        public ICommand ResetConditionsCommand { get; }
        public ICommand SetColorCommand { get; }
        public ICommand ShowFileCommand { get; }
        public ICommand OpenDataFolderCommand { get; }
        public ICommand ClearSelectionCommand { get; }
        public ICommand CloseDetailsCommand { get; }

        private bool detailsOpen;

        /// <summary>
        /// The details of the selected target or set, shown under the list. Opened by double-clicking a row. They stay
        /// open until closed, even if the row leaves the list (marked done or hidden); closing them then drops the selection.
        /// </summary>
        public bool DetailsOpen {
            get => detailsOpen;
            set {
                if (!Set(ref detailsOpen, value && selectedListItem != null)) return;
                if (!detailsOpen && selectedListItem != null && !Items.Contains(selectedListItem)) Select(null);
            }
        }

        // ------------------------------------------------------------------ panel sizes (saved with the board)

        private bool layoutRestored;

        /// <summary>Width of the target list column, as last dragged.</summary>
        public double ListPanelWidth => Math.Clamp(context.Engine.Settings.ListPanelWidth, 260, 2000);

        /// <summary>How the list and the details share the left side when the details are open, as last dragged.</summary>
        public (double List, double Details) DetailsSplit {
            get {
                var s = context.Engine.Settings;
                double list = s.ListShare, details = s.DetailsShare;
                return list > 0 && details > 0 && double.IsFinite(list) && double.IsFinite(details) ? (list, details) : (1, 1);
            }
        }

        /// <summary>Saves the panel sizes the user dragged to. Changes nothing on the plan and redraws nothing.</summary>
        public void SaveLayout(double? listWidth, double? listShare, double? detailsShare) {
            try {
                context.Engine.UpdateLayout(s => {
                    if (listWidth is double w && double.IsFinite(w) && w >= 100) s.ListPanelWidth = Math.Round(w);
                    if (listShare is double l && detailsShare is double d && l > 0 && d > 0 && double.IsFinite(l) && double.IsFinite(d)) {
                        s.ListShare = Math.Round(l, 1);
                        s.DetailsShare = Math.Round(d, 1);
                    }
                });
            } catch (Exception ex) {
                NINA.Core.Utility.Logger.Error("Target Board: could not save the panel sizes", ex);
            }
        }

        /// <summary>Selects a row and opens its details.</summary>
        public void OpenDetails(object? item) {
            if (item is not (TargetItemVM or GroupItemVM)) return;
            SelectedListItem = item;
            DetailsOpen = true;
        }

        public BoardViewMode Mode {
            get => mode;
            set {
                if (mode == value) return;
                // Week view opens on the calendar week that holds the night being shown.
                if (value == BoardViewMode.Week) anchor = WeekStart(anchor);
                mode = value;
                Raise();
                Raise(nameof(AnchorNight));
                Raise(nameof(AnchorDate));
                Raise(nameof(RangeTitle));
                context.Engine.UpdateSettings(s => s.LastView = value);
                QueueRefresh();
            }
        }

        public DateOnly AnchorNight {
            get => anchor;
            set {
                if (anchor == value) return;
                anchor = value;
                Raise();
                Raise(nameof(AnchorDate));
                Raise(nameof(RangeTitle));
                QueueRefresh();
            }
        }

        /// <summary>For the date picker.</summary>
        public DateTime? AnchorDate {
            get => anchor.ToDateTime(TimeOnly.MinValue);
            set {
                if (value is not DateTime d) return;
                var night = DateOnly.FromDateTime(d);
                AnchorNight = mode == BoardViewMode.Week ? WeekStart(night) : night;
            }
        }

        public IReadOnlyList<DropSizeOption> DropSizes { get; }

        public DropSizeOption DropSize {
            get => dropSize;
            set => Set(ref dropSize, value ?? DropSizes[0]);
        }

        public bool ShowHidden {
            get => showHidden;
            set { if (Set(ref showHidden, value)) ApplyTargets(lastTargets); }
        }

        public bool ShowComplete {
            get => showComplete;
            set { if (Set(ref showComplete, value)) ApplyTargets(lastTargets); }
        }

        public string StatusText {
            get => statusText;
            private set => Set(ref statusText, value);
        }

        public bool IsScanning => context.IsScanning;
        public string ScanSummary => Settings.ScanSummary;

        /// <summary>
        /// The mouse wheel over the calendar: Night and Week views move one night at a time (Week view slides its seven
        /// nights); Month view moves a month.
        /// </summary>
        public void ScrollDays(int days) {
            if (days == 0) return;
            if (mode == BoardViewMode.Month) { Step(Math.Sign(days)); return; }
            AnchorNight = anchor.AddDays(days);
        }

        private void Step(int direction) {
            AnchorNight = mode switch {
                BoardViewMode.Night => anchor.AddDays(direction),
                BoardViewMode.Week => anchor.AddDays(7 * direction),
                _ => anchor.AddMonths(direction)
            };
        }

        private static DayOfWeek FirstDay => CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;

        private static DateOnly WeekStart(DateOnly d) => d.AddDays(-(((int)d.DayOfWeek - (int)FirstDay + 7) % 7));

        private static (DateOnly Start, int Count) RangeFor(BoardViewMode m, DateOnly a) {
            switch (m) {
                case BoardViewMode.Night:
                    return (a, 1);
                case BoardViewMode.Week:
                    // Seven nights from the anchor: a calendar week after Tonight or a date pick, sliding a night at a time with the wheel.
                    return (a, 7);
                default:
                    var first = new DateOnly(a.Year, a.Month, 1);
                    var start = WeekStart(first);
                    var end = WeekStart(first.AddMonths(1).AddDays(-1)).AddDays(7);
                    return (start, end.DayNumber - start.DayNumber);
            }
        }

        public string RangeTitle {
            get {
                var c = CultureInfo.CurrentCulture;
                var (start, count) = RangeFor(mode, anchor);
                switch (mode) {
                    case BoardViewMode.Night:
                        return "Night of " + anchor.ToString("dddd d MMMM yyyy", c);
                    case BoardViewMode.Week:
                        var last = start.AddDays(count - 1);
                        return start.Month == last.Month
                            ? $"{start.Day} – {last.ToString("d MMMM yyyy", c)}"
                            : $"{start.ToString("d MMM", c)} – {last.ToString("d MMM yyyy", c)}";
                    default:
                        return anchor.ToString("MMMM yyyy", c);
                }
            }
        }

        // ------------------------------------------------------------------ refresh

        public BoardSnapshot Snapshot {
            get => snapshot;
            private set => Set(ref snapshot, value);
        }

        /// <summary>Rebuilds the calendar and list off the UI thread. Safe to call from anywhere, any number of times.</summary>
        public void QueueRefresh() {
            if (Interlocked.Exchange(ref refreshQueued, 1) == 1) return;
            Task.Run(async () => {
                await Task.Delay(100);
                Interlocked.Exchange(ref refreshQueued, 0);
                try {
                    var m = mode;
                    var a = anchor;
                    var id = selectedId;
                    var (start, count) = RangeFor(m, a);
                    bool loaded = context.Engine.IsLoaded;
                    var snap = context.Engine.BuildSnapshot(m, start, count, id);
                    var targets = context.Engine.GetTargets(includeHidden: true);
                    var summary = id != null ? context.Engine.GetTarget(id) : null;
                    var milestones = id != null ? context.Engine.Milestones().Where(x => x.TargetId == id).OrderByDescending(x => x.TimeUtc).Take(30).ToList() : new List<Milestone>();
                    _ = dispatcher.BeginInvoke(new Action(() => Safe.Run("refresh", () => {
                        Snapshot = snap;
                        Raise(nameof(CanUndo));
                        Raise(nameof(UndoToolTip));
                        Raise(nameof(ShowMoon));
                        Raise(nameof(ListScrollSpeed));
                        Raise(nameof(SmoothScrolling));
                        if (!layoutRestored && loaded) {
                            // The saved panel sizes are known once the board has loaded (it loads in the background).
                            layoutRestored = true;
                            Raise(nameof(ListPanelWidth));
                        }
                        lastTargets = targets;
                        ApplyTargets(targets);
                        ApplySelected(summary, milestones);
                    })));
                } catch (Exception ex) {
                    Logger.Error("Target Board: refresh failed", ex);
                }
            });
        }

        // ------------------------------------------------------------------ target list

        /// <summary>Rows of the list: <see cref="GroupItemVM"/> headers for target sets, and <see cref="TargetItemVM"/> rows.</summary>
        public ObservableCollection<object> Items { get; } = new();

        private readonly HashSet<string> collapsedGroups = new();
        private object? selectedListItem;
        private string? selectedGroupId;

        public IReadOnlyList<SortOption> SortOptions { get; } = new[] {
            new SortOption(TargetSort.CreatedNewest, "Date created, newest first"),
            new SortOption(TargetSort.CreatedOldest, "Date created, oldest first"),
            new SortOption(TargetSort.SavedNewest, "Last saved, newest first"),
            new SortOption(TargetSort.Name, "Name"),
            new SortOption(TargetSort.Progress, "Progress, furthest first"),
            new SortOption(TargetSort.ToPlace, "Most time left to place")
        };

        public SortOption SelectedSort {
            get {
                var current = context.Engine.Settings.Sort;
                return SortOptions.FirstOrDefault(o => o.Value == current) ?? SortOptions[0];
            }
            set {
                if (value == null || value.Value == context.Engine.Settings.Sort) return;
                context.Engine.UpdateSettings(st => st.Sort = value.Value);
                Raise();
                ApplyTargets(lastTargets);
            }
        }

        public bool ShowCopies {
            get => context.Engine.Settings.ShowLinkedCopies;
            set {
                if (value == context.Engine.Settings.ShowLinkedCopies) return;
                context.Engine.UpdateSettings(st => st.ShowLinkedCopies = value);
                Raise();
                ApplyTargets(lastTargets);
            }
        }

        /// <summary>Draws the Moon's altitude through each night (Night and Week views).</summary>
        public bool ShowMoon {
            get => context.Engine.Settings.ShowMoon;
            set {
                if (value == context.Engine.Settings.ShowMoon) return;
                context.Engine.UpdateSettings(st => st.ShowMoon = value);
                Raise();
                QueueRefresh();
            }
        }

        public bool CanUndo => snapshot.CanUndo;
        public string UndoToolTip => snapshot.CanUndo ? $"Undo {snapshot.UndoLabel} (Ctrl+Z)" : "Nothing to undo";
        public ICommand UndoCommand { get; }

        /// <summary>Mouse wheel speed for the left panel (1 = Windows' usual step).</summary>
        public double ListScrollSpeed => Math.Clamp(context.Engine.Settings.ListScrollSpeed, 0.05, 5);
        public bool SmoothScrolling => context.Engine.Settings.SmoothScrolling;

        // ------------------------------------------------------------------ right-click actions on the list

        /// <summary>Ids of the targets in a target set, in the set's order.</summary>
        public List<string> GroupMemberIds(string groupId)
            => lastTargets.Where(t => t.GroupId == groupId).OrderBy(t => t.GroupIndex).Select(t => t.Id).ToList();

        public bool AllMarkedDone(IReadOnlyCollection<string> ids)
            => ids.Count > 0 && ids.All(id => lastTargets.FirstOrDefault(t => t.Id == id)?.MarkedDone == true);

        public bool AllHidden(IReadOnlyCollection<string> ids)
            => ids.Count > 0 && ids.All(id => lastTargets.FirstOrDefault(t => t.Id == id)?.Hidden == true);

        /// <summary>Marks targets done or not done on the board. Their sequence files are not touched.</summary>
        public void MarkDone(IReadOnlyCollection<string> ids, bool done, string name) {
            if (context.Engine.SetMarkedDone(ids, done) == 0) return;
            if (!done) {
                StatusText = $"{name} marked not done.";
                return;
            }
            var ahead = TimeSpan.FromTicks(lastTargets.Where(t => ids.Contains(t.Id)).Sum(t => t.ScheduledAhead.Ticks));
            var text = $"{name} marked done.";
            if (ahead > TimeSpan.Zero) text += $" {Fmt.Duration(ahead)} still planned on the calendar.";
            StatusText = text;
        }

        public void SetHidden(IReadOnlyCollection<string> ids, bool hidden, string name) {
            foreach (var id in ids) context.Engine.UpdateTarget(id, r => r.Hidden = hidden);
            StatusText = hidden
                ? $"{name} hidden."
                : $"{name} shown again.";
        }

        private void ToggleGroup(string groupId) {
            if (!collapsedGroups.Remove(groupId)) collapsedGroups.Add(groupId);
            ApplyTargets(lastTargets);
        }

        private void ApplyTargets(List<TargetSummary> all) {
            var settings = context.Engine.Settings;
            var visible = all.Where(t => (showHidden || !t.Hidden) && (showComplete || !t.IsComplete) && (settings.ShowLinkedCopies || t.CopyOfId == null)).ToList();
            var units = TargetOrdering.Arrange(visible, settings.Sort);
            var oldTargets = Items.OfType<TargetItemVM>().ToDictionary(t => t.Id);
            var oldGroups = Items.OfType<GroupItemVM>().ToDictionary(g => g.GroupId);
            // A selected row that left the list while its details stayed open is the same row when it comes back.
            if (selectedListItem is TargetItemVM st) oldTargets.TryAdd(st.Id, st);
            if (selectedListItem is GroupItemVM sg) oldGroups.TryAdd(sg.GroupId, sg);

            TargetItemVM Row(TargetSummary m, bool inGroup) {
                var row = oldTargets.TryGetValue(m.Id, out var e) ? e : new TargetItemVM(m.Id);
                row.Update(m, inGroup);
                return row;
            }

            var wanted = new List<object>();
            foreach (var u in units) {
                if (u.IsGroup) {
                    var g = oldGroups.TryGetValue(u.GroupId!, out var eg) ? eg : new GroupItemVM(u.GroupId!, ToggleGroup);
                    g.Update(u, !collapsedGroups.Contains(u.GroupId!));
                    wanted.Add(g);
                    if (g.IsExpanded) foreach (var m in u.Members) wanted.Add(Row(m, inGroup: true));
                } else {
                    wanted.Add(Row(u.Members[0], inGroup: false));
                }
            }

            if (!wanted.SequenceEqual(Items)) {
                Items.Clear();
                foreach (var w in wanted) Items.Add(w);
            }
            // The selection belongs to the view model; rows only show it. A row that left the list takes it along,
            // unless its details are open: they stay until closed, as long as the target or set still exists.
            if (selectedListItem != null && !wanted.Contains(selectedListItem)) {
                bool exists = selectedListItem switch {
                    TargetItemVM t => all.Any(x => x.Id == t.Id),
                    GroupItemVM g => all.Any(x => x.GroupId == g.GroupId),
                    _ => false
                };
                if (!detailsOpen || !exists) Select(null);
                else if (selectedListItem is TargetItemVM t && all.FirstOrDefault(x => x.Id == t.Id) is TargetSummary s) t.Update(s, t.InGroup);
            }
            shownTargets = visible.Count;
            totalTargets = all.Count(t => t.CopyOfId == null || settings.ShowLinkedCopies);
            Raise(nameof(TargetCountText));
            if (selectedGroupId != null) LoadGroup();
        }

        private int shownTargets, totalTargets;

        public string TargetCountText => shownTargets == totalTargets ? $"{totalTargets} targets" : $"{shownTargets} of {totalTargets} targets";

        /// <summary>
        /// What the list has selected: a target row, a target-set header, or nothing. This is the only place the selection
        /// changes; the rows draw it from their IsSelected flag.
        /// </summary>
        public object? SelectedListItem {
            get => selectedListItem;
            set => Select(value);
        }

        public TargetItemVM? SelectedTarget => selectedTarget;

        private void Select(object? value) {
            if (value is not (TargetItemVM or GroupItemVM)) value = null;
            if (ReferenceEquals(selectedListItem, value)) return;
            var old = selectedListItem;
            selectedListItem = value;
            SetRowSelected(old, false);
            SetRowSelected(value, true);
            Raise(nameof(SelectedListItem));
            if (value == null) DetailsOpen = false;
            selectedGroupId = (value as GroupItemVM)?.GroupId;
            SetSelectedTarget(value as TargetItemVM);
            LoadGroup();
        }

        private static void SetRowSelected(object? row, bool selected) {
            if (row is TargetItemVM t) t.IsSelected = selected;
            else if (row is GroupItemVM g) g.IsSelected = selected;
        }

        private void SetSelectedTarget(TargetItemVM? value) {
            if (ReferenceEquals(selectedTarget, value)) return;
            FlushConditions();
            selectedTarget = value;
            selectedId = value?.Id;
            Raise(nameof(SelectedTarget));
            LoadSelected();
            QueueRefresh();
        }

        public string? SelectedTargetId => selectedId;

        // ------------------------------------------------------------------ selected target set

        public bool HasGroupSelection => selectedGroupId != null && groupUnit != null;
        public bool HasAnySelection => HasSelection || HasGroupSelection;
        private TargetUnit? groupUnit;
        public string GroupName => groupUnit?.Name ?? "";
        public string GroupFile => groupUnit?.SourceFile ?? "";

        public string GroupProgress => groupUnit == null ? "" :
            $"{groupUnit.Members.Count} panels · {Fmt.DurationSeconds(groupUnit.DoneSeconds)} of {Fmt.DurationSeconds(groupUnit.PlannedSeconds)} done ({groupUnit.Progress * 100:0}%)";

        public string GroupPlanning => groupUnit == null ? "" :
            $"{Fmt.Duration(groupUnit.ScheduledAhead)} planned · {Fmt.Duration(groupUnit.Unscheduled)} to place";

        public ObservableCollection<LineVM> GroupLines { get; } = new();

        private void LoadGroup() {
            var id = selectedGroupId;
            groupUnit = id == null ? null : TargetOrdering.Arrange(lastTargets.Where(t => t.GroupId == id), TargetSort.Name).FirstOrDefault();
            GroupLines.Clear();
            if (groupUnit != null) {
                foreach (var m in groupUnit.Members) {
                    GroupLines.Add(new LineVM {
                        Filter = m.Name,
                        Exposure = "",
                        Count = $"{m.Progress * 100:0}%",
                        Percent = m.Progress * 100,
                        Time = m.Unscheduled > TimeSpan.Zero ? $"{Fmt.Duration(m.Unscheduled)} to place" : (m.IsComplete ? "done" : "all placed")
                    });
                }
            }
            Raise(nameof(HasGroupSelection));
            Raise(nameof(HasAnySelection));
            Raise(nameof(GroupName));
            Raise(nameof(GroupFile));
            Raise(nameof(GroupProgress));
            Raise(nameof(GroupPlanning));
        }

        // ------------------------------------------------------------------ selected target details

        public bool HasSelection => selected != null;
        public string SelectedName => selected?.Name ?? "";
        public string SelectedCoordinates => selected == null ? "" : $"RA {Fmt.RaText(selected.RaHours)}   Dec {Fmt.DecText(selected.DecDegrees)}";
        public string SelectedFile => selected?.SourceFile ?? "";

        public string SelectedProgress {
            get {
                if (selected == null) return "";
                if (selected.Missing) return "This target is no longer in any scanned file. Its events stay on the board.";
                var text = $"{Fmt.DurationSeconds(selected.DoneSeconds)} of {Fmt.DurationSeconds(selected.PlannedSeconds)} done ({selected.Progress * 100:0}%)";
                if (selected.CompletedUtc is DateTime done) text += $" · completed {done.ToLocalTime():d MMM yyyy HH:mm}";
                return text;
            }
        }

        public string SelectedPlanning {
            get {
                if (selected == null) return "";
                var overhead = context.Engine.Settings.OverheadPercent;
                return $"Needs {Fmt.Duration(TimeSpan.FromSeconds(selected.RemainingSeconds * (1 + overhead / 100)))} with {overhead:0}% overhead · "
                     + $"{Fmt.Duration(selected.ScheduledAhead)} planned · {Fmt.Duration(selected.Unscheduled)} to place";
            }
        }

        public string SelectedCaptured => selected == null || selected.Captured.Count == 0 ? ""
            : "NINA's count: " + string.Join(" · ", selected.Captured.GroupBy(c => c.Filter)
                .Select(g => $"{g.Key} {g.Sum(c => c.Count)}× ({Fmt.DurationSeconds(g.Sum(c => c.Count * c.ExposureSeconds))})"));

        public string SelectedNotes {
            get => selected?.Notes ?? "";
            set { if (selectedId != null && value != selected?.Notes) context.Engine.UpdateTarget(selectedId, r => r.Notes = value ?? ""); }
        }

        public bool SelectedHidden {
            get => selected?.Hidden ?? false;
            set { if (selectedId != null) context.Engine.UpdateTarget(selectedId, r => r.Hidden = value); }
        }

        public bool SelectedMarkedDone {
            get => selected?.MarkedDone ?? false;
            set { if (selectedId != null && selected != null && value != selected.MarkedDone) MarkDone(new[] { selectedId }, value, selected.Name); }
        }

        public IReadOnlyList<string> ColorChoices => Palette.Colors;

        public ObservableCollection<LineVM> Lines { get; } = new();
        public ObservableCollection<ConditionVM> Conditions { get; } = new();
        public ObservableCollection<string> History { get; } = new();

        private void LoadSelected() {
            var id = selectedId;
            var summary = id != null ? context.Engine.GetTarget(id) : null;
            var milestones = id != null ? context.Engine.Milestones().Where(x => x.TargetId == id).OrderByDescending(x => x.TimeUtc).Take(30).ToList() : new List<Milestone>();
            ApplySelected(summary, milestones, reloadConditions: true);
        }

        private void ApplySelected(TargetSummary? summary, List<Milestone> milestones, bool reloadConditions = false) {
            // A refresh started before the selection changed: ignore its stale result.
            if (summary != null && summary.Id != selectedId) return;
            bool switched = summary?.Id != selected?.Id;
            selected = summary;

            Lines.Clear();
            if (summary != null) {
                foreach (var l in summary.Lines) {
                    Lines.Add(new LineVM {
                        Filter = l.Filter,
                        Exposure = Fmt.Seconds(l.ExposureSeconds),
                        Count = $"{Math.Min(l.Done, l.Planned)}/{l.Planned}",
                        Percent = l.Planned > 0 ? Math.Min(100, 100.0 * l.Done / l.Planned) : 0,
                        Time = Fmt.DurationSeconds(l.RemainingSeconds) + " left"
                    });
                }
            }

            // Do not overwrite conditions the user is typing into; reload only on a new selection.
            if (reloadConditions || switched || (!conditionTimer.IsEnabled && !Conditions.Any(c => c.HasError) && !SameConditions(summary))) {
                Conditions.Clear();
                if (summary != null) foreach (var c in summary.Conditions) Conditions.Add(new ConditionVM(c, ConditionsEdited));
            }

            History.Clear();
            foreach (var m in milestones) {
                History.Add($"{m.TimeUtc.ToLocalTime():d MMM yyyy HH:mm}  {m.Describe()}");
            }

            Raise(nameof(HasSelection));
            Raise(nameof(HasAnySelection));
            Raise(nameof(SelectedName));
            Raise(nameof(SelectedCoordinates));
            Raise(nameof(SelectedFile));
            Raise(nameof(SelectedProgress));
            Raise(nameof(SelectedPlanning));
            Raise(nameof(SelectedCaptured));
            Raise(nameof(SelectedNotes));
            Raise(nameof(SelectedHidden));
            Raise(nameof(SelectedMarkedDone));
        }

        private bool SameConditions(TargetSummary? s) {
            if (s == null) return Conditions.Count == 0;
            var mine = Conditions.Select(c => c.ToSpec().CacheKey);
            return mine.SequenceEqual(s.Conditions.Select(c => c.CacheKey));
        }

        private void AddCondition() {
            Conditions.Add(new ConditionVM(ConditionSpec.MinAltitude(30), ConditionsEdited));
            PushConditions();
        }

        private void ConditionsEdited() {
            conditionTimer.Stop();
            conditionTimer.Start();
        }

        private void FlushConditions() {
            if (!conditionTimer.IsEnabled) return;
            conditionTimer.Stop();
            PushConditions();
        }

        private void PushConditions() {
            conditionTimer.Stop();
            if (selectedId == null) return;
            // While a value is being typed and is not valid yet, keep the last good conditions.
            if (Conditions.Any(c => c.HasError)) return;
            context.Engine.SetConditions(selectedId, Conditions.Select(c => c.ToSpec()));
        }

        private void ShowFile() {
            if (selected == null) return;
            try {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{selected.SourceFile}\"") { UseShellExecute = true });
            } catch (Exception) {
                // Explorer not available; nothing to do.
            }
        }

        private void OnPlanSettled(object? sender, PlanChangeReport report) {
            dispatcher.BeginInvoke(new Action(() => Safe.Run("showing the morning settle", () => {
                Report(report);
                Notification.ShowInformation($"Target Board: {report.Summary}");
            })));
        }

        private void OnMilestones(object? sender, IReadOnlyList<Milestone> list) {
            foreach (var m in list) {
                if (m.Source != MilestoneSource.LiveCapture) continue;
                if (m.Kind == MilestoneKind.TargetComplete) Notification.ShowSuccess($"Target Board: {m.Describe()}");
                else if (m.Kind == MilestoneKind.PartComplete) Notification.ShowInformation($"Target Board: {m.Describe()}");
            }
        }

        // ------------------------------------------------------------------ calendar callbacks

        public PlacementPreview PreviewNew(string targetId, DateOnly night, DateTime? desiredStartUtc, bool force)
            => context.Engine.PreviewNew(targetId, night, desiredStartUtc, dropSize.Size, force);

        public PlacementPreview PreviewMove(string eventId, DateOnly night, DateTime? desiredStartUtc, bool force)
            => context.Engine.PreviewMove(eventId, night, desiredStartUtc, force);

        public PlacementPreview PreviewResize(string eventId, DateTime desiredEndUtc, bool force)
            => context.Engine.PreviewResize(eventId, desiredEndUtc, force);

        public void Commit(PlacementPreview preview) {
            if (!preview.Result.CanCommit) {
                StatusText = preview.Result.Message;
                return;
            }
            var ev = context.Engine.Commit(preview);
            if (ev == null) return;
            var name = context.Engine.GetTarget(ev.TargetId)?.Name ?? "Event";
            var zone = TimeZoneInfo.Local;
            var when = $"{NightMath.ToLocal(ev.StartUtc, zone):ddd d MMM HH:mm}–{NightMath.ToLocal(ev.EndUtc, zone):HH:mm}";
            StatusText = preview.Result.Outcome == PlacementOutcome.Clipped || preview.Forced || preview.ReplanOfEventId != null
                ? $"{name}: {when}. {preview.Result.Message}"
                : $"{name}: {when} ({Fmt.Duration(ev.Duration)}).";
        }

        public void RemoveEvents(IReadOnlyCollection<string> eventIds) {
            int n = context.Engine.RemoveEvents(eventIds);
            StatusText = n switch {
                0 => "Nothing removed.",
                1 => "Event removed.",
                _ => $"{n} events removed."
            };
        }

        public void ShiftEvents(IReadOnlyCollection<string> eventIds, int nights) => Report(context.Engine.ShiftEvents(eventIds, nights));

        public void ShiftPlanFrom(DateOnly night) => Report(context.Engine.ShiftPlanFrom(night));

        public void AddMissedTime(IReadOnlyCollection<string> eventIds) => Report(context.Engine.AddMissedTime(eventIds));

        public void SettleNight(DateOnly night) => Report(context.Engine.SettleNight(night));

        public void Undo() {
            var label = context.Engine.Undo();
            StatusText = label == null ? "Nothing to undo." : $"Undone: {label}.";
        }

        public FramePlanView? FramePlan(string eventId) => context.Engine.FramePlan(eventId);

        public MultiMovePreview PreviewMoveMany(IReadOnlyCollection<string> eventIds, string anchorId, DateOnly night, DateTime? desiredAnchorStartUtc, bool force)
            => context.Engine.PreviewMoveMany(eventIds, anchorId, night, desiredAnchorStartUtc, force);

        public void CommitMany(MultiMovePreview preview) {
            if (!preview.CanCommit) { StatusText = preview.Message; return; }
            int n = context.Engine.CommitMany(preview);
            StatusText = n == 0 ? "Nothing moved." : preview.Message;
        }

        /// <summary>Puts a settle, shift or carry report on the status line (the full list is in the tooltip and the NINA log).</summary>
        private void Report(PlanChangeReport report) {
            var text = report.ToString();
            if (report.Changed > 0) Logger.Info("Target Board: " + report);
            StatusText = text;
        }

        public void SplitEvent(string eventId) {
            StatusText = context.Engine.SplitEvent(eventId)
                ? "Event split in two."
                : "Too short to split.";
        }

        public void SelectTarget(string? targetId) {
            if (targetId == null) { SelectedListItem = null; return; }
            TargetItemVM? Find() => Items.OfType<TargetItemVM>().FirstOrDefault(t => t.Id == targetId);
            var item = Find();
            var summary = lastTargets.FirstOrDefault(t => t.Id == targetId);
            if (item == null && summary != null) {
                if (summary.GroupId != null && collapsedGroups.Remove(summary.GroupId)) ApplyTargets(lastTargets);
                item = Find();
                if (item == null) {
                    ShowHidden = true;
                    ShowComplete = true;
                    if (summary.CopyOfId != null) ShowCopies = true;
                    item = Find();
                }
            }
            SelectedListItem = item;
        }

        public GroupPlacementPreview PreviewGroup(string groupId, DateOnly night, DateTime? desiredStartUtc)
            => context.Engine.PreviewGroup(groupId, night, desiredStartUtc, dropSize.Size);

        public void CommitGroup(GroupPlacementPreview preview) {
            int placed = preview.CanCommit ? context.Engine.CommitGroup(preview) : 0;
            StatusText = placed > 0 ? preview.Message : (preview.Message.Length > 0 ? preview.Message : "Nothing placed.");
        }

        public void OpenNight(DateOnly night) {
            AnchorNight = night;
            Mode = BoardViewMode.Night;
        }

        public void ShowMessage(string text) => StatusText = text;
    }
}
