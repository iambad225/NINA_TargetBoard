using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TargetBoard.Core.History;
using TargetBoard.Core.Models;
using TargetBoard.Core.Scheduling;
using TargetBoard.Core.Sequences;
using TargetBoard.Core.Sky;
using TargetBoard.Core.Storage;

namespace TargetBoard.Core.Engine {

    /// <summary>
    /// The board's state and rules, with no UI or NINA types in it.
    /// All public members are thread-safe; the plugin calls scans and snapshot builds from background tasks.
    /// </summary>
    public sealed class BoardEngine {
        private readonly object sync = new();
        private readonly Func<DateTime> utcNow;
        private readonly Dictionary<string, List<TimeRange>> windowCache = new();

        private ISkyContext sky;
        private BoardData data = new();
        private HistoryData history = new();
        private List<CaptureRecord> captures = new();
        private List<SequenceTarget> targets = new();
        private Dictionary<string, SequenceTarget> targetById = new();
        private Dictionary<string, EffectiveProgress> progress = new();
        private Dictionary<string, string> copyOf = new();
        private List<CaptureSession>? sessionCache;
        // Nothing is written until the saved files have been read, so an early change can never overwrite them.
        private bool loaded;

        public BoardEngine(BoardStore store, ISkyContext sky, Func<DateTime>? utcNow = null) {
            Store = store;
            this.sky = sky;
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public BoardStore Store { get; }
        public ScanReport? LastScan { get; private set; }
        public DateTime NowUtc => utcNow();

        /// <summary>Raised after anything that changes what the board shows. May fire on any thread.</summary>
        public event EventHandler? Changed;

        /// <summary>Raised with milestones as they are recorded.</summary>
        public event EventHandler<IReadOnlyList<Milestone>>? MilestonesRecorded;

        public ISkyContext Sky {
            get { lock (sync) return sky; }
            set {
                lock (sync) {
                    sky = value;
                    windowCache.Clear();
                    sessionCache = null;
                }
                RaiseChanged();
            }
        }

        public BoardSettings Settings {
            get { lock (sync) return data.Settings.Clone(); }
        }

        // ------------------------------------------------------------------ loading and saving

        public void Load() {
            lock (sync) {
                data = Store.LoadBoard();
                history = Store.LoadHistory();
                captures = Store.LoadCaptures();
                undoStack.Clear();
                try {
                    undoStack.AddRange(Store.LoadUndo().Steps.Select(s => (s.Label, s.Events)).TakeLast(UndoDepth));
                } catch (Exception) {
                    // A damaged undo file only costs the undo history.
                }
                sessionCache = null;
                windowCache.Clear();
                loaded = true;
            }
            RaiseChanged();
        }

        public bool IsLoaded {
            get { lock (sync) return loaded; }
        }

        private void SaveBoardLocked() {
            if (!loaded) return;
            Store.SaveBoard(data);
            SaveUndoLocked();
        }

        private void SaveHistoryLocked() {
            if (loaded) Store.SaveHistory(history);
        }

        /// <summary>
        /// Saves layout settings (panel sizes) without touching the plan: no recalculation, no refresh.
        /// </summary>
        public void UpdateLayout(Action<BoardSettings> change) {
            lock (sync) {
                change(data.Settings);
                SaveBoardLocked();
            }
        }

        public void UpdateSettings(Action<BoardSettings> change) {
            lock (sync) {
                change(data.Settings);
                data.Settings.SnapMinutes = Math.Clamp(data.Settings.SnapMinutes, 1, 60);
                data.Settings.MinimumEventMinutes = Math.Clamp(data.Settings.MinimumEventMinutes, 1, 240);
                data.Settings.OverheadPercent = Math.Clamp(data.Settings.OverheadPercent, 0, 200);
                data.Settings.SessionGapMinutes = Math.Clamp(data.Settings.SessionGapMinutes, 5, 600);
                windowCache.Clear();
                sessionCache = null;
                SaveBoardLocked();
            }
            RaiseChanged();
        }

        /// <summary>Folders to scan: the profile's sequence and target folders (when enabled) plus any the user added.</summary>
        public List<string> FoldersToScan(IEnumerable<string> profileFolders) {
            lock (sync) {
                var list = new List<string>();
                if (data.Settings.UseProfileFolders) list.AddRange(profileFolders.Where(f => !string.IsNullOrWhiteSpace(f)));
                list.AddRange(data.Settings.ExtraFolders);
                return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        // ------------------------------------------------------------------ scanning and progress

        /// <summary>Takes a fresh set of targets from a scan and updates records, progress and milestones.</summary>
        public void ApplyScan(List<SequenceTarget> found, ScanReport report) {
            List<Milestone> added;
            lock (sync) {
                var now = utcNow();
                targets = found;
                targetById = new Dictionary<string, SequenceTarget>();
                foreach (var t in found) targetById[t.Id] = t;
                copyOf = TargetLinks.FindCopies(found);

                foreach (var t in found) {
                    if (!data.Targets.TryGetValue(t.Id, out var rec)) {
                        rec = new TargetRecord { Id = t.Id, FirstSeenUtc = now, Color = Palette.ForKey(t.Name) };
                        data.Targets[t.Id] = rec;
                    }
                    rec.Name = t.Name;
                    rec.RaHours = t.RaHours;
                    rec.DecDegrees = t.DecDegrees;
                    rec.SourceFile = t.SourceFile;
                    rec.LastSeenUtc = now;
                    if (string.IsNullOrEmpty(rec.Color)) rec.Color = Palette.ForKey(t.Name);
                    if (!rec.ConditionsInitialized) {
                        rec.Conditions = DefaultConditions(t);
                        rec.ConditionsInitialized = true;
                    }
                }

                RematchCapturesLocked();
                RebuildProgressLocked();
                added = ProgressTracker.Update(history, progress.Values);
                SaveHistoryLocked();

                // Forget targets that are gone and have nothing on the board, so the file does not grow forever.
                var used = new HashSet<string>(data.Events.Select(e => e.TargetId));
                foreach (var stale in data.Targets.Keys.Where(id => !targetById.ContainsKey(id) && !used.Contains(id)
                                                                 && (now - data.Targets[id].LastSeenUtc).TotalDays > 90).ToList()) {
                    data.Targets.Remove(stale);
                }

                LastScan = report;
                windowCache.Clear();
                sessionCache = null;
                SaveBoardLocked();
            }
            if (added.Count > 0) MilestonesRecorded?.Invoke(this, added);
            RaiseChanged();
        }

        private List<ConditionSpec> DefaultConditions(SequenceTarget? t) {
            var s = data.Settings;
            var list = new List<ConditionSpec>();
            if (s.DefaultMinAltitude > 0) list.Add(ConditionSpec.MinAltitude(s.DefaultMinAltitude, ConditionSource.Default));
            if (s.DefaultAboveHorizon) list.Add(ConditionSpec.AboveHorizon(0, ConditionSource.Default));
            if (s.ImportSequenceConditions && t != null) {
                foreach (var c in t.ImportedConditions) {
                    if (!list.Any(x => x.Kind == c.Kind && Math.Abs(x.Value - c.Value) < 1e-9)) list.Add(c.Clone());
                }
            }
            return list;
        }

        private void RematchCapturesLocked() {
            bool changed = false;
            foreach (var c in captures) {
                if (c.TargetId != null && targetById.ContainsKey(c.TargetId)) continue;
                var match = TargetMatcher.Match(c, targets);
                if (match != null && match.Id != c.TargetId) {
                    c.TargetId = match.Id;
                    changed = true;
                }
            }
            if (changed) Store.RewriteCaptures(captures);
        }

        private void RebuildProgressLocked() {
            var byTarget = captures.Where(c => c.TargetId != null).GroupBy(c => c.TargetId!).ToDictionary(g => g.Key, g => g.ToList());
            progress = new Dictionary<string, EffectiveProgress>();
            foreach (var t in targets) {
                byTarget.TryGetValue(t.Id, out var list);
                progress[t.Id] = EffectiveProgress.Build(t, list ?? new List<CaptureRecord>());
            }
        }

        /// <summary>Logs one saved light frame, matches it to a target and checks for milestones.</summary>
        public CaptureRecord? RecordCapture(CaptureRecord record) {
            List<Milestone> added = new();
            lock (sync) {
                if (!data.Settings.LogCaptures || !loaded) return null;
                record.TimeUtc = SkyMath.AsUtc(record.TimeUtc);
                record.TargetId = TargetMatcher.Match(record, targets)?.Id;
                Store.AppendCapture(record);
                captures.Add(record);
                sessionCache = null;

                if (record.TargetId != null && targetById.TryGetValue(record.TargetId, out var t)) {
                    var forTarget = captures.Where(c => c.TargetId == t.Id).ToList();
                    var eff = EffectiveProgress.Build(t, forTarget);
                    progress[t.Id] = eff;
                    added = ProgressTracker.Update(history, new[] { eff });
                    if (added.Count > 0) SaveHistoryLocked();
                }
            }
            if (added.Count > 0) MilestonesRecorded?.Invoke(this, added);
            RaiseChanged();
            return record;
        }

        // ------------------------------------------------------------------ targets

        public List<TargetSummary> GetTargets(bool includeHidden = true) {
            lock (sync) {
                var now = utcNow();
                var list = new List<TargetSummary>();
                var withEvents = new HashSet<string>(data.Events.Select(e => e.TargetId));
                foreach (var rec in data.Targets.Values) {
                    bool found = targetById.TryGetValue(rec.Id, out var t);
                    if (!found && !withEvents.Contains(rec.Id)) continue;
                    if (rec.Hidden && !includeHidden) continue;
                    list.Add(SummaryLocked(rec, t, now));
                }
                return list.OrderBy(s => s.Hidden).ThenBy(s => s.IsComplete).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            }
        }

        public TargetSummary? GetTarget(string id) {
            lock (sync) {
                if (!data.Targets.TryGetValue(id, out var rec)) return null;
                targetById.TryGetValue(id, out var t);
                return SummaryLocked(rec, t, utcNow());
            }
        }

        private TargetSummary SummaryLocked(TargetRecord rec, SequenceTarget? t, DateTime now) {
            EffectiveProgress? eff = t != null && progress.TryGetValue(t.Id, out var p) ? p : null;
            var ahead = Scheduler.ScheduledAhead(data.Events, rec.Id, now);
            double remaining = eff?.RemainingSeconds ?? 0;
            history.Snapshots.TryGetValue(rec.Id, out var snap);
            string? copyOfId = copyOf.TryGetValue(rec.Id, out var cid) ? cid : null;
            var copies = copyOf.Where(kv => kv.Value == rec.Id)
                .Select(kv => {
                    targetById.TryGetValue(kv.Key, out var ct);
                    data.Targets.TryGetValue(kv.Key, out var cr);
                    double cp = progress.TryGetValue(kv.Key, out var ce) ? ce.Progress : 0;
                    return new LinkedCopy(kv.Key, System.IO.Path.GetFileName(ct?.SourceFile ?? cr?.SourceFile ?? ""), cp, cr?.Hidden ?? false);
                }).ToList();
            return new TargetSummary {
                Id = rec.Id,
                Name = rec.Name,
                Color = rec.Color,
                SourceFile = rec.SourceFile,
                RaHours = rec.RaHours,
                DecDegrees = rec.DecDegrees,
                Hidden = rec.Hidden,
                Notes = rec.Notes,
                Missing = t == null,
                IsOpenEnded = t?.IsOpenEnded ?? false,
                IsComplete = (eff?.IsComplete ?? false) || rec.MarkedDoneUtc != null,
                MarkedDone = rec.MarkedDoneUtc != null,
                CompletedUtc = snap?.Complete == true ? snap.CompletedUtc : rec.MarkedDoneUtc,
                FileWriteUtc = t?.FileWriteUtc ?? default,
                FileCreatedUtc = t?.FileCreatedUtc ?? default,
                GroupId = t?.GroupId,
                GroupName = t?.GroupName,
                GroupIndex = t?.GroupIndex ?? 0,
                CopyOfId = copyOfId,
                CopyOfName = copyOfId != null && data.Targets.TryGetValue(copyOfId, out var orig)
                    ? $"{orig.Name} in {System.IO.Path.GetFileName(orig.SourceFile)}" : null,
                Copies = copies,
                Progress = eff?.Progress ?? 0,
                PlannedSeconds = eff?.PlannedSeconds ?? 0,
                DoneSeconds = eff?.DoneSeconds ?? 0,
                RemainingSeconds = remaining,
                CapturedSeconds = t?.CapturedSeconds ?? 0,
                LiveFrames = eff?.LiveFrames ?? 0,
                ScheduledAhead = ahead,
                Unscheduled = BudgetLocked(rec.Id) > ahead ? BudgetLocked(rec.Id) - ahead : TimeSpan.Zero,
                // A target you marked done has no budget; its leftover events are not "over-planned", just spare.
                OverPlanned = rec.MarkedDoneUtc == null && ahead > BudgetLocked(rec.Id) ? ahead - BudgetLocked(rec.Id) : TimeSpan.Zero,
                Lines = eff?.Lines ?? new List<ExposureLine>(),
                Captured = t?.Captured ?? new List<CapturedLine>(),
                Conditions = rec.Conditions.Select(c => c.Clone()).ToList(),
                ImportedConditions = t?.ImportedConditions.Select(c => c.Clone()).ToList() ?? new List<ConditionSpec>()
            };
        }

        public void SetConditions(string targetId, IEnumerable<ConditionSpec> conditions) {
            lock (sync) {
                if (!data.Targets.TryGetValue(targetId, out var rec)) return;
                rec.Conditions = conditions.Select(c => c.Clone()).ToList();
                rec.ConditionsInitialized = true;
                windowCache.Clear();
                SaveBoardLocked();
            }
            RaiseChanged();
        }

        /// <summary>Puts a target's conditions back to the defaults plus whatever its sequence contains.</summary>
        public void ResetConditions(string targetId) {
            lock (sync) {
                if (!data.Targets.TryGetValue(targetId, out var rec)) return;
                targetById.TryGetValue(targetId, out var t);
                rec.Conditions = DefaultConditions(t);
                rec.ConditionsInitialized = true;
                windowCache.Clear();
                SaveBoardLocked();
            }
            RaiseChanged();
        }

        /// <summary>
        /// Marks targets done (or not done) on the board, and records the date in their history.
        /// The sequence files are not touched. Returns how many targets changed.
        /// </summary>
        public int SetMarkedDone(IEnumerable<string> targetIds, bool done) {
            int changed = 0;
            lock (sync) {
                var now = utcNow();
                foreach (var id in targetIds.Distinct()) {
                    if (!data.Targets.TryGetValue(id, out var rec)) continue;
                    if ((rec.MarkedDoneUtc != null) == done) continue;
                    rec.MarkedDoneUtc = done ? now : null;
                    var kind = done ? MilestoneKind.TargetComplete : MilestoneKind.Reopened;
                    progress.TryGetValue(id, out var eff);
                    history.Milestones.Add(new Milestone {
                        Id = StableId.From(id, "manual", kind.ToString(), now.Ticks.ToString(CultureInfo.InvariantCulture)),
                        TimeUtc = now,
                        Kind = kind,
                        Source = MilestoneSource.Manual,
                        TargetId = id,
                        TargetName = rec.Name,
                        Done = eff?.Lines.Sum(l => Math.Min(l.Done, l.Planned)) ?? 0,
                        Planned = eff?.Lines.Sum(l => l.Planned) ?? 0,
                        SourceFile = rec.SourceFile
                    });
                    changed++;
                }
                if (changed > 0) {
                    history.Milestones.Sort((a, b) => a.TimeUtc.CompareTo(b.TimeUtc));
                    SaveBoardLocked();
                    SaveHistoryLocked();
                }
            }
            if (changed > 0) RaiseChanged();
            return changed;
        }

        public void UpdateTarget(string targetId, Action<TargetRecord> change) {
            lock (sync) {
                if (!data.Targets.TryGetValue(targetId, out var rec)) return;
                change(rec);
                SaveBoardLocked();
            }
            RaiseChanged();
        }

        // ------------------------------------------------------------------ nights and windows

        public TimeRange? DarkWindow(DateOnly night) {
            lock (sync) return sky.GetDarkWindow(night, data.Settings.DarkTime);
        }

        /// <summary>When a target may be shot on a night: dark time clipped by its enabled conditions.</summary>
        public List<TimeRange> ValidWindows(string targetId, DateOnly night) {
            lock (sync) return ValidWindowsLocked(targetId, night);
        }

        private List<TimeRange> ValidWindowsLocked(string targetId, DateOnly night) {
            if (!data.Targets.TryGetValue(targetId, out var rec)) return new List<TimeRange>();
            string key = string.Join("|", targetId, night.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), data.Settings.DarkTime,
                sky.LocationKey, rec.RaHours.ToString("R", CultureInfo.InvariantCulture), rec.DecDegrees.ToString("R", CultureInfo.InvariantCulture),
                string.Join(";", rec.Conditions.Select(c => c.CacheKey)));
            if (windowCache.TryGetValue(key, out var cached)) return cached;

            var dark = sky.GetDarkWindow(night, data.Settings.DarkTime);
            var windows = dark == null
                ? new List<TimeRange>()
                : new VisibilityCalculator(sky).Windows(rec.RaHours, rec.DecDegrees, rec.Conditions, dark.Value);
            windowCache[key] = windows;
            return windows;
        }

        // ------------------------------------------------------------------ placing events

        private PlacementOptions OptionsLocked(bool forResize = false) => new() {
            SnapMinutes = data.Settings.SnapMinutes,
            MinimumLength = TimeSpan.FromMinutes(data.Settings.MinimumEventMinutes),
            AllowOverlap = data.Settings.AllowOverlap,
            NotBeforeUtc = forResize ? null : utcNow()
        };

        private TimeSpan StepLocked => TimeSpan.FromMinutes(Math.Max(1, data.Settings.SnapMinutes));

        private static TimeSpan RoundUp(TimeSpan t, TimeSpan step)
            => t <= TimeSpan.Zero ? TimeSpan.Zero : TimeSpan.FromTicks((t.Ticks + step.Ticks - 1) / step.Ticks * step.Ticks);

        /// <summary>
        /// The most time a target may have planned ahead of now: what its sequence still needs
        /// (exposure left plus overhead), rounded up to the snap step. Planning never goes past it.
        /// </summary>
        private TimeSpan BudgetLocked(string targetId) {
            if (data.Targets.TryGetValue(targetId, out var rec) && rec.MarkedDoneUtc != null) return TimeSpan.Zero;
            double remaining = progress.TryGetValue(targetId, out var eff) ? eff.RemainingSeconds : 0;
            var needed = TimeSpan.FromSeconds(Math.Max(0, remaining) * (1 + Math.Max(0, data.Settings.OverheadPercent) / 100.0));
            return RoundUp(needed, StepLocked);
        }

        private TimeSpan AheadLocked(string targetId, string? exceptEventId, DateTime now)
            => Scheduler.ScheduledAhead(data.Events.Where(e => e.Id != exceptEventId), targetId, now);

        /// <summary>Planned time still allowed for a target, not counting one event (the one being moved or resized).</summary>
        private TimeSpan RoomLocked(string targetId, string? exceptEventId, DateTime now) {
            var room = BudgetLocked(targetId) - AheadLocked(targetId, exceptEventId, now);
            return room > TimeSpan.Zero ? room : TimeSpan.Zero;
        }

        private string NameLocked(string targetId) => data.Targets.TryGetValue(targetId, out var r) ? r.Name : "This target";

        private PlacementResult FullLocked(string targetId, TimeSpan requested) {
            if (data.Targets.TryGetValue(targetId, out var rec) && rec.MarkedDoneUtc != null) {
                return PlacementResult.Reject(requested, $"{rec.Name} is marked done. Right-click it in the list and choose Mark as not done to plan it again.");
            }
            return PlacementResult.Reject(requested, $"{NameLocked(targetId)} is fully planned: its sequence needs {Fmt.Duration(BudgetLocked(targetId))} more. Shorten or remove one of its events to plan it elsewhere.");
        }

        /// <summary>
        /// Where a target may be placed on a night: inside dark time and (unless forced) inside the target's conditions.
        /// Forced placement still keeps the target above the horizon.
        /// </summary>
        private List<TimeRange> WindowsLocked(string targetId, DateOnly night, bool force)
            => force ? AboveHorizonWindowsLocked(targetId, night) : ValidWindowsLocked(targetId, night);

        /// <summary>
        /// Dark time while the target is above 0° altitude: where Shift may force an event. The target's own conditions
        /// are ignored, but a target below the horizon can never be shot.
        /// </summary>
        private List<TimeRange> AboveHorizonWindowsLocked(string targetId, DateOnly night) {
            if (!data.Targets.TryGetValue(targetId, out var rec)) return new List<TimeRange>();
            string key = string.Join("|", "force", targetId, night.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), data.Settings.DarkTime,
                sky.LocationKey, rec.RaHours.ToString("R", CultureInfo.InvariantCulture), rec.DecDegrees.ToString("R", CultureInfo.InvariantCulture));
            if (windowCache.TryGetValue(key, out var cached)) return cached;
            var dark = sky.GetDarkWindow(night, data.Settings.DarkTime);
            var windows = dark == null
                ? new List<TimeRange>()
                : new VisibilityCalculator(sky).Windows(rec.RaHours, rec.DecDegrees, new[] { ConditionSpec.MinAltitude(0) }, dark.Value);
            windowCache[key] = windows;
            return windows;
        }

        /// <summary>Time of a block that falls outside the target's conditions (under 30 s counts as none).</summary>
        private TimeSpan OutsideConditionsLocked(string targetId, DateOnly night, TimeRange block) {
            if (block.IsEmpty) return TimeSpan.Zero;
            var inside = TimeRange.Total(ValidWindowsLocked(targetId, night).Select(w => w.Intersect(block)));
            var outside = block.Duration - inside;
            return outside > TimeSpan.FromSeconds(30) ? outside : TimeSpan.Zero;
        }

        private List<TimeRange> BlocksLocked(DateOnly night, params string[] except)
            => data.Events.Where(e => e.Night == night && !except.Contains(e.Id) && e.Outcome != EventOutcome.Missed).Select(e => e.Range).ToList();

        private PlacementResult PlaceLocked(string targetId, DateOnly night, DateTime? desiredStartUtc, TimeSpan length, bool force,
                                            IEnumerable<TimeRange> busy, PlacementOptions opts)
            => Scheduler.Place(desiredStartUtc, length, WindowsLocked(targetId, night, force), busy, opts);

        /// <summary>Adds the outside-conditions note and the Shift hint, and wraps the result.</summary>
        private PlacementPreview FinishLocked(DragKind kind, string targetId, string? eventId, DateOnly night, PlacementResult result,
                                              bool force, Func<PlacementResult>? tryForced, string? replanOf = null) {
            var outside = TimeSpan.Zero;
            if (result.Placed is TimeRange block) {
                outside = OutsideConditionsLocked(targetId, night, block);
                if (outside > TimeSpan.Zero) result = result with { Message = result.Message + $" {Fmt.Duration(outside)} is outside the target's conditions." };
            }
            if (!force && tryForced != null && result.Outcome != PlacementOutcome.Fits) {
                var forced = tryForced();
                double got = result.Placed?.Duration.TotalSeconds ?? 0;
                if (forced.CanCommit && forced.Placed!.Value.Duration.TotalSeconds > got + 60) {
                    result = result with { Message = result.Message + " Hold Shift to place it outside the target's conditions." };
                }
            }
            return new PlacementPreview {
                Kind = kind, TargetId = targetId, EventId = eventId, Night = night, Result = result,
                Forced = outside > TimeSpan.Zero, OutsideConditions = outside, ReplanOfEventId = replanOf
            };
        }

        /// <summary>
        /// Tries a new event for a target. Length is <paramref name="size"/> when given, otherwise everything the target
        /// still has to place. Either way it never goes past what the sequence still needs. With <paramref name="force"/>
        /// the target's conditions are ignored (dark time and the horizon still count).
        /// </summary>
        public PlacementPreview PreviewNew(string targetId, DateOnly night, DateTime? desiredStartUtc, TimeSpan? size = null, bool force = false) {
            lock (sync) {
                var now = utcNow();
                var room = RoomLocked(targetId, null, now);
                if (room <= TimeSpan.Zero) {
                    return new PlacementPreview { Kind = DragKind.New, TargetId = targetId, Night = night, Result = FullLocked(targetId, size ?? TimeSpan.Zero) };
                }
                string note = "";
                var length = size is TimeSpan s && s > TimeSpan.Zero ? RoundUp(s, StepLocked) : room;
                if (length > room) {
                    length = room;
                    note = $" (cut to {Fmt.Duration(room)}, all this target still needs)";
                }
                var busy = BlocksLocked(night);
                var opts = OptionsLocked();
                var result = PlaceLocked(targetId, night, desiredStartUtc, length, force, busy, opts);
                if (note.Length > 0) result = result with { Message = result.Message + note };
                return FinishLocked(DragKind.New, targetId, null, night, result, force,
                    () => PlaceLocked(targetId, night, desiredStartUtc, length, true, busy, opts));
            }
        }

        /// <summary>
        /// Tries a whole group (a target set's panels) on one night: panels go back to back in file order,
        /// each with its unscheduled time (or <paramref name="sizePerPanel"/>). Panels that don't fit stay to place.
        /// </summary>
        public GroupPlacementPreview PreviewGroup(string groupId, DateOnly night, DateTime? desiredStartUtc, TimeSpan? sizePerPanel = null) {
            lock (sync) {
                var now = utcNow();
                var members = targets.Where(t => t.GroupId == groupId).OrderBy(t => t.GroupIndex).ToList();
                string groupName = members.FirstOrDefault()?.GroupName ?? "Group";
                var placed = new List<PlacementPreview>();
                var notPlaced = new List<string>();
                var busy = BlocksLocked(night);
                var overflow = TimeSpan.Zero;
                DateTime? cursor = desiredStartUtc;

                foreach (var m in members) {
                    if (data.Targets.TryGetValue(m.Id, out var rec) && rec.Hidden) continue;
                    var room = RoomLocked(m.Id, null, now);
                    if (room <= TimeSpan.Zero) continue;   // finished or already fully planned
                    var length = sizePerPanel is TimeSpan fixedSize && fixedSize > TimeSpan.Zero ? RoundUp(fixedSize, StepLocked) : room;
                    if (length > room) length = room;

                    var opts = OptionsLocked();
                    // Keep the panels in order: each one starts no earlier than the end of the one before.
                    if (placed.Count > 0 && cursor is DateTime c) {
                        opts = new PlacementOptions {
                            SnapMinutes = opts.SnapMinutes, MinimumLength = opts.MinimumLength, AllowOverlap = opts.AllowOverlap,
                            NotBeforeUtc = opts.NotBeforeUtc is DateTime nb && nb > c ? nb : c
                        };
                    }
                    var result = PlaceLocked(m.Id, night, cursor, length, false, busy, opts);
                    if (result.CanCommit) {
                        placed.Add(new PlacementPreview { Kind = DragKind.New, TargetId = m.Id, Night = night, Result = result });
                        busy.Add(result.Placed!.Value);
                        cursor = result.Placed!.Value.EndUtc;
                        overflow += result.Overflow;
                    } else {
                        notPlaced.Add(m.Name);
                        overflow += length;
                    }
                }

                string message;
                if (members.Count == 0) message = "This group has no targets.";
                else if (placed.Count == 0 && notPlaced.Count == 0) message = $"{groupName}: nothing left to place.";
                else if (placed.Count == 0) message = $"{groupName}: no panel fits this night.";
                else {
                    var total = TimeRange.Total(placed.Select(p => p.Result.Placed!.Value));
                    message = $"{groupName}: {placed.Count} panel{(placed.Count == 1 ? "" : "s")} placed ({Fmt.Duration(total)})";
                    if (overflow > TimeSpan.FromSeconds(30)) message += $"; {Fmt.Duration(overflow)} does not fit — place the rest on another night";
                    message += ".";
                }
                return new GroupPlacementPreview {
                    GroupId = groupId, GroupName = groupName, Night = night,
                    Panels = placed, NotPlaced = notPlaced, Overflow = overflow, Message = message
                };
            }
        }

        public int CommitGroup(GroupPlacementPreview preview) {
            int count = 0;
            lock (sync) {
                if (!preview.Panels.Any(p => p.Result.CanCommit)) return 0;
                PushUndoLocked($"placing {preview.GroupName}");
                foreach (var p in preview.Panels) {
                    if (CommitLocked(p) != null) count++;
                }
                SaveBoardLocked();
            }
            if (count > 0) RaiseChanged();
            return count;
        }

        /// <summary>
        /// Tries an event at a new night and time. A past event that was missed is not moved: dropping it plans its time
        /// again on the new night, and the original stays as the record of the missed night.
        /// </summary>
        public PlacementPreview PreviewMove(string eventId, DateOnly night, DateTime? desiredStartUtc, bool force = false) {
            lock (sync) {
                var e = data.Events.FirstOrDefault(x => x.Id == eventId);
                if (e == null) return new PlacementPreview { Kind = DragKind.Move, EventId = eventId, Night = night };
                var now = utcNow();
                var opts = OptionsLocked();

                if (e.IsSettled || e.EndUtc <= now) {
                    var status = e.Outcome == EventOutcome.Missed ? EventStatus.Missed : OutcomesLocked(now)[e.Id].Status;
                    if (status is not (EventStatus.Missed or EventStatus.Unknown)) {
                        return new PlacementPreview {
                            Kind = DragKind.Move, TargetId = e.TargetId, EventId = e.Id, Night = night,
                            Result = PlacementResult.Reject(e.Duration, "Past events that took frames stay where they happened. Drag the target from the list to plan more.")
                        };
                    }
                    var plannedLength = PlannedLength(e);
                    var replanRoom = RoomLocked(e.TargetId, null, now);
                    if (replanRoom <= TimeSpan.Zero) {
                        return new PlacementPreview { Kind = DragKind.New, TargetId = e.TargetId, Night = night, Result = FullLocked(e.TargetId, plannedLength), ReplanOfEventId = e.Id };
                    }
                    if (plannedLength > replanRoom) plannedLength = replanRoom;
                    var replanBusy = BlocksLocked(night);
                    var replan = PlaceLocked(e.TargetId, night, desiredStartUtc, plannedLength, force, replanBusy, opts);
                    if (replan.CanCommit) replan = replan with { Message = replan.Message + " The missed night stays on the calendar as a record." };
                    return FinishLocked(DragKind.New, e.TargetId, null, night, replan, force,
                        () => PlaceLocked(e.TargetId, night, desiredStartUtc, plannedLength, true, replanBusy, opts), replanOf: e.Id);
                }

                var room = RoomLocked(e.TargetId, e.Id, now);
                var length = e.Duration;
                string note = "";
                if (length > room) {
                    if (room < TimeSpan.FromMinutes(data.Settings.MinimumEventMinutes)) {
                        return new PlacementPreview { Kind = DragKind.Move, TargetId = e.TargetId, EventId = e.Id, Night = night, Result = FullLocked(e.TargetId, length) };
                    }
                    length = room;
                    note = $" (cut to {Fmt.Duration(room)}, all this target still needs)";
                }
                var busy = BlocksLocked(night, e.Id);
                var result = PlaceLocked(e.TargetId, night, desiredStartUtc, length, force, busy, opts);
                if (note.Length > 0 && result.CanCommit) result = result with { Message = result.Message + note };
                return FinishLocked(DragKind.Move, e.TargetId, e.Id, night, result, force,
                    () => PlaceLocked(e.TargetId, night, desiredStartUtc, length, true, busy, opts));
            }
        }

        /// <summary>
        /// Tries moving several events together. Each moves by the same number of nights and the same clock offset as the
        /// one being dragged (the anchor), and is then placed as a single drag would place it: where it lands if that is
        /// free, otherwise in the nearest free slot on its night, shortened if needed. Events that are not being moved
        /// never move. Events that already happened stay where they are; missed ones are planned again at the new place
        /// and stay behind as records.
        /// </summary>
        /// <param name="anchorDesiredStartUtc">Where the anchor would start; null keeps its clock time (Month view).</param>
        public MultiMovePreview PreviewMoveMany(IReadOnlyCollection<string> eventIds, string anchorId, DateOnly anchorNight, DateTime? anchorDesiredStartUtc, bool force = false) {
            lock (sync) {
                var anchor = data.Events.FirstOrDefault(e => e.Id == anchorId);
                if (anchor == null) return new MultiMovePreview { Night = anchorNight, Message = "That event is gone." };
                var now = utcNow();
                var zone = sky.TimeZone;
                DateTime SameClock(DateTime utc, DateOnly from, DateOnly to)
                    => NightMath.FromClockHoursAfterNoon(NightMath.ClockHoursAfterNoon(utc, from, zone), to, zone);

                int nightDelta = anchorNight.DayNumber - anchor.Night.DayNumber;
                var anchorFrom = anchor.IsSettled ? anchor.PlannedStartUtc ?? anchor.StartUtc : anchor.StartUtc;
                var clockDelta = anchorDesiredStartUtc is DateTime wanted ? wanted - SameClock(anchorFrom, anchor.Night, anchorNight) : TimeSpan.Zero;

                var ids = new HashSet<string>(eventIds) { anchorId };
                var outcomes = OutcomesLocked(now);
                var opts = OptionsLocked();
                var skipped = new List<string>();
                var plans = new List<(PlanEvent E, DateOnly Night, DateTime Desired, TimeSpan Length, bool Replan)>();
                var replanned = new Dictionary<string, TimeSpan>();

                foreach (var e in data.Events.Where(x => ids.Contains(x.Id)).OrderBy(x => x.StartUtc)) {
                    var night = e.Night.AddDays(nightDelta);
                    bool past = e.IsSettled || e.EndUtc <= now;
                    if (past) {
                        bool missed = e.Outcome == EventOutcome.Missed || (!e.IsSettled && outcomes[e.Id].Status is EventStatus.Missed or EventStatus.Unknown);
                        if (!missed) { skipped.Add(e.Id); continue; }
                        // Planning a missed event again adds time: keep within what the target still needs.
                        var room = RoomLocked(e.TargetId, null, now) - (replanned.TryGetValue(e.TargetId, out var used) ? used : TimeSpan.Zero);
                        var length = PlannedLength(e);
                        if (length > room) length = room;
                        if (length < TimeSpan.FromMinutes(1)) { skipped.Add(e.Id); continue; }
                        replanned[e.TargetId] = (replanned.TryGetValue(e.TargetId, out var u) ? u : TimeSpan.Zero) + length;
                        plans.Add((e, night, SameClock(e.PlannedStartUtc ?? e.StartUtc, e.Night, night) + clockDelta, length, true));
                        continue;
                    }
                    if (e.StartUtc <= now) { skipped.Add(e.Id); continue; }   // running now
                    plans.Add((e, night, SameClock(e.StartUtc, e.Night, night) + clockDelta, e.Duration, false));
                }

                // Blocks that stay put, per night; each placed event is added so the moved ones never overlap each other.
                var busyByNight = new Dictionary<DateOnly, List<TimeRange>>();
                List<TimeRange> Busy(DateOnly n) {
                    if (!busyByNight.TryGetValue(n, out var list)) {
                        list = data.Events.Where(x => x.Night == n && !ids.Contains(x.Id) && x.Outcome != EventOutcome.Missed).Select(x => x.Range).ToList();
                        busyByNight[n] = list;
                    }
                    return list;
                }

                var items = new List<PlacementPreview>();
                var refused = new List<string>();
                int shortened = 0;
                foreach (var p in plans.OrderBy(x => x.Desired)) {
                    var busy = Busy(p.Night);
                    var result = PlaceLocked(p.E.TargetId, p.Night, p.Desired, p.Length, force, busy, opts);
                    if (result.CanCommit) {
                        busy.Add(result.Placed!.Value);
                        if (result.Outcome == PlacementOutcome.Clipped) shortened++;
                    } else {
                        refused.Add($"{NameLocked(p.E.TargetId)} ({NightText(p.Night)}): {result.Message}");
                    }
                    items.Add(FinishLocked(p.Replan ? DragKind.New : DragKind.Move, p.E.TargetId, p.Replan ? null : p.E.Id, p.Night, result, force, null,
                                           p.Replan ? p.E.Id : null));
                }

                string message;
                PlacementOutcome outcome;
                if (items.Count == 0) {
                    outcome = PlacementOutcome.Rejected;
                    message = "These events already happened, so they stay where they are.";
                } else if (refused.Count > 0) {
                    outcome = PlacementOutcome.Rejected;
                    message = $"{refused.Count} of {items.Count} events can't go there. {refused[0]}";
                } else {
                    outcome = shortened > 0 ? PlacementOutcome.Clipped : PlacementOutcome.Fits;
                    string where = nightDelta == 0 ? "" : $" {Math.Abs(nightDelta)} night{(Math.Abs(nightDelta) == 1 ? "" : "s")} {(nightDelta > 0 ? "later" : "earlier")}";
                    message = $"{items.Count} events{where}.";
                    if (shortened > 0) message += $" {shortened} shortened to fit; that time goes back to place.";
                    if (items.Any(i => i.ReplanOfEventId != null)) message += " Missed events stay behind as records.";
                }
                if (skipped.Count > 0 && items.Count > 0) message += $" {skipped.Count} already happened and stay.";
                return new MultiMovePreview { Night = anchorNight, Items = items, Skipped = skipped, Outcome = outcome, Message = message };
            }
        }

        /// <summary>Commits a multi-event move as one step (one Ctrl+Z).</summary>
        public int CommitMany(MultiMovePreview preview) {
            if (!preview.CanCommit) return 0;
            int count = 0;
            lock (sync) {
                PushUndoLocked($"moving {preview.Items.Count} events");
                foreach (var item in preview.Items) {
                    if (CommitLocked(item) != null) count++;
                }
                if (count == 0) { DropUndoLocked(); return 0; }
                SaveBoardLocked();
            }
            RaiseChanged();
            return count;
        }

        /// <summary>Length an event was planned with (before settling shrank it).</summary>
        private static TimeSpan PlannedLength(PlanEvent e) {
            var start = e.PlannedStartUtc ?? e.StartUtc;
            var end = e.PlannedEndUtc ?? e.EndUtc;
            return end > start ? end - start : TimeSpan.Zero;
        }

        /// <summary>
        /// Tries a new end for an event. An event already outside its target's conditions stays forced, so resizing
        /// never makes it jump.
        /// </summary>
        public PlacementPreview PreviewResize(string eventId, DateTime desiredEndUtc, bool force = false) {
            lock (sync) {
                var e = data.Events.FirstOrDefault(x => x.Id == eventId);
                if (e == null) return new PlacementPreview { Kind = DragKind.Resize, EventId = eventId };
                if (e.IsSettled) {
                    return new PlacementPreview { Kind = DragKind.Resize, TargetId = e.TargetId, EventId = e.Id, Night = e.Night,
                        Result = PlacementResult.Reject(e.Duration, "This event is a record of a past night.") };
                }
                bool forced = force || e.Forced || OutsideConditionsLocked(e.TargetId, e.Night, e.Range) > TimeSpan.Zero;
                var opts = OptionsLocked(forResize: true);
                var now = utcNow();
                var end = Scheduler.Snap(SkyMath.AsUtc(desiredEndUtc), opts.SnapMinutes);
                // Only the part ahead of now counts against the budget; an event can always be made shorter.
                var budgetEnd = (e.StartUtc > now ? e.StartUtc : now) + RoomLocked(e.TargetId, e.Id, now);
                var limit = budgetEnd > e.EndUtc ? budgetEnd : e.EndUtc;
                bool capped = end > limit;
                if (capped) end = limit;
                var length = end - e.StartUtc;
                if (length < opts.MinimumLength) length = opts.MinimumLength;
                var busy = BlocksLocked(e.Night, e.Id);
                var result = PlaceLocked(e.TargetId, e.Night, e.StartUtc, length, forced, busy, opts);
                if (capped && result.CanCommit) {
                    result = result with { Outcome = PlacementOutcome.Clipped, Message = $"{Fmt.Duration(result.Placed!.Value.Duration)}: as long as it can be — this target's plan is full." };
                } else if (result.Outcome == PlacementOutcome.Clipped) {
                    result = result with { Message = $"{Fmt.Duration(result.Placed!.Value.Duration)}: as long as this slot allows." };
                }
                return FinishLocked(DragKind.Resize, e.TargetId, e.Id, e.Night, result, forced,
                    forced ? null : () => PlaceLocked(e.TargetId, e.Night, e.StartUtc, length, true, busy, opts));
            }
        }

        public PlanEvent? Commit(PlacementPreview preview) {
            if (!preview.Result.CanCommit) return null;
            PlanEvent? changed;
            lock (sync) {
                string verb = preview.ReplanOfEventId != null ? "planning again" : preview.Kind switch { DragKind.New => "placing", DragKind.Move => "moving", _ => "resizing" };
                PushUndoLocked($"{verb} {NameLocked(preview.TargetId)}");
                changed = CommitLocked(preview);
                if (changed == null) { DropUndoLocked(); return null; }
                SaveBoardLocked();
                changed = changed.Clone();
            }
            RaiseChanged();
            return changed;
        }

        private PlanEvent? CommitLocked(PlacementPreview preview) {
            if (!preview.Result.CanCommit) return null;
            var placed = preview.Result.Placed!.Value;
            PlanEvent? changed;
            if (preview.Kind == DragKind.New) {
                changed = new PlanEvent {
                    Id = StableId.New(),
                    TargetId = preview.TargetId,
                    Night = preview.Night,
                    StartUtc = placed.StartUtc,
                    EndUtc = placed.EndUtc,
                    Forced = preview.Forced
                };
                data.Events.Add(changed);
                if (preview.ReplanOfEventId != null) {
                    var source = data.Events.FirstOrDefault(x => x.Id == preview.ReplanOfEventId);
                    if (source != null && !source.IsSettled) MarkMissedLocked(source, utcNow());
                }
            } else {
                changed = data.Events.FirstOrDefault(e => e.Id == preview.EventId);
                if (changed == null || changed.IsSettled) return null;
                changed.Night = preview.Night;
                changed.StartUtc = placed.StartUtc;
                changed.EndUtc = placed.EndUtc;
                changed.Forced = preview.Forced;
            }
            data.Events.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));
            return changed;
        }

