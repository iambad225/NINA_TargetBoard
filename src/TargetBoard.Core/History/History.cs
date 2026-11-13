using System;
using System.Collections.Generic;
using System.Linq;
using TargetBoard.Core.Models;
using TargetBoard.Core.Sky;

namespace TargetBoard.Core.History {

    /// <summary>Matches a saved frame to a target on the board, by name first and then by position.</summary>
    public static class TargetMatcher {
        public static string Normalize(string name) {
            if (string.IsNullOrWhiteSpace(name)) return "";
            var chars = name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray();
            return new string(chars);
        }

        public static double SeparationDeg(double ra1h, double dec1, double ra2h, double dec2) {
            const double d = Math.PI / 180;
            double a1 = ra1h * 15 * d, a2 = ra2h * 15 * d, b1 = dec1 * d, b2 = dec2 * d;
            double cos = Math.Sin(b1) * Math.Sin(b2) + Math.Cos(b1) * Math.Cos(b2) * Math.Cos(a1 - a2);
            return Math.Acos(Math.Clamp(cos, -1, 1)) / d;
        }

        public static SequenceTarget? Match(CaptureRecord capture, IReadOnlyList<SequenceTarget> targets, double maxSeparationDeg = 1.0) {
            var key = Normalize(capture.TargetName);
            bool hasCoords = capture.RaHours is double && capture.DecDegrees is double;
            var byName = key.Length == 0 ? new List<SequenceTarget>() : targets.Where(t => Normalize(t.Name) == key).ToList();

            if (byName.Count == 1) return byName[0];
            if (byName.Count > 1) {
                // The same target saved twice (in a target set and as its own file): the file NINA was running decides.
                var bySource = FromRunningFile(capture, byName);
                if (bySource.Count == 1) return bySource[0];
                if (bySource.Count > 1) byName = bySource;
            }
            if (byName.Count > 1) {
                if (hasCoords) {
                    return byName
                        .OrderBy(t => Math.Round(SeparationDeg(t.RaHours, t.DecDegrees, capture.RaHours!.Value, capture.DecDegrees!.Value), 2))
                        .ThenByDescending(t => t.GroupId != null)
                        .ThenByDescending(t => t.FileWriteUtc)
                        .First();
                }
                return byName.OrderByDescending(t => t.GroupId != null).ThenByDescending(t => t.FileWriteUtc).First();
            }
            if (hasCoords) {
                var nearest = targets
                    .Select(t => (t, sep: SeparationDeg(t.RaHours, t.DecDegrees, capture.RaHours!.Value, capture.DecDegrees!.Value)))
                    .OrderBy(x => x.sep)
                    .FirstOrDefault();
                if (nearest.t != null && nearest.sep <= maxSeparationDeg) {
                    // Copies at the same pointing: again let the running file decide.
                    var same = targets.Where(t => SeparationDeg(t.RaHours, t.DecDegrees, nearest.t.RaHours, nearest.t.DecDegrees) < 1e-6).ToList();
                    var bySource = same.Count > 1 ? FromRunningFile(capture, same) : new List<SequenceTarget>();
                    return bySource.Count == 1 ? bySource[0] : nearest.t;
                }
            }
            return null;
        }

        /// <summary>
        /// The candidates that come from the file NINA was running: by its path when NINA gave one, otherwise by the
        /// sequence title NINA stamped on the frame (a target set's or a .json's file name). Empty when neither says.
        /// When NINA gave a path, the title is not used: the Simple Sequencer keeps the old title when a single .xml is
        /// loaded, so it would point at the wrong file.
        /// </summary>
        public static List<SequenceTarget> FromRunningFile(CaptureRecord capture, IReadOnlyList<SequenceTarget> candidates) {
            if (!string.IsNullOrWhiteSpace(capture.SourceFile)) {
                var file = NormalizePath(capture.SourceFile!);
                return candidates.Where(t => NormalizePath(t.SourceFile) == file).ToList();
            }
            if (!string.IsNullOrWhiteSpace(capture.SequenceTitle)) {
                var title = capture.SequenceTitle!.Trim();
                var hit = candidates.Where(t => string.Equals(System.IO.Path.GetFileNameWithoutExtension(t.SourceFile ?? ""), title, StringComparison.OrdinalIgnoreCase)).ToList();
                if (hit.Count > 0) return hit;
            }
            return new List<SequenceTarget>();
        }

        private static string NormalizePath(string? path) {
            if (string.IsNullOrWhiteSpace(path)) return "";
            try {
                return System.IO.Path.GetFullPath(path.Trim()).TrimEnd('\\', '/').Replace('\\', '/').ToLowerInvariant();
            } catch (Exception) {
                return path.Trim().Replace('\\', '/').ToLowerInvariant();
            }
        }
    }

