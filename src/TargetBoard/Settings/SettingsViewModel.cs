using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using TargetBoard.Core;
using TargetBoard.Core.Models;
using TargetBoard.Util;

namespace TargetBoard.Settings {

    /// <summary>Board settings. Every change is saved to board.json straight away.</summary>
    public sealed class SettingsViewModel : ObservableBase {
        private readonly BoardContext context;

        public SettingsViewModel(BoardContext context) {
            this.context = context;
            ExtraFolders = new ObservableCollection<string>(Current.ExtraFolders);
            ExtraFiles = new ObservableCollection<string>(Current.ExtraFiles);

            AddFolderCommand = new BoardCommand(_ => AddFolder());
            RemoveFolderCommand = new BoardCommand(p => { if (p is string f) Change(s => s.ExtraFolders.Remove(f), rescan: true); });
            AddFileCommand = new BoardCommand(_ => AddFile());
            RemoveFileCommand = new BoardCommand(p => { if (p is string f) Change(s => s.ExtraFiles.Remove(f), rescan: true); });
            OpenDataFolderCommand = new BoardCommand(_ => OpenFolder(context.Store.Folder));
            RescanCommand = new BoardCommand(_ => _ = context.RescanAsync());

            context.Engine.Changed += (_, _) => Application.Current?.Dispatcher.BeginInvoke(new Action(() => Safe.Run("settings refresh", Refresh)));
            context.ScanStateChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(new Action(() => Safe.Run("scan status", () => Raise(nameof(ScanSummary)))));
        }

        private BoardSettings Current => context.Engine.Settings;

        private void Change(Action<BoardSettings> change, bool rescan = false) {
            context.Engine.UpdateSettings(change);
            if (rescan) context.RequestRescan(TimeSpan.FromMilliseconds(300));
        }

        private void Refresh() {
            var s = Current;
            Sync(ExtraFolders, s.ExtraFolders);
            Sync(ExtraFiles, s.ExtraFiles);
            Raise(string.Empty);
        }

        private static void Sync(ObservableCollection<string> target, List<string> source) {
            if (target.SequenceEqual(source)) return;
            target.Clear();
            foreach (var x in source) target.Add(x);
        }

        // ------------------------------------------------------------------ folders

        public bool UseProfileFolders {
            get => Current.UseProfileFolders;
            set => Change(s => s.UseProfileFolders = value, rescan: true);
        }

        public string ProfileFoldersText => string.Join(Environment.NewLine, context.ProfileFolders());

        public bool IncludeSubfolders {
            get => Current.IncludeSubfolders;
            set => Change(s => s.IncludeSubfolders = value, rescan: true);
        }

        public ObservableCollection<string> ExtraFolders { get; }
        public ObservableCollection<string> ExtraFiles { get; }

        public ICommand AddFolderCommand { get; }
        public ICommand RemoveFolderCommand { get; }
        public ICommand AddFileCommand { get; }
        public ICommand RemoveFileCommand { get; }
        public ICommand OpenDataFolderCommand { get; }
        public ICommand RescanCommand { get; }

