using NINA.Core.Enum;
using NINA.Core.Utility;
using NINA.Image.ImageData;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.Mediator;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TargetBoard.Core.Engine;
using TargetBoard.Core.Models;
using TargetBoard.Core.Sequences;
using TargetBoard.Core.Storage;
using TargetBoard.Nina;

namespace TargetBoard {

    /// <summary>
    /// One shared board per NINA session. The plugin manifest creates it at startup (so frames are logged even
    /// while the panel is closed); the dock panel and the options page use the same instance.
    /// </summary>
    public sealed class BoardContext : IDisposable {
        private static readonly object Gate = new();
        private static BoardContext? instance;

        private readonly IProfileService profileService;
        private readonly IImageSaveMediator imageSaveMediator;
        private readonly ISequenceMediator? sequenceMediator;
        private readonly List<FileSystemWatcher> watchers = new();
        private readonly Timer rescanTimer;
        /// <summary>Checks every few minutes whether last night is due to be settled (at the settle time, default 08:00).</summary>
        private readonly Timer settleTimer;
        private readonly SemaphoreSlim scanLock = new(1, 1);
        private int disposed;

        public static BoardContext Get(IProfileService profileService, IImageSaveMediator imageSaveMediator, ISequenceMediator? sequenceMediator) {
            lock (Gate) {
                instance ??= new BoardContext(profileService, imageSaveMediator, sequenceMediator);
                return instance;
            }
        }

        private BoardContext(IProfileService profileService, IImageSaveMediator imageSaveMediator, ISequenceMediator? sequenceMediator) {
            this.profileService = profileService;
            this.imageSaveMediator = imageSaveMediator;
            this.sequenceMediator = sequenceMediator;
            Store = new BoardStore();
            Engine = new BoardEngine(Store, new NinaSkyContext(profileService));
            rescanTimer = new Timer(_ => _ = RescanAsync(), null, Timeout.Infinite, Timeout.Infinite);
            settleTimer = new Timer(_ => SettleIfDue(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));

            profileService.LocationChanged += OnSkyChanged;
            profileService.HorizonChanged += OnSkyChanged;
            profileService.ProfileChanged += OnProfileChanged;
            imageSaveMediator.ImageSaved += OnImageSaved;

            Task.Run(() => {
                try {
                    Engine.Load();
                } catch (Exception ex) {
                    Logger.Error("Target Board: could not load its data files", ex);
                }
                return RescanAsync();
            });
        }

        public BoardStore Store { get; }
        public BoardEngine Engine { get; }
        public bool IsScanning { get; private set; }

        /// <summary>Raised when a scan starts or ends.</summary>
        public event EventHandler? ScanStateChanged;

        public IEnumerable<string> ProfileFolders() {
            var s = profileService.ActiveProfile.SequenceSettings;
            return new[] { s.DefaultSequenceFolder, s.SequencerTargetsFolder }.Where(f => !string.IsNullOrWhiteSpace(f));
        }