    /// <summary>A target's lines with frames logged live (after the file was last saved) added on.</summary>
    public sealed class EffectiveProgress {
        public SequenceTarget Target { get; init; } = null!;
        public List<ExposureLine> Lines { get; init; } = new();
        /// <summary>Latest live frame time per line key.</summary>
        public Dictionary<string, DateTime> LastLiveUtc { get; init; } = new();
        public int LiveFrames { get; init; }

        public double PlannedSeconds => Lines.Sum(l => l.Planned * l.ExposureSeconds);
        public double DoneSeconds => Lines.Sum(l => Math.Min(l.Done, l.Planned) * l.ExposureSeconds);
        public double RemainingSeconds => Lines.Sum(l => l.RemainingSeconds);
        public double Progress => PlannedSeconds > 0 ? DoneSeconds / PlannedSeconds : 0;
        public bool IsComplete => !Target.IsOpenEnded && Lines.Any(l => l.Planned > 0) && Lines.Where(l => l.Planned > 0).All(l => l.Done >= l.Planned);

        public static EffectiveProgress Build(SequenceTarget target, IEnumerable<CaptureRecord> capturesForTarget) {
            var lines = target.Lines.Select(l => l.Clone()).ToList();
            var byKey = lines.ToDictionary(l => l.Key);
            var lastLive = new Dictionary<string, DateTime>();
            int live = 0;

            foreach (var c in capturesForTarget) {
                // Frames the saved file already counts are not added twice.
                if (c.TimeUtc <= target.FileWriteUtc) continue;
                var line = FindLine(byKey, lines, c);
                if (line == null) continue;
                line.Done++;
                live++;
                if (!lastLive.TryGetValue(line.Key, out var t) || c.TimeUtc > t) lastLive[line.Key] = c.TimeUtc;
            }
            return new EffectiveProgress { Target = target, Lines = lines, LastLiveUtc = lastLive, LiveFrames = live };
        }

        private static ExposureLine? FindLine(Dictionary<string, ExposureLine> byKey, List<ExposureLine> lines, CaptureRecord c) {
            var filter = string.IsNullOrWhiteSpace(c.Filter) ? Sequences.SequenceReader.NoFilter : c.Filter;
            if (byKey.TryGetValue(ExposureLine.MakeKey(filter, c.ExposureSeconds), out var exact)) return exact;
            // Filter names can differ (no wheel, renamed slot): fall back to the only line with this exposure length.
            var sameExposure = lines.Where(l => Math.Abs(l.ExposureSeconds - c.ExposureSeconds) < 0.01).ToList();
            return sameExposure.Count == 1 ? sameExposure[0] : null;
        }
    }

    /// <summary>
    /// Compares progress with the last stored snapshot and records milestones:
    /// a line reaching its planned count, a whole target finishing, a target reopening,
    /// and progress found in a re-saved file that the live log did not see.
    /// </summary>
    public static class ProgressTracker {

        public static List<Milestone> Update(HistoryData history, IEnumerable<EffectiveProgress> targets) {
            var added = new List<Milestone>();
            foreach (var eff in targets) {
                var target = eff.Target;
                bool firstSeen = !history.Snapshots.TryGetValue(target.Id, out var snap);
                snap ??= new TargetSnapshot();
                var fileTime = target.FileWriteUtc;
                bool fileChanged = !firstSeen && fileTime > snap.FileWriteUtc;
                var fileLines = target.Lines.ToDictionary(l => l.Key);
                var partTimes = new List<DateTime>();
                var newLines = new Dictionary<string, LineSnapshot>();

                foreach (var line in eff.Lines) {
                    snap.Lines.TryGetValue(line.Key, out var prev);
                    int fileDone = fileLines.TryGetValue(line.Key, out var fl) ? fl.Done : 0;
                    DateTime? liveTime = eff.LastLiveUtc.TryGetValue(line.Key, out var lt) ? lt : null;

                    if (fileChanged && prev != null && fileDone > prev.EffectiveDone) {
                        added.Add(Make(MilestoneKind.Progress, MilestoneSource.SequenceFile, fileTime, target, line, fileDone - prev.EffectiveDone, fileDone));
                    }

                    bool wasComplete = prev != null && prev.Planned > 0 && prev.EffectiveDone >= prev.Planned;
                    bool isComplete = line.Planned > 0 && line.Done >= line.Planned;
                    if (isComplete && !wasComplete) {
                        MilestoneSource source;
                        DateTime when;
                        if (firstSeen) {
                            source = MilestoneSource.FirstScan;
                            when = fileTime;
                        } else if (liveTime is DateTime l && l > fileTime) {
                            source = MilestoneSource.LiveCapture;
                            when = l;
                        } else {
                            source = MilestoneSource.SequenceFile;
                            when = fileTime;
                        }
                        added.Add(Make(MilestoneKind.PartComplete, source, when, target, line, 0, line.Done));
                        partTimes.Add(when);
                    }
                    newLines[line.Key] = new LineSnapshot { FileDone = fileDone, EffectiveDone = line.Done, Planned = line.Planned };
                }

                bool complete = eff.IsComplete;
                if (complete && !snap.Complete) {
                    DateTime when = partTimes.Count > 0 ? partTimes.Max() : (eff.LastLiveUtc.Count > 0 ? eff.LastLiveUtc.Values.Max() : fileTime);
                    var source = firstSeen ? MilestoneSource.FirstScan
                        : (eff.LastLiveUtc.Count > 0 && when > fileTime ? MilestoneSource.LiveCapture : MilestoneSource.SequenceFile);
                    var m = Make(MilestoneKind.TargetComplete, source, when, target, null, 0, eff.Lines.Sum(l => l.Done));
                    m.Planned = eff.Lines.Sum(l => l.Planned);
                    added.Add(m);
                    snap.Complete = true;
                    snap.CompletedUtc = when;
                } else if (!complete && snap.Complete) {
                    var m = Make(MilestoneKind.Reopened, MilestoneSource.SequenceFile, fileTime > (snap.CompletedUtc ?? DateTime.MinValue) ? fileTime : DateTime.UtcNow, target, null, 0, eff.Lines.Sum(l => l.Done));
                    m.Planned = eff.Lines.Sum(l => l.Planned);
                    added.Add(m);
                    snap.Complete = false;
                    snap.CompletedUtc = null;
                }

                snap.Lines = newLines;
                snap.FileWriteUtc = fileTime;
                snap.TargetName = target.Name;
                history.Snapshots[target.Id] = snap;
            }

            foreach (var m in added) {
                if (!history.Milestones.Any(x => x.Id == m.Id)) history.Milestones.Add(m);
            }
            history.Milestones.Sort((a, b) => a.TimeUtc.CompareTo(b.TimeUtc));
            return added;
        }