        private void AddFolder() {
            var dialog = new OpenFolderDialog { Title = "Folder with NINA sequence files" };
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName)) {
                var folder = dialog.FolderName;
                Change(s => { if (!s.ExtraFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) s.ExtraFolders.Add(folder); }, rescan: true);
            }
        }

        private void AddFile() {
            var dialog = new OpenFileDialog { Title = "NINA sequence or target file", Filter = "NINA sequences and target sets (*.json;*.xml;*.ninaTargetSet)|*.json;*.xml;*.ninaTargetSet|All files (*.*)|*.*", Multiselect = true };
            if (dialog.ShowDialog() == true) {
                var files = dialog.FileNames;
                Change(s => {
                    foreach (var f in files) if (!s.ExtraFiles.Contains(f, StringComparer.OrdinalIgnoreCase)) s.ExtraFiles.Add(f);
                }, rescan: true);
            }
        }

        public static void OpenFolder(string folder) {
            try {
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            } catch (Exception) {
                // Nothing useful to do if Explorer cannot start.
            }
        }

        // ------------------------------------------------------------------ planning rules

        public IReadOnlyList<DarkTimeMode> DarkTimeModes { get; } = new[] { DarkTimeMode.Astronomical, DarkTimeMode.Nautical };

        public DarkTimeMode DarkTime {
            get => Current.DarkTime;
            set => Change(s => s.DarkTime = value);
        }

        public double OverheadPercent {
            get => Current.OverheadPercent;
            set => Change(s => s.OverheadPercent = value);
        }

        public int SnapMinutes {
            get => Current.SnapMinutes;
            set => Change(s => s.SnapMinutes = value);
        }

        public int MinimumEventMinutes {
            get => Current.MinimumEventMinutes;
            set => Change(s => s.MinimumEventMinutes = value);
        }

        public bool AllowOverlap {
            get => Current.AllowOverlap;
            set => Change(s => s.AllowOverlap = value);
        }

        public double DefaultMinAltitude {
            get => Current.DefaultMinAltitude;
            set => Change(s => s.DefaultMinAltitude = Math.Clamp(value, 0, 89));
        }

        public bool DefaultAboveHorizon {
            get => Current.DefaultAboveHorizon;
            set => Change(s => s.DefaultAboveHorizon = value);
        }

        public bool ImportSequenceConditions {
            get => Current.ImportSequenceConditions;
            set => Change(s => s.ImportSequenceConditions = value);
        }

        // ------------------------------------------------------------------ target list

        /// <summary>1 = Windows' usual mouse wheel step; smaller is slower.</summary>
        public double ListScrollSpeed {
            get => Current.ListScrollSpeed;
            set {
                var v = Math.Round(Math.Clamp(value, 0.25, 3), 2);
                if (Math.Abs(v - Current.ListScrollSpeed) < 0.001) return;
                Change(s => s.ListScrollSpeed = v);
                Raise(nameof(ListScrollSpeedText));
            }
        }

        public string ListScrollSpeedText => $"{Current.ListScrollSpeed:0.##}×";

        public bool SmoothScrolling {
            get => Current.SmoothScrolling;
            set => Change(s => s.SmoothScrolling = value);
        }

        // ------------------------------------------------------------------ settling each morning

        public sealed record TimeOption(int Minutes, string Label);

        /// <summary>04:00 to 14:00 in half hours. Settling never happens before dawn, whatever is chosen.</summary>
        public IReadOnlyList<TimeOption> SettleTimes { get; } = Enumerable.Range(8, 21)
            .Select(i => new TimeOption(i * 30, $"{i * 30 / 60:00}:{i * 30 % 60:00}")).ToList();

        public bool AutoSettle {
            get => Current.AutoSettle;
            set => Change(s => s.AutoSettle = value);
        }

        public TimeOption SettleAt {
            get => SettleTimes.FirstOrDefault(t => t.Minutes == Current.SettleAtMinutes) ?? SettleTimes.First(t => t.Minutes == 8 * 60);
            set { if (value != null) Change(s => s.SettleAtMinutes = value.Minutes); }
        }

        // ------------------------------------------------------------------ history

        public bool LogCaptures {
            get => Current.LogCaptures;
            set => Change(s => s.LogCaptures = value);
        }

        public int SessionGapMinutes {
            get => Current.SessionGapMinutes;
            set => Change(s => s.SessionGapMinutes = value);
        }

        public string DataFolder => context.Store.Folder;

        public string ScanSummary {
            get {
                if (context.IsScanning) return "Reading sequence files…";
                var r = context.Engine.LastScan;
                if (r == null) return "Not scanned yet.";
                var local = r.FinishedUtc.ToLocalTime();
                var text = $"{r.TargetsFound} targets in {r.FilesRead} files · read at {local:HH:mm}";
                if (r.Errors.Count > 0) text += $" · {r.Errors.Count} file(s) could not be read";
                return text;
            }
        }

        public string ScanErrors => string.Join(Environment.NewLine, context.Engine.LastScan?.Errors ?? new List<string>());
    }
}