        public void RemoveEvent(string eventId) => RemoveEvents(new[] { eventId });

        public int RemoveEvents(IEnumerable<string> eventIds) {
            int removed;
            lock (sync) {
                var ids = new HashSet<string>(eventIds);
                if (!data.Events.Any(e => ids.Contains(e.Id))) return 0;
                PushUndoLocked(ids.Count == 1 ? $"removing {NameLocked(data.Events.First(e => ids.Contains(e.Id)).TargetId)}" : $"removing {ids.Count} events");
                removed = data.Events.RemoveAll(e => ids.Contains(e.Id));
                SaveBoardLocked();
            }
            RaiseChanged();
            return removed;
        }

        /// <summary>Cuts an event in two at its middle (snapped), so each half can be moved on its own.</summary>
        public bool SplitEvent(string eventId) {
            lock (sync) {
                var e = data.Events.FirstOrDefault(x => x.Id == eventId);
                if (e == null || e.IsSettled) return false;
                var min = TimeSpan.FromMinutes(data.Settings.MinimumEventMinutes);
                var mid = Scheduler.Snap(e.StartUtc + TimeSpan.FromTicks(e.Duration.Ticks / 2), data.Settings.SnapMinutes);
                if (mid - e.StartUtc < min || e.EndUtc - mid < min) return false;
                PushUndoLocked($"splitting {NameLocked(e.TargetId)}");
                var second = e.Clone();
                second.Id = StableId.New();
                second.StartUtc = mid;
                e.EndUtc = mid;
                data.Events.Add(second);
                data.Events.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));
                SaveBoardLocked();
            }
            RaiseChanged();
            return true;
        }

        // ------------------------------------------------------------------ undo

        private const int UndoDepth = 30;
        private readonly List<(string Label, List<PlanEvent> Events)> undoStack = new();

        private void PushUndoLocked(string label) {
            undoStack.Add((label, data.Events.Select(e => e.Clone()).ToList()));
            if (undoStack.Count > UndoDepth) undoStack.RemoveAt(0);
            undoDirty = true;
        }

        private void DropUndoLocked() {
            if (undoStack.Count > 0) undoStack.RemoveAt(undoStack.Count - 1);
            undoDirty = true;
        }

        private bool undoDirty;

        /// <summary>
        /// The undo history goes to its own file, so it survives NINA restarts. It is written with the board, so the two
        /// files always agree.
        /// </summary>
        private void SaveUndoLocked() {
            if (!loaded || !undoDirty) return;
            undoDirty = false;
            try {
                Store.SaveUndo(new UndoData { Steps = undoStack.Select(u => new UndoStep { Label = u.Label, Events = u.Events }).ToList() });
            } catch (Exception) {
                // Losing the undo file is not worth failing the change for; the board itself is saved.
            }
        }

        /// <summary>What the next undo would take back, or null.</summary>
        public string? UndoLabel {
            get { lock (sync) return undoStack.Count > 0 ? undoStack[^1].Label : null; }
        }

        /// <summary>Puts the plan back as it was before the last change. Returns what was undone, or null.</summary>
        public string? Undo() {
            string label;
            lock (sync) {
                if (undoStack.Count == 0) return null;
                var last = undoStack[^1];
                undoStack.RemoveAt(undoStack.Count - 1);
                data.Events = last.Events.Select(e => e.Clone()).ToList();
                label = last.Label;
                undoDirty = true;
                SaveBoardLocked();
            }
            RaiseChanged();
            return label;
        }

        // ------------------------------------------------------------------ what happened: outcomes, settling, shifting

        /// <summary>Raised when the morning settle changed the plan.</summary>
        public event EventHandler<PlanChangeReport>? PlanSettled;

        private Dictionary<string, EventProgress> OutcomesLocked(DateTime now)
            => OutcomeTracker.Evaluate(data.Events, captures, data.Settings.OverheadPercent, now, sky.TimeZone, data.Settings.LogCaptures);

        public Dictionary<string, EventProgress> EventOutcomes() {
            lock (sync) return OutcomesLocked(utcNow());
        }

        /// <summary>When a night's dark time is over (or its span, if it never gets dark).</summary>
        private DateTime NightOverLocked(DateOnly night)
            => sky.GetDarkWindow(night, data.Settings.DarkTime)?.EndUtc ?? NightMath.Span(night, sky.TimeZone).EndUtc;

        /// <summary>The settle time the morning after a night, never before its dawn.</summary>
        private DateTime SettleMomentLocked(DateOnly night) {
            var local = night.AddDays(1).ToDateTime(TimeOnly.MinValue).AddMinutes(Math.Clamp(data.Settings.SettleAtMinutes, 0, 24 * 60 - 1));
            var at = NightMath.LocalToUtc(local, sky.TimeZone);
            var over = NightOverLocked(night);
            return at > over ? at : over;
        }

        private DateOnly LastSettleableNightLocked(DateTime now) {
            var n = NightMath.NightOf(now, sky.TimeZone);
            for (int i = 0; i < 3 && SettleMomentLocked(n) > now; i++) n = n.AddDays(-1);
            return n;
        }

        private bool CanSettleLocked(DateOnly night, DateTime now)
            => now >= NightOverLocked(night) && data.Events.Any(e => e.Night == night && !e.IsSettled && e.EndUtc <= now);

        private void MarkMissedLocked(PlanEvent e, DateTime now) {
            e.Outcome = EventOutcome.Missed;
            e.SettledUtc = now;
            e.Frames = 0;
            e.AchievedSeconds = 0;
            e.PlannedStartUtc ??= e.StartUtc;
            e.PlannedEndUtc ??= e.EndUtc;
        }

        private string NightText(DateOnly night) => night.ToString("ddd d MMM", CultureInfo.CurrentCulture);
        private string ClockText(DateTime utc) => NightMath.ToLocal(utc, sky.TimeZone).ToString("HH:mm", CultureInfo.InvariantCulture);

        /// <summary>
        /// Settles one finished night: each event becomes a record (done, partly done or missed). A partly done event
        /// shrinks to the time its frames took, and the time still missing goes onto the target's next events.
        /// Missed events are only marked; <see cref="ShiftEvents"/> or <see cref="AddMissedTime"/> deal with them.
        /// </summary>
        private void SettleNightLocked(DateOnly night, DateTime now, PlanChangeReport report) {
            var evs = data.Events.Where(e => e.Night == night && !e.IsSettled && e.EndUtc <= now).OrderBy(e => e.StartUtc).ToList();
            if (evs.Count == 0) return;
            var outcomes = OutcomesLocked(now);
            var carry = new Dictionary<string, TimeSpan>();
            foreach (var e in evs) {
                var p = outcomes[e.Id];
                if (p.Status == EventStatus.Unknown) continue;
                string name = NameLocked(e.TargetId);
                if (p.Status == EventStatus.Missed) {
                    MarkMissedLocked(e, now);
                    report.Details.Add($"{name} was missed on {NightText(night)}.");
                    report.Changed++;
                    continue;
                }
                var planned = e.Duration;
                e.SettledUtc = now;
                e.Frames = p.Frames;
                e.AchievedSeconds = p.AchievedSeconds;
                if (p.Status == EventStatus.Done) {
                    e.Outcome = EventOutcome.Done;
                } else {
                    e.Outcome = EventOutcome.Partial;
                    e.PlannedStartUtc = e.StartUtc;
                    e.PlannedEndUtc = e.EndUtc;
                    // The block now shows when its frames were actually taken.
                    if (p.FirstUtc is DateTime first && p.LastUtc is DateTime last && last > first) {
                        e.StartUtc = first;
                        e.EndUtc = last;
                    }
                    var shortfall = planned - TimeSpan.FromSeconds(p.AchievedSeconds);
                    if (shortfall > TimeSpan.Zero) carry[e.TargetId] = (carry.TryGetValue(e.TargetId, out var c) ? c : TimeSpan.Zero) + shortfall;
                    report.Details.Add($"{name}: {Fmt.DurationSeconds(p.AchievedSeconds)} of {Fmt.Duration(planned)} done on {NightText(night)}.");
                }
                report.Changed++;
            }
            data.Events.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));
            foreach (var (targetId, amount) in carry) CarryLocked(targetId, amount, now, report);
        }

        /// <summary>
        /// Adds missing time to a target's next events, growing each up to the end of its slot, then lets the
        /// cascade move whatever is in the way. Never plans past what the sequence still needs. Forced events are left alone.
        /// </summary>
        private void CarryLocked(string targetId, TimeSpan amount, DateTime now, PlanChangeReport report) {
            string name = NameLocked(targetId);
            var room = RoomLocked(targetId, null, now);
            if (amount > room) amount = room;
            if (amount < TimeSpan.FromMinutes(1)) return;
            var grown = new HashSet<string>();
            foreach (var e in data.Events.Where(x => x.TargetId == targetId && !x.IsSettled && !x.Forced && x.StartUtc > now).OrderBy(x => x.StartUtc).ToList()) {
                if (amount < TimeSpan.FromMinutes(1)) break;
                var window = ValidWindowsLocked(targetId, e.Night)
                    .FirstOrDefault(w => w.StartUtc <= e.StartUtc && w.EndUtc > e.StartUtc);
                if (window.IsEmpty) continue;
                var maxEnd = window.EndUtc;
                // Never grow into something the cascade cannot move.
                foreach (var f in data.Events.Where(x => x.Night == e.Night && x.Id != e.Id && x.StartUtc >= e.EndUtc && !IsMovableLocked(x, now, null))) {
                    if (f.StartUtc < maxEnd) maxEnd = f.StartUtc;
                }
                var grow = maxEnd - e.EndUtc;
                if (grow > amount) grow = amount;
                grow = TimeSpan.FromMinutes(Math.Floor(grow.TotalMinutes));
                if (grow < TimeSpan.FromMinutes(1)) continue;
                e.EndUtc += grow;
                amount -= grow;
                grown.Add(e.Id);
                report.Details.Add($"{name}: {Fmt.Duration(grow)} added to {NightText(e.Night)}.");
            }
            if (grown.Count > 0) ReflowLocked(new Dictionary<string, (DateOnly, DateTime)>(), grown, report);
            if (amount >= TimeSpan.FromMinutes(1)) report.Details.Add($"{name}: {Fmt.Duration(amount)} is back to place.");
        }

        /// <summary>Events the cascade may move: upcoming, not settled, not forced, and inside their conditions (or being moved on purpose).</summary>
        private bool IsMovableLocked(PlanEvent e, DateTime now, IReadOnlyDictionary<string, (DateOnly, DateTime)>? overrides) {
            if (e.IsSettled || e.Forced || e.StartUtc <= now) return false;
            if (overrides != null && overrides.ContainsKey(e.Id)) return true;
            return OutsideConditionsLocked(e.TargetId, e.Night, e.Range) == TimeSpan.Zero;
        }

        /// <summary>
        /// Runs the cascade over every movable event. <paramref name="overrides"/> gives new wanted places (a shift);
        /// <paramref name="priority"/> events win ties. Events that no longer fit within 30 nights are removed and
        /// their time goes back to "to place".
        /// </summary>
        private void ReflowLocked(Dictionary<string, (DateOnly Night, DateTime Start)> overrides, ICollection<string> priority, PlanChangeReport report) {
            var now = utcNow();
            var movable = data.Events.Where(e => IsMovableLocked(e, now, overrides)).ToList();
            var movableIds = new HashSet<string>(movable.Select(e => e.Id));
            var items = movable.Select(e => {
                var (night, start) = overrides.TryGetValue(e.Id, out var o) ? o : (e.Night, e.StartUtc);
                return new ReflowItem {
                    Id = e.Id, TargetId = e.TargetId, Night = night, DesiredStartUtc = start, Length = e.Duration,
                    Priority = priority.Contains(e.Id)
                };
            }).ToList();

            var result = Reflow.Run(items,
                (targetId, night) => ValidWindowsLocked(targetId, night),
                night => data.Events.Where(e => e.Night == night && !movableIds.Contains(e.Id) && e.Outcome != EventOutcome.Missed).Select(e => e.Range).ToList(),
                new ReflowOptions {
                    NotBeforeUtc = now, HorizonNights = 30, AllowOverlap = data.Settings.AllowOverlap,
                    MinimumLength = TimeSpan.FromMinutes(Math.Max(1, data.Settings.MinimumEventMinutes))
                });

            var byId = data.Events.ToDictionary(e => e.Id);
            foreach (var p in result.Placed) {
                var e = byId[p.Id];
                if (e.Night == p.Night && e.StartUtc == p.Block.StartUtc && e.EndUtc == p.Block.EndUtc) continue;
                bool asked = overrides.TryGetValue(e.Id, out var o) && o.Night == p.Night && o.Start == p.Block.StartUtc;
                bool moved = e.Night != p.Night || e.StartUtc != p.Block.StartUtc;
                e.Night = p.Night;
                e.StartUtc = p.Block.StartUtc;
                e.EndUtc = p.Block.EndUtc;
                if (moved && !asked) report.Details.Add($"{NameLocked(e.TargetId)} moved to {NightText(p.Night)} {ClockText(p.Block.StartUtc)}.");
                if (p.Overflow >= TimeSpan.FromMinutes(1)) report.Details.Add($"{NameLocked(e.TargetId)} on {NightText(p.Night)} shortened by {Fmt.Duration(p.Overflow)} to fit; that time is back to place.");
                report.Changed++;
            }
            foreach (var id in result.Unplaced) {
                var e = byId[id];
                report.Details.Add($"{NameLocked(e.TargetId)} ({NightText(e.Night)}) no longer fits in the next 30 nights; its time is back to place.");
                data.Events.Remove(e);
                report.Changed++;
            }
            data.Events.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));
            MergeTouchingLocked(now);
        }

        /// <summary>
        /// Joins upcoming events of the same target that end up back to back on one night (the cascade can leave a
        /// target's event pressed against another of its own).
        /// </summary>
        private void MergeTouchingLocked(DateTime now) {
            var list = data.Events.Where(e => !e.IsSettled && !e.Forced && e.StartUtc > now).OrderBy(e => e.StartUtc).ToList();
            for (int i = 0; i < list.Count; i++) {
                var a = list[i];
                if (!data.Events.Contains(a)) continue;
                var b = list.Skip(i + 1).FirstOrDefault(x => x.TargetId == a.TargetId && x.Night == a.Night && Math.Abs((x.StartUtc - a.EndUtc).TotalSeconds) < 60);
                if (b == null) continue;
                a.EndUtc = b.EndUtc;
                data.Events.Remove(b);
                list.Remove(b);
                i--;
            }
        }

        /// <summary>Settles a finished night now, without waiting for the morning settle time.</summary>
        public PlanChangeReport SettleNight(DateOnly night) {
            var report = new PlanChangeReport();
            lock (sync) {
                var now = utcNow();
                if (!CanSettleLocked(night, now)) {
                    report.Summary = now < NightOverLocked(night) ? $"{NightText(night)} is not over yet." : $"Nothing to settle on {NightText(night)}.";
                    return report;
                }
                PushUndoLocked($"settling {NightText(night)}");
                SettleNightLocked(night, now, report);
                if (report.Changed == 0) { DropUndoLocked(); report.Summary = $"Nothing to settle on {NightText(night)}."; return report; }
                report.Summary = $"Settled {NightText(night)}.";
                SaveBoardLocked();
            }
            RaiseChanged();
            return report;
        }

        /// <summary>
        /// The morning settle. Runs each finished night once, at the settle time (default 08:00) the next morning, so
        /// nothing changes while a night is under way. Nights before the board first ran are never settled by themselves.
        /// </summary>
        public PlanChangeReport? AutoSettleIfDue() {
            var report = new PlanChangeReport();
            lock (sync) {
                if (!loaded || LastScan == null) return null;
                var now = utcNow();
                var last = LastSettleableNightLocked(now);
                if (data.AutoSettledThrough is not DateOnly mark) {
                    data.AutoSettledThrough = last;
                    SaveBoardLocked();
                    return null;
                }
                if (last <= mark) return null;
                bool active = data.Settings.AutoSettle && data.Settings.LogCaptures;
                bool pushed = false;
                for (var n = mark.AddDays(1); n <= last && active; n = n.AddDays(1)) {
                    if (!CanSettleLocked(n, now)) continue;
                    if (!pushed) { PushUndoLocked("the morning settle"); pushed = true; }
                    SettleNightLocked(n, now, report);
                }
                data.AutoSettledThrough = last;
                if (pushed && report.Changed == 0) DropUndoLocked();
                SaveBoardLocked();
                if (report.Changed == 0) return null;
                report.Summary = last == mark.AddDays(1) ? $"Settled the night of {NightText(last)}." : $"Settled the nights up to {NightText(last)}.";
            }
            PlanSettled?.Invoke(this, report);
            RaiseChanged();
            return report;
        }

        /// <summary>
        /// Moves events by whole nights at the same clock time, then lets the cascade sort out clashes. Missed past events
        /// are planned again on the new night and stay behind as records. Records with frames and running events stay put.
        /// </summary>
        public PlanChangeReport ShiftEvents(IEnumerable<string> eventIds, int nights) {
            var report = new PlanChangeReport();
            if (nights == 0) return report;
            lock (sync) {
                var now = utcNow();
                var zone = sky.TimeZone;
                var ids = eventIds.Distinct().ToList();
                var list = data.Events.Where(e => ids.Contains(e.Id)).OrderBy(e => e.StartUtc).ToList();
                if (list.Count == 0) return report;
                PushUndoLocked(list.Count == 1 ? $"moving {NameLocked(list[0].TargetId)}" : $"moving {list.Count} events");
                var outcomes = OutcomesLocked(now);
                var overrides = new Dictionary<string, (DateOnly, DateTime)>();
                var priority = new HashSet<string>();
                int skipped = 0;
                DateTime SameClock(DateTime utc, DateOnly from, DateOnly to)
                    => NightMath.FromClockHoursAfterNoon(NightMath.ClockHoursAfterNoon(utc, from, zone), to, zone);

                foreach (var e in list) {
                    var target = e.Night.AddDays(nights);
                    bool past = e.IsSettled || e.EndUtc <= now;
                    if (past) {
                        bool missed = e.Outcome == EventOutcome.Missed || (!e.IsSettled && outcomes[e.Id].Status is EventStatus.Missed or EventStatus.Unknown);
                        if (!missed || nights < 0) { skipped++; continue; }
                        var length = PlannedLength(e);
                        var room = RoomLocked(e.TargetId, null, now);
                        if (length > room) length = room;
                        if (!e.IsSettled) MarkMissedLocked(e, now);
                        if (length < TimeSpan.FromMinutes(1)) { report.Details.Add($"{NameLocked(e.TargetId)} is already fully planned elsewhere."); continue; }
                        var start = SameClock(e.PlannedStartUtc ?? e.StartUtc, e.Night, target);
                        var copy = new PlanEvent {
                            Id = StableId.New(), TargetId = e.TargetId, Night = target,
                            StartUtc = start, EndUtc = start + length
                        };
                        data.Events.Add(copy);
                        overrides[copy.Id] = (target, start);
                        priority.Add(copy.Id);
                        report.Changed++;
                        continue;
                    }
                    if (e.StartUtc <= now) { skipped++; continue; }   // running now
                    if (e.Forced) {
                        // Forced events move exactly; they are not checked against conditions.
                        e.StartUtc = SameClock(e.StartUtc, e.Night, target);
                        e.EndUtc = SameClock(e.EndUtc, e.Night, target);
                        e.Night = target;
                        report.Changed++;
                        continue;
                    }
                    overrides[e.Id] = (target, SameClock(e.StartUtc, e.Night, target));
                    priority.Add(e.Id);
                    report.Changed++;
                }
                ReflowLocked(overrides, priority, report);
                if (report.Changed == 0) { DropUndoLocked(); }
                else SaveBoardLocked();
                string dir = nights > 0 ? "later" : "earlier";
                int n = Math.Abs(nights);
                report.Summary = report.Changed == 0 ? "Nothing could be moved." : $"Moved {(list.Count - skipped == 1 ? "1 event" : $"{list.Count - skipped} events")} {n} night{(n == 1 ? "" : "s")} {dir}.";
                if (skipped > 0) report.Details.Insert(0, $"{skipped} event{(skipped == 1 ? " was" : "s were")} left alone (already happened or running now).");
            }
            if (report.Changed > 0) RaiseChanged();
            return report;
        }

        /// <summary>Shifts the plan from one night onward: that night's missed events and every later event move one night later.</summary>
        public PlanChangeReport ShiftPlanFrom(DateOnly night, int nights = 1) {
            List<string> ids;
            lock (sync) {
                var now = utcNow();
                var outcomes = OutcomesLocked(now);
                ids = data.Events.Where(e => e.Night >= night && (
                        (!e.IsSettled && e.EndUtc > now)
                        || (e.Night == night && (e.Outcome == EventOutcome.Missed || (!e.IsSettled && outcomes[e.Id].Status is EventStatus.Missed or EventStatus.Unknown)))))
                    .Select(e => e.Id).ToList();
            }
            return ShiftEvents(ids, nights);
        }

        /// <summary>Gives missed events' time to each target's next events (growing them; the cascade moves what is in the way).</summary>
        public PlanChangeReport AddMissedTime(IEnumerable<string> eventIds) {
            var report = new PlanChangeReport();
            lock (sync) {
                var now = utcNow();
                var outcomes = OutcomesLocked(now);
                var ids = new HashSet<string>(eventIds);
                var missed = data.Events.Where(e => ids.Contains(e.Id) && (e.Outcome == EventOutcome.Missed
                                  || (!e.IsSettled && e.EndUtc <= now && outcomes[e.Id].Status is EventStatus.Missed or EventStatus.Unknown))).ToList();
                if (missed.Count == 0) { report.Summary = "No missed events selected."; return report; }
                PushUndoLocked("adding missed time");
                var carry = new Dictionary<string, TimeSpan>();
                foreach (var e in missed) {
                    if (!e.IsSettled) MarkMissedLocked(e, now);
                    carry[e.TargetId] = (carry.TryGetValue(e.TargetId, out var c) ? c : TimeSpan.Zero) + PlannedLength(e);
                }
                foreach (var (targetId, amount) in carry) CarryLocked(targetId, amount, now, report);
                report.Changed += missed.Count;
                report.Summary = "Missed time added to the next events.";
                SaveBoardLocked();
            }
            RaiseChanged();
            return report;
        }

        // ------------------------------------------------------------------ frames per event

        /// <summary>
        /// The frames of one event: those taken so far, and the forecast for the rest, in the order the sequence shoots
        /// its rows. The forecast walks through all the target's upcoming events in time order, so earlier events use up
        /// frames first.
        /// </summary>
        public FramePlanView? FramePlan(string eventId) {
            lock (sync) {
                var e = data.Events.FirstOrDefault(x => x.Id == eventId);
                if (e == null) return null;
                var now = utcNow();
                var p = OutcomesLocked(now)[e.Id];
                var taken = FramePlanner.FromCaptures(p.Captures);
                var planned = new List<FrameSegment>();
                string note;
                data.Targets.TryGetValue(e.TargetId, out var rec);
                double overhead = data.Settings.OverheadPercent;
                if (e.IsSettled || e.EndUtc <= now) {
                    note = taken.Count == 0 ? "No frames were logged for this event." : $"{p.Frames} frames taken, {Fmt.DurationSeconds(p.ExposureSeconds)} of exposure.";
                } else if (rec?.MarkedDoneUtc != null) {
                    note = "This target is marked done, so no frames are planned.";
                } else if (!targetById.TryGetValue(e.TargetId, out var t) || !progress.TryGetValue(e.TargetId, out var eff)) {
                    note = "The target's sequence file was not found.";
                } else {
                    var slots = data.Events
                        .Where(x => x.TargetId == e.TargetId && !x.IsSettled && x.EndUtc > now)
                        .OrderBy(x => x.StartUtc)
                        .Select(x => new FrameSlot(x.Id, x.StartUtc > now ? x.StartUtc : now, x.EndUtc));
                    var forecast = FramePlanner.Forecast(eff.Lines, t.Order, overhead, slots);
                    planned = forecast.TryGetValue(e.Id, out var mine) ? mine : new List<FrameSegment>();
                    string order = t.Order == ShootingOrder.Rotate ? "one frame of each row in turn" : "row by row, as in the file";
                    note = planned.Count == 0
                        ? "No frames left for this event: earlier events of this target already cover what the sequence needs."
                        : $"Forecast: {planned.Sum(s => s.Frames)} frames, {order}. Each frame counts its exposure plus {overhead:0}% overhead.";
                    if (t.IsOpenEnded) note += " Part of this sequence loops until a condition, so the count is one pass.";
                }
                return new FramePlanView {
                    EventId = e.Id, TargetId = e.TargetId, Title = rec?.Name ?? "Unknown target", Color = rec?.Color ?? "#9E9E9E",
                    Night = e.Night, Block = e.Range, Taken = taken, Planned = planned, Note = note
                };
            }
        }

        public PlanEvent? GetEvent(string eventId) {
            lock (sync) return data.Events.FirstOrDefault(e => e.Id == eventId)?.Clone();
        }

        public List<PlanEvent> Events() {
            lock (sync) return data.Events.Select(e => e.Clone()).ToList();
        }

        public List<Milestone> Milestones() {
            lock (sync) return history.Milestones.ToList();
        }

        public List<CaptureRecord> Captures() {
            lock (sync) return captures.ToList();
        }

        // ------------------------------------------------------------------ snapshot for the calendar

        private List<CaptureSession> SessionsLocked() {
            sessionCache ??= SessionBuilder.Build(captures, sky.TimeZone, TimeSpan.FromMinutes(data.Settings.SessionGapMinutes));
            return sessionCache;
        }

        public BoardSnapshot BuildSnapshot(BoardViewMode mode, DateOnly start, int nightCount, string? activeTargetId, string title = "") {
            lock (sync) {
                var now = utcNow();
                var zone = sky.TimeZone;
                var tonight = NightMath.NightOf(now, zone);
                var end = start.AddDays(nightCount);
                bool InRange(DateOnly n) => n >= start && n < end;

                var outcomes = OutcomesLocked(now);
                var nights = new List<NightView>();
                for (var n = start; n < end; n = n.AddDays(1)) {
                    var dark = sky.GetDarkWindow(n, data.Settings.DarkTime);
                    var onNight = data.Events.Where(e => e.Night == n).ToList();
                    nights.Add(new NightView {
                        Night = n,
                        Span = NightMath.Span(n, zone),
                        Dark = dark,
                        MoonIllumination = sky.GetMoonIllumination(n),
                        Planned = TimeRange.Total(onNight.Where(e => e.Outcome != EventOutcome.Missed).Select(e => e.Range)),
                        IsTonight = n == tonight,
                        IsPast = n < tonight,
                        Missed = onNight.Count(e => outcomes[e.Id].Status == EventStatus.Missed),
                        Partial = onNight.Count(e => outcomes[e.Id].Status == EventStatus.Partial),
                        CanSettle = CanSettleLocked(n, now)
                    });
                }

                string ColorOf(string? id) => id != null && data.Targets.TryGetValue(id, out var r) && !string.IsNullOrEmpty(r.Color) ? r.Color : "#9E9E9E";
                string NameOf(string id) => data.Targets.TryGetValue(id, out var r) ? r.Name : "Unknown target";
                string Clock(DateTime utc) => NightMath.ToLocal(utc, zone).ToString("HH:mm", CultureInfo.InvariantCulture);

                var events = data.Events.Where(e => InRange(e.Night)).Select(e => {
                    var p = outcomes[e.Id];
                    string status = p.Status switch {
                        EventStatus.Done => $"Done · {p.Frames} frames",
                        EventStatus.Partial => $"Partly done · {Fmt.DurationSeconds(p.AchievedSeconds)} of {Fmt.Duration(e.IsSettled ? PlannedLength(e) : e.Duration)}",
                        EventStatus.Missed => "Missed: no frames logged",
                        EventStatus.Running => p.Frames > 0 ? $"Running · {p.Frames} frames so far" : "Running",
                        EventStatus.Unknown => "Frame log is off, so the board can't tell if it was done",
                        _ => ""
                    };
                    return new EventView {
                        Id = e.Id,
                        TargetId = e.TargetId,
                        Title = NameOf(e.TargetId),
                        Color = ColorOf(e.TargetId),
                        Night = e.Night,
                        StartUtc = e.StartUtc,
                        EndUtc = e.EndUtc,
                        IsPast = e.EndUtc <= now,
                        TargetMissing = !targetById.ContainsKey(e.TargetId),
                        GroupName = targetById.TryGetValue(e.TargetId, out var et) ? et.GroupName : null,
                        Detail = $"{Clock(e.StartUtc)}–{Clock(e.EndUtc)} · {Fmt.Duration(e.Duration)}"
                                 + (targetById.TryGetValue(e.TargetId, out var gt) && gt.GroupName != null ? $" · {gt.GroupName}" : ""),
                        Forced = e.Forced,
                        OutsideConditions = e.IsSettled ? TimeSpan.Zero : OutsideConditionsLocked(e.TargetId, e.Night, e.Range),
                        Status = p.Status,
                        IsSettled = e.IsSettled,
                        AchievedSeconds = p.AchievedSeconds,
                        Frames = p.Frames,
                        PlannedStartUtc = e.PlannedStartUtc,
                        PlannedEndUtc = e.PlannedEndUtc,
                        StatusText = status
                    };
                }).ToList();

                var sessions = SessionsLocked().Where(s => InRange(s.Night)).Select(s => new SessionView {
                    Key = s.Key,
                    TargetId = s.TargetId,
                    Title = s.TargetId != null ? NameOf(s.TargetId) : s.TargetName,
                    Color = ColorOf(s.TargetId),
                    Night = s.Night,
                    StartUtc = s.StartUtc,
                    EndUtc = s.EndUtc,
                    Frames = s.Frames,
                    Detail = $"Done {Clock(s.StartUtc)}–{Clock(s.EndUtc)} · {s.Frames} frames · {Fmt.DurationSeconds(s.ExposureSeconds)} · {s.Summary}"
                }).ToList();

                var milestones = history.Milestones
                    .Where(m => m.Kind != MilestoneKind.Progress || m.Source == MilestoneSource.SequenceFile)
                    .Select(m => (m, night: NightMath.NightOf(m.TimeUtc, zone)))
                    .Where(x => InRange(x.night))
                    .Select(x => new MilestoneView {
                        Id = x.m.Id,
                        TargetId = x.m.TargetId,
                        TargetName = x.m.TargetName,
                        Color = ColorOf(x.m.TargetId),
                        Night = x.night,
                        TimeUtc = x.m.TimeUtc,
                        Kind = x.m.Kind,
                        Source = x.m.Source,
                        Text = $"{Clock(x.m.TimeUtc)} {x.m.Describe()}"
                    }).ToList();

                var moonAltitude = new Dictionary<DateOnly, List<(DateTime, double)>>();
                if (data.Settings.ShowMoon && mode != BoardViewMode.Month) {
                    var observer = sky.Observer;
                    foreach (var n in nights) {
                        var curve = new List<(DateTime, double)>();
                        for (int i = 0; i < 145; i++) {
                            var t = n.Span.StartUtc + TimeSpan.FromTicks(n.Span.Duration.Ticks * i / 144);
                            curve.Add((t, SkyMath.MoonAltitude(t, observer)));
                        }
                        moonAltitude[n.Night] = curve;
                    }
                }

                var activeWindows = new Dictionary<DateOnly, List<TimeRange>>();
                var activeAltitude = new Dictionary<DateOnly, List<(DateTime, double)>>();
                string activeColor = "";
                if (activeTargetId != null && data.Targets.TryGetValue(activeTargetId, out var activeRec)) {
                    activeColor = activeRec.Color;
                    foreach (var n in nights) {
                        activeWindows[n.Night] = ValidWindowsLocked(activeTargetId, n.Night);
                    }
                    if (mode != BoardViewMode.Month) {
                        var calc = new VisibilityCalculator(sky);
                        foreach (var n in nights) {
                            activeAltitude[n.Night] = calc.AltitudeCurve(activeRec.RaHours, activeRec.DecDegrees, n.Span, 145);
                        }
                    }
                }

                // Time axis: from an hour before the earliest dusk to an hour after the latest dawn, in whole hours.
                double axisStart = 5, axisEnd = 20;
                var darkNights = nights.Where(n => n.Dark != null).ToList();
                if (darkNights.Count > 0) {
                    axisStart = Math.Floor(darkNights.Min(n => NightMath.ClockHoursAfterNoon(n.Dark!.Value.StartUtc, n.Night, zone)) - 1);
                    axisEnd = Math.Ceiling(darkNights.Max(n => NightMath.ClockHoursAfterNoon(n.Dark!.Value.EndUtc, n.Night, zone)) + 1);
                }
                axisStart = Math.Clamp(axisStart, 0, 23);
                axisEnd = Math.Clamp(axisEnd, axisStart + 1, 24);

                return new BoardSnapshot {
                    Mode = mode,
                    Start = start,
                    Nights = nights,
                    Events = events,
                    Sessions = sessions,
                    Milestones = milestones,
                    ActiveTargetId = activeTargetId,
                    ActiveTargetColor = activeColor,
                    TargetColors = data.Targets.ToDictionary(kv => kv.Key, kv => kv.Value.Color),
                    TargetNames = data.Targets.ToDictionary(kv => kv.Key, kv => kv.Value.Name),
                    ActiveWindows = activeWindows,
                    ActiveAltitude = activeAltitude,
                    MoonAltitude = moonAltitude,
                    ShowMoon = data.Settings.ShowMoon,
                    CanUndo = undoStack.Count > 0,
                    UndoLabel = undoStack.Count > 0 ? undoStack[^1].Label : "",
                    Zone = zone,
                    NowUtc = now,
                    AxisStartHours = axisStart,
                    AxisEndHours = axisEnd,
                    Title = title
                };
            }
        }

        private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }
}