        /// <summary>Asks for a rescan a few seconds from now; repeated calls fold into one.</summary>
        public void RequestRescan(TimeSpan? delay = null) {
            if (disposed != 0) return;
            rescanTimer.Change(delay ?? TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
        }

        public async Task RescanAsync() {
            if (disposed != 0) return;
            if (!await scanLock.WaitAsync(0)) {
                RequestRescan();
                return;
            }
            try {
                IsScanning = true;
                ScanStateChanged?.Invoke(this, EventArgs.Empty);
                await Task.Run(() => {
                    var settings = Engine.Settings;
                    var folders = Engine.FoldersToScan(ProfileFolders());
                    var (found, report) = SequenceScanner.Scan(folders, settings.ExtraFiles, settings.IncludeSubfolders, new[] { Store.Folder });
                    foreach (var error in report.Errors) Logger.Warning($"Target Board could not read {error}");
                    Engine.ApplyScan(found, report);
                    ResetWatchers(folders, settings);
                });
                SettleIfDue();
            } catch (Exception ex) {
                Logger.Error("Target Board: scan failed", ex);
            } finally {
                IsScanning = false;
                scanLock.Release();
                ScanStateChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void SettleIfDue() {
            if (disposed != 0) return;
            try {
                var report = Engine.AutoSettleIfDue();
                if (report != null) Logger.Info($"Target Board: {report}");
            } catch (Exception ex) {
                Logger.Error("Target Board: settling last night failed", ex);
            }
        }

        // ------------------------------------------------------------------ watching sequence folders

        private void ResetWatchers(List<string> folders, BoardSettings settings) {
            lock (watchers) {
                foreach (var w in watchers) w.Dispose();
                watchers.Clear();
                var dirs = folders.Concat(settings.ExtraFiles.Select(Path.GetDirectoryName).Where(d => d != null).Select(d => d!))
                                  .Where(Directory.Exists)
                                  .Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var dir in dirs) {
                    try {
                        var w = new FileSystemWatcher(dir) {
                            IncludeSubdirectories = settings.IncludeSubfolders,
                            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
                        };
                        foreach (var ext in SequenceScanner.Extensions) w.Filters.Add("*" + ext);
                        w.Changed += OnFileEvent;
                        w.Created += OnFileEvent;
                        w.Deleted += OnFileEvent;
                        w.Renamed += OnFileEvent;
                        w.EnableRaisingEvents = true;
                        watchers.Add(w);
                    } catch (Exception ex) {
                        Logger.Warning($"Target Board cannot watch {dir}: {ex.Message}");
                    }
                }
            }
        }

        private void OnFileEvent(object sender, FileSystemEventArgs e) {
            // Our own files live under Documents\N.I.N.A too; ignore them.
            if (e.FullPath.StartsWith(Store.Folder, StringComparison.OrdinalIgnoreCase)) return;
            if (!SequenceScanner.IsCandidate(e.FullPath)) return;
            RequestRescan();
        }

        // ------------------------------------------------------------------ NINA events

        private void OnSkyChanged(object? sender, EventArgs e) {
            Engine.Sky = new NinaSkyContext(profileService);
        }

        private void OnProfileChanged(object? sender, EventArgs e) {
            Engine.Sky = new NinaSkyContext(profileService);
            RequestRescan(TimeSpan.FromSeconds(1));
        }

        /// <summary>Logs each saved light frame. Only reads what NINA reports; nothing is sent back.</summary>
        private void OnImageSaved(object? sender, ImageSavedEventArgs e) {
            try {
                var md = e.MetaData;
                if (md == null) return;
                var imageType = md.Image?.ImageType ?? "";
                if (!imageType.Equals("LIGHT", StringComparison.OrdinalIgnoreCase)) return;

                double exposure = md.Image != null && !double.IsNaN(md.Image.ExposureTime) ? md.Image.ExposureTime : e.Duration;
                var start = md.Image?.ExposureStart ?? DateTime.MinValue;
                DateTime startUtc = start == DateTime.MinValue
                    ? DateTime.UtcNow.AddSeconds(-Math.Max(0, exposure))
                    : start.Kind switch {
                        DateTimeKind.Utc => start,
                        DateTimeKind.Local => start.ToUniversalTime(),
                        _ => DateTime.SpecifyKind(start, DateTimeKind.Local).ToUniversalTime()
                    };

                var filter = md.FilterWheel?.Filter;
                if (string.IsNullOrWhiteSpace(filter)) filter = e.Filter ?? "";
                var coords = md.Target?.Coordinates;

                var record = new CaptureRecord {
                    TimeUtc = startUtc,
                    ExposureSeconds = exposure,
                    TargetName = md.Target?.Name ?? "",
                    RaHours = coords?.RA,
                    DecDegrees = coords?.Dec,
                    Filter = filter,
                    Gain = md.Camera?.Gain ?? -1,
                    Binning = md.Camera?.Binning ?? "",
                    ImagePath = e.PathToImage?.LocalPath,
                    SequenceTitle = NullIfBlank(md.Sequence?.Title),
                    SourceFile = RunningFile(md.Target?.Name ?? "")
                };
                Task.Run(() => {
                    try {
                        Engine.RecordCapture(record);
                    } catch (Exception ex) {
                        Logger.Error("Target Board: could not log a frame", ex);
                    }
                });
            } catch (Exception ex) {
                Logger.Error("Target Board: could not read frame metadata", ex);
            }
        }

        /// <summary>
        /// The file NINA is running, when it can say: the Advanced Sequencer's .json, or the Simple Sequencer target's
        /// own .xml. Null for a target that came from a target set (NINA keeps no file name for those), or when unsure.
        /// Read the moment the frame is saved, so it matches what NINA was doing; only reads, never changes anything.
        /// </summary>
        private string? RunningFile(string targetName) {
            try {
                var mediator = sequenceMediator;
                if (mediator == null || !mediator.Initialized) return null;
                if (mediator.IsAdvancedSequenceRunning()) return NullIfBlank(mediator.GetAdvancedSequencerSavePath());

                // Simple Sequencer: the target with this name. If more than one has it, the one running.
                var key = TargetBoard.Core.History.TargetMatcher.Normalize(targetName);
                if (key.Length == 0) return null;
                var same = (mediator.GetAllTargetsInSimpleSequence() ?? new List<IDeepSkyObjectContainer>())
                    .Where(c => c != null && TargetBoard.Core.History.TargetMatcher.Normalize(c.Target?.TargetName ?? "") == key)
                    .ToList();
                var pick = same.Count == 1 ? same[0] : same.FirstOrDefault(c => c.Status == SequenceEntityStatus.RUNNING);
                return NullIfBlank((pick as ISimpleDSOContainer)?.FileName);
            } catch (Exception ex) {
                Logger.Debug($"Target Board: could not tell which file NINA is running: {ex.Message}");
                return null;
            }
        }

        private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        public void Dispose() {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            profileService.LocationChanged -= OnSkyChanged;
            profileService.HorizonChanged -= OnSkyChanged;
            profileService.ProfileChanged -= OnProfileChanged;
            imageSaveMediator.ImageSaved -= OnImageSaved;
            rescanTimer.Dispose();
            settleTimer.Dispose();
            lock (watchers) {
                foreach (var w in watchers) w.Dispose();
                watchers.Clear();
            }
            lock (Gate) {
                if (ReferenceEquals(instance, this)) instance = null;
            }
        }
    }
}