        private static Milestone Make(MilestoneKind kind, MilestoneSource source, DateTime when, SequenceTarget target, ExposureLine? line, int added, int done) {
            return new Milestone {
                Id = StableId.From(target.Id, kind.ToString(), line?.Key ?? "", when.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), done.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                TimeUtc = when,
                Kind = kind,
                Source = source,
                TargetId = target.Id,
                TargetName = target.Name,
                Filter = line?.Filter,
                ExposureSeconds = line?.ExposureSeconds,
                FramesAdded = added,
                Done = done,
                Planned = line?.Planned ?? 0,
                SourceFile = target.SourceFile
            };
        }
    }

    /// <summary>A run of frames on one target in one night: what actually got done.</summary>
    public sealed class CaptureSession {
        public string Key { get; init; } = "";
        public string? TargetId { get; init; }
        public string TargetName { get; init; } = "";
        public DateOnly Night { get; init; }
        public DateTime StartUtc { get; set; }
        public DateTime EndUtc { get; set; }
        public int Frames { get; set; }
        public double ExposureSeconds { get; set; }
        public Dictionary<string, int> FramesByFilter { get; } = new(StringComparer.OrdinalIgnoreCase);

        public TimeRange Range => new(StartUtc, EndUtc);

        public string Summary => string.Join(", ", FramesByFilter.OrderBy(k => k.Key).Select(k => $"{k.Value}× {k.Key}"));
    }

    public static class SessionBuilder {
        public static List<CaptureSession> Build(IEnumerable<CaptureRecord> captures, TimeZoneInfo zone, TimeSpan maxGap) {
            var sessions = new List<CaptureSession>();
            var open = new Dictionary<string, CaptureSession>();
            foreach (var c in captures.OrderBy(c => c.TimeUtc)) {
                string key = c.TargetId ?? ("name:" + TargetMatcher.Normalize(c.TargetName));
                var night = NightMath.NightOf(c.TimeUtc, zone);
                if (!open.TryGetValue(key, out var s) || s.Night != night || c.TimeUtc - s.EndUtc > maxGap) {
                    s = new CaptureSession {
                        Key = StableId.From(key, c.TimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        TargetId = c.TargetId,
                        TargetName = string.IsNullOrWhiteSpace(c.TargetName) ? "Unnamed target" : c.TargetName,
                        Night = night,
                        StartUtc = c.TimeUtc,
                        EndUtc = c.EndUtc
                    };
                    open[key] = s;
                    sessions.Add(s);
                }
                if (c.EndUtc > s.EndUtc) s.EndUtc = c.EndUtc;
                s.Frames++;
                s.ExposureSeconds += c.ExposureSeconds;
                var filter = string.IsNullOrWhiteSpace(c.Filter) ? "No filter" : c.Filter;
                s.FramesByFilter.TryGetValue(filter, out int n);
                s.FramesByFilter[filter] = n + 1;
            }
            return sessions;
        }
    }
}
