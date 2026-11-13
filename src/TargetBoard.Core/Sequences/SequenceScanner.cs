using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TargetBoard.Core.Models;

namespace TargetBoard.Core.Sequences {

    /// <summary>Finds sequence files in folders and reads every target from them.</summary>
    public static class SequenceScanner {
        private const long MaxFileBytes = 64L * 1024 * 1024;

        public static IEnumerable<string> FindFiles(IEnumerable<string> folders, IEnumerable<string> extraFiles, bool includeSubfolders, IEnumerable<string>? excludeFolders = null) {
            var excluded = (excludeFolders ?? Enumerable.Empty<string>())
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => Path.TrimEndingDirectorySeparator(Path.GetFullPath(f)) + Path.DirectorySeparatorChar)
                .ToList();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var options = new EnumerationOptions {
                RecurseSubdirectories = includeSubfolders,
                IgnoreInaccessible = true,
                MaxRecursionDepth = 6,
                MatchCasing = MatchCasing.CaseInsensitive
            };

            foreach (var folder in folders.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase)) {
                if (!Directory.Exists(folder)) continue;
                IEnumerable<string> files;
                try {
                    files = Directory.EnumerateFiles(folder, "*", options).Where(IsCandidate).ToList();
                } catch (Exception) {
                    continue;
                }
                foreach (var file in files) {
                    var full = Path.GetFullPath(file);
                    if (full.EndsWith(".template.json", StringComparison.OrdinalIgnoreCase)) continue;
                    if (excluded.Any(e => full.StartsWith(e, StringComparison.OrdinalIgnoreCase))) continue;
                    if (seen.Add(full)) yield return full;
                }
            }
            foreach (var file in extraFiles.Where(f => !string.IsNullOrWhiteSpace(f))) {
                if (!File.Exists(file)) continue;
                var full = Path.GetFullPath(file);
                if (seen.Add(full)) yield return full;
            }
        }

        /// <summary>File types the board reads: Advanced Sequencer JSON and Simple Sequencer XML / target sets.</summary>
        public static readonly string[] Extensions = { ".json", ".xml", ".ninaTargetSet" };

        public static bool IsCandidate(string path) {
            var ext = Path.GetExtension(path);
            return Extensions.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase));
        }

        public static (List<SequenceTarget> Targets, ScanReport Report) Scan(IEnumerable<string> folders, IEnumerable<string> extraFiles, bool includeSubfolders, IEnumerable<string>? excludeFolders = null) {
            var report = new ScanReport();
            var folderList = folders.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            report.FoldersScanned.AddRange(folderList);
            var targets = new List<SequenceTarget>();

            foreach (var file in FindFiles(folderList, extraFiles, includeSubfolders, excludeFolders)) {
                try {
                    var info = new FileInfo(file);
                    if (info.Length == 0 || info.Length > MaxFileBytes) continue;
                    bool simple = SimpleSequenceReader.IsSimpleSequenceFile(file);
                    if (!LooksLikeSequence(file, simple)) continue;
                    var found = simple ? SimpleSequenceReader.ReadFile(file) : SequenceReader.ReadFile(file);
                    report.FilesRead++;
                    StampFile(found, file);
                    targets.AddRange(found);
                } catch (Exception ex) {
                    report.Errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }
            report.TargetsFound = targets.Count;
            report.FinishedUtc = DateTime.UtcNow;
            return (targets, report);
        }

        /// <summary>
        /// Adds the file's dates, and links targets that share a file (a target set's panels, or the targets of one
        /// sequence) into a group, in file order.
        /// </summary>
        public static void StampFile(List<SequenceTarget> found, string file) {
            var full = Path.GetFullPath(file);
            var created = File.GetCreationTimeUtc(full);
            var written = File.GetLastWriteTimeUtc(full);
            if (created.Year < 1980) created = written;
            string? groupId = found.Count >= 2 ? StableId.From("group", full.ToLowerInvariant()) : null;
            string? groupName = found.Count >= 2 ? Path.GetFileNameWithoutExtension(full) : null;
            for (int i = 0; i < found.Count; i++) {
                found[i].FileCreatedUtc = created;
                found[i].GroupId = groupId;
                found[i].GroupName = groupName;
                found[i].GroupIndex = i;
            }
        }

        /// <summary>Cheap check on the first few KB so unrelated JSON files are skipped without a full parse.</summary>
        private static bool LooksLikeSequence(string file, bool simple) {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[8192];
            int read = stream.Read(buffer, 0, buffer.Length);
            var head = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
            return simple
                ? head.Contains("<CaptureSequenceList", StringComparison.Ordinal) || head.Contains("<ArrayOfCaptureSequenceList", StringComparison.Ordinal)
                : head.Contains("NINA.Sequencer", StringComparison.Ordinal);
        }
    }
}
