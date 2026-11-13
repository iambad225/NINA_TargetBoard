using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TargetBoard.Core.Models;

namespace TargetBoard.Core.Storage {

    /// <summary>
    /// Local files in Documents\N.I.N.A\TargetBoard:
    ///   board.json     settings, target colors and conditions, planned events
    ///   history.json   progress snapshots and completion milestones
    ///   captures.jsonl one line per light frame NINA saved (append-only)
    /// JSON files are written to a temp file first and then swapped in, keeping a .bak of the last good copy.
    /// </summary>
    public sealed class BoardStore {
        public const string FolderName = "TargetBoard";

        private static readonly JsonSerializerOptions Options = CreateOptions(indented: true);
        private static readonly JsonSerializerOptions LineOptions = CreateOptions(indented: false);
        private readonly object fileLock = new();

        public BoardStore(string? folder = null) {
            Folder = folder ?? DefaultFolder;
        }

        /// <summary>%USERPROFILE%\Documents\N.I.N.A\TargetBoard, next to NINA's own Targets and Templates folders.</summary>
        public static string DefaultFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "N.I.N.A", FolderName);

        public string Folder { get; }
        public string BoardPath => Path.Combine(Folder, "board.json");
        public string HistoryPath => Path.Combine(Folder, "history.json");
        public string CapturesPath => Path.Combine(Folder, "captures.jsonl");
        public string UndoPath => Path.Combine(Folder, "undo.json");

        public BoardData LoadBoard() {
            var data = Load<BoardData>(BoardPath) ?? new BoardData();
            data.Settings ??= new BoardSettings();
            data.Targets ??= new Dictionary<string, TargetRecord>();
            data.Events ??= new List<PlanEvent>();
            foreach (var e in data.Events) {
                e.StartUtc = DateTime.SpecifyKind(e.StartUtc, DateTimeKind.Utc);
                e.EndUtc = DateTime.SpecifyKind(e.EndUtc, DateTimeKind.Utc);
            }
            return data;
        }

        public HistoryData LoadHistory() {
            var data = Load<HistoryData>(HistoryPath) ?? new HistoryData();
            data.Snapshots ??= new Dictionary<string, TargetSnapshot>();
            data.Milestones ??= new List<Milestone>();
            return data;
        }

        public void SaveBoard(BoardData data) => Save(BoardPath, data);
        public void SaveHistory(HistoryData data) => Save(HistoryPath, data);

        public UndoData LoadUndo() {
            var data = Load<UndoData>(UndoPath) ?? new UndoData();
            data.Steps ??= new List<UndoStep>();
            foreach (var step in data.Steps) {
                step.Events ??= new List<PlanEvent>();
                foreach (var e in step.Events) {
                    e.StartUtc = DateTime.SpecifyKind(e.StartUtc, DateTimeKind.Utc);
                    e.EndUtc = DateTime.SpecifyKind(e.EndUtc, DateTimeKind.Utc);
                }
            }
            return data;
        }

        public void SaveUndo(UndoData data) => Save(UndoPath, data);

        public List<CaptureRecord> LoadCaptures() {
            var list = new List<CaptureRecord>();
            lock (fileLock) {
                if (!File.Exists(CapturesPath)) return list;
                foreach (var line in File.ReadLines(CapturesPath, Encoding.UTF8)) {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try {
                        var rec = JsonSerializer.Deserialize<CaptureRecord>(line, LineOptions);
                        if (rec == null) continue;
                        rec.TimeUtc = DateTime.SpecifyKind(rec.TimeUtc, DateTimeKind.Utc);
                        list.Add(rec);
                    } catch (JsonException) {
                        // A half-written last line (power cut mid-write) is skipped, not fatal.
                    }
                }
            }
            return list;
        }

        public void AppendCapture(CaptureRecord record) {
            lock (fileLock) {
                Directory.CreateDirectory(Folder);
                var line = JsonSerializer.Serialize(record, LineOptions) + Environment.NewLine;
                File.AppendAllText(CapturesPath, line, Encoding.UTF8);
            }
        }

        /// <summary>Rewrites the capture log, used when target matches are corrected after a rescan.</summary>
        public void RewriteCaptures(IEnumerable<CaptureRecord> records) {
            lock (fileLock) {
                Directory.CreateDirectory(Folder);
                var tmp = CapturesPath + ".tmp";
                using (var writer = new StreamWriter(tmp, false, new UTF8Encoding(false))) {
                    foreach (var r in records) writer.WriteLine(JsonSerializer.Serialize(r, LineOptions));
                }
                Swap(tmp, CapturesPath);
            }
        }

        private T? Load<T>(string path) where T : class {
            lock (fileLock) {
                foreach (var candidate in new[] { path, path + ".bak" }) {
                    if (!File.Exists(candidate)) continue;
                    try {
                        var json = File.ReadAllText(candidate, Encoding.UTF8);
                        var value = JsonSerializer.Deserialize<T>(json, Options);
                        if (value != null) return value;
                    } catch (Exception) {
                        // Keep the unreadable file for inspection and try the backup.
                        TryCopy(candidate, candidate + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                    }
                }
                return null;
            }
        }

        private void Save<T>(string path, T value) {
            lock (fileLock) {
                Directory.CreateDirectory(Folder);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options), new UTF8Encoding(false));
                Swap(tmp, path);
            }
        }

        /// <summary>Swaps the new file in. Retries briefly, since antivirus or OneDrive can hold a file for a moment.</summary>
        private static void Swap(string tmp, string path) {
            for (int attempt = 1; ; attempt++) {
                try {
                    if (File.Exists(path)) {
                        File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
                    } else {
                        File.Move(tmp, path);
                    }
                    return;
                } catch (IOException) when (attempt < 4) {
                    System.Threading.Thread.Sleep(150 * attempt);
                } catch (UnauthorizedAccessException) when (attempt < 4) {
                    System.Threading.Thread.Sleep(150 * attempt);
                }
            }
        }

        private static void TryCopy(string from, string to) {
            try { File.Copy(from, to, overwrite: false); } catch (Exception) { /* best effort */ }
        }

        private static JsonSerializerOptions CreateOptions(bool indented) {
            var o = new JsonSerializerOptions {
                WriteIndented = indented,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };
            o.Converters.Add(new JsonStringEnumConverter());
            return o;
        }
    }
}
