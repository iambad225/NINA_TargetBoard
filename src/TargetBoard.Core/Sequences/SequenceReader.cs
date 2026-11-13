using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using TargetBoard.Core.Models;

namespace TargetBoard.Core.Sequences {

    /// <summary>
    /// Reads targets and progress out of NINA sequence files (Advanced Sequencer JSON).
    /// Files are opened read-only with sharing, so NINA can keep writing them. Nothing is ever written back.
    ///
    /// What it reads:
    /// - Any object with Target.InputCoordinates is a target (DeepSkyObjectContainer and look-alikes from plugins).
    /// - LoopCondition Iterations / CompletedIterations on the target and on containers inside it give planned and done counts.
    ///   Nested loops multiply. NINA 3.3 also writes IterationsExpression; the plain number is read first.
    /// - Light exposures are any item with ExposureTime and ImageType (TakeExposure, TakeSubframeExposure, plugin items).
    ///   The filter is the last SwitchFilter met before the exposure inside the same target.
    /// - ExposureInfoList on the target is NINA's own count of light frames taken per filter.
    /// - AltitudeCondition, AboveHorizonCondition, WaitForAltitude and WaitUntilAboveHorizon become imported conditions.
    /// </summary>
    public static class SequenceReader {
        public const string NoFilter = "No filter";

        private static readonly JsonDocumentOptions DocOptions = new() {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 1024
        };

        // Conditions that keep a container looping until something outside a count happens.
        private static readonly HashSet<string> OpenEndedConditions = new(StringComparer.Ordinal) {
            "TimeCondition", "TimeSpanCondition", "AltitudeCondition", "AboveHorizonCondition",
            "SunAltitudeCondition", "MoonAltitudeCondition", "MoonIlluminationCondition", "SafetyMonitorCondition"
        };

        public static List<SequenceTarget> ReadFile(string path) {
            string json;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true)) {
                json = reader.ReadToEnd();
            }
            var writeUtc = File.GetLastWriteTimeUtc(path);
            return ReadJson(json, Path.GetFullPath(path), writeUtc);
        }

        public static List<SequenceTarget> ReadJson(string json, string sourcePath, DateTime fileWriteUtc) {
            using var doc = JsonDocument.Parse(json, DocOptions);
            var walker = new Walker(sourcePath, fileWriteUtc);
            walker.Visit(doc.RootElement, null, Array.Empty<LoopFrame>());
            return walker.Finish();
        }

        private readonly record struct LoopFrame(int Iterations, int Completed);

        private sealed class TargetBuilder {
            public SequenceTarget Target = new();
            public Dictionary<string, ExposureLine> Lines = new();
            /// <summary>Lines in the order the sequence reaches them, which is the order it shoots them.</summary>
            public List<ExposureLine> Ordered = new();
            public string? CurrentFilter;
        }

        private sealed class Walker {
            private readonly string source;
            private readonly DateTime writeUtc;
            private readonly List<TargetBuilder> builders = new();

            public Walker(string source, DateTime writeUtc) {
                this.source = source;
                this.writeUtc = writeUtc;
            }

            public void Visit(JsonElement node, TargetBuilder? target, LoopFrame[] loops) {
                if (node.ValueKind != JsonValueKind.Object) return;
                if (node.TryGetProperty("$ref", out _)) return;

                string type = ShortType(node);

                if (TryStartTarget(node, out var started)) {
                    target = started;
                    loops = Array.Empty<LoopFrame>();
                }

                // Conditions on this container: loop counts, open-ended loops, altitude rules.
                bool hasCountLoop = false, hasOtherLoop = false;
                LoopFrame? loop = null;
                foreach (var cond in Values(node, "Conditions")) {
                    string ctype = ShortType(cond);
                    if (ctype == "LoopCondition") {
                        int iterations = ReadInt(cond, "Iterations") ?? ReadExpressionInt(cond, "IterationsExpression") ?? 1;
                        int completed = ReadInt(cond, "CompletedIterations") ?? 0;
                        var frame = new LoopFrame(Math.Max(0, iterations), Math.Max(0, completed));
                        // Several loop conditions on one container: the smallest count stops it first.
                        if (loop == null || frame.Iterations < loop.Value.Iterations) loop = frame;
                        hasCountLoop = true;
                    } else if (ctype.Length > 0) {
                        if (OpenEndedConditions.Contains(ctype)) hasOtherLoop = true;
                        if (target != null) ImportCondition(cond, ctype, target);
                    }
                }
                if (loop != null) loops = loops.Append(loop.Value).ToArray();
                if (target != null && hasOtherLoop && !hasCountLoop) target.Target.IsOpenEnded = true;

                if (target != null) {
                    switch (type) {
                        case "SwitchFilter":
                            var name = ReadFilterName(node);
                            if (name != null) target.CurrentFilter = name;
                            break;
                        case "WaitForAltitude":
                        case "WaitUntilAboveHorizon":
                            ImportCondition(node, type, target);
                            break;
                    }
                    if (IsLightExposure(node, out double exposure)) AddExposure(node, target, loops, exposure);
                }

                foreach (var child in Values(node, "Items")) {
                    Visit(child, target, loops);
                }
            }

            private bool TryStartTarget(JsonElement node, out TargetBuilder builder) {
                builder = null!;
                if (!node.TryGetProperty("Target", out var t) || t.ValueKind != JsonValueKind.Object) return false;
                if (!t.TryGetProperty("InputCoordinates", out var c) || c.ValueKind != JsonValueKind.Object) return false;

                double ra = (ReadDouble(c, "RAHours") ?? 0) + (ReadDouble(c, "RAMinutes") ?? 0) / 60.0 + (ReadDouble(c, "RASeconds") ?? 0) / 3600.0;
                double decDeg = ReadDouble(c, "DecDegrees") ?? 0;
                bool negative = ReadBool(c, "NegativeDec") ?? false;
                double dec = Math.Abs(decDeg) + (ReadDouble(c, "DecMinutes") ?? 0) / 60.0 + (ReadDouble(c, "DecSeconds") ?? 0) / 3600.0;
                if (negative || decDeg < 0) dec = -dec;

                string targetName = ReadString(t, "TargetName")?.Trim() ?? "";
                string containerName = ReadString(node, "Name")?.Trim() ?? "";
                string name = targetName.Length > 0 ? targetName : containerName;

                // Empty placeholder containers (no name, no coordinates) are templates, not targets.
                if (name.Length == 0 && ra == 0 && dec == 0) return false;
                if (name.Length == 0) name = Path.GetFileNameWithoutExtension(source);

                builder = new TargetBuilder();
                builder.Target.Name = name;
                builder.Target.ContainerName = containerName;
                builder.Target.RaHours = ra;
                builder.Target.DecDegrees = dec;
                builder.Target.PositionAngle = ReadDouble(t, "PositionAngle") ?? ReadDouble(t, "Rotation") ?? 0;
                builder.Target.SourceFile = source;
                builder.Target.FileWriteUtc = writeUtc;

                foreach (var info in Values(node, "ExposureInfoList")) {
                    var imageType = ReadString(info, "ImageType");
                    if (imageType != null && !imageType.Equals("LIGHT", StringComparison.OrdinalIgnoreCase)) continue;
                    builder.Target.Captured.Add(new CapturedLine {
                        Filter = ReadString(info, "Filter") ?? NoFilter,
                        ExposureSeconds = ReadDouble(info, "ExposureTime") ?? 0,
                        Count = ReadInt(info, "Count") ?? 0,
                        Gain = ReadInt(info, "Gain") ?? -1,
                        Binning = $"{ReadInt(info, "BinningX") ?? 1}x{ReadInt(info, "BinningY") ?? 1}"
                    });
                }

                builders.Add(builder);
                return true;
            }

            private static void AddExposure(JsonElement node, TargetBuilder target, LoopFrame[] loops, double exposure) {
                int planned, done;
                if (loops.Length == 0) {
                    planned = 1;
                    done = Math.Min(1, ReadInt(node, "ExposureCount") ?? 0);
                } else {
                    // Mixed-radix count: outer loops count whole passes of the inner ones.
                    long p = 1, d = 0;
                    foreach (var l in loops) {
                        p *= l.Iterations;
                        d = d * l.Iterations + Math.Min(l.Completed, l.Iterations);
                    }
                    planned = (int)Math.Min(int.MaxValue, p);
                    done = (int)Math.Min(planned, d);
                }

                string filter = target.CurrentFilter ?? NoFilter;
                string key = ExposureLine.MakeKey(filter, exposure);
                if (!target.Lines.TryGetValue(key, out var line)) {
                    line = new ExposureLine { Filter = filter, ExposureSeconds = exposure };
                    target.Lines[key] = line;
                    target.Ordered.Add(line);
                }
                line.Planned += planned;
                line.Done += done;
            }

            private static void ImportCondition(JsonElement node, string type, TargetBuilder target) {
                double? offset = node.TryGetProperty("Data", out var data) && data.ValueKind == JsonValueKind.Object
                    ? ReadDouble(data, "Offset")
                    : null;
                offset ??= ReadDouble(node, "Offset") ?? ReadDouble(node, "Altitude");

                ConditionSpec? spec = null;
                switch (type) {
                    case "AltitudeCondition":
                        if (offset != null) spec = ConditionSpec.MinAltitude(offset.Value, ConditionSource.Sequence);
                        break;
                    case "WaitForAltitude":
                        var dir = ReadString(node, "AboveOrBelow");
                        if (offset != null && (dir == null || dir.Contains('>'))) spec = ConditionSpec.MinAltitude(offset.Value, ConditionSource.Sequence);
                        break;
                    case "AboveHorizonCondition":
                    case "WaitUntilAboveHorizon":
                        spec = ConditionSpec.AboveHorizon(offset ?? 0, ConditionSource.Sequence);
                        break;
                }
                if (spec == null) return;
                var list = target.Target.ImportedConditions;
                if (!list.Any(c => c.Kind == spec.Kind && Math.Abs(c.Value - spec.Value) < 1e-9)) list.Add(spec);
            }

            private static bool IsLightExposure(JsonElement node, out double exposure) {
                exposure = 0;
                double? time = ReadDouble(node, "ExposureTime") ?? ReadExpressionDouble(node, "ExposureTimeExpression");
                if (time == null) return false;
                if (!node.TryGetProperty("ImageType", out var it)) return false;
                var imageType = it.ValueKind == JsonValueKind.String ? it.GetString() : null;
                if (imageType != null && !imageType.Equals("LIGHT", StringComparison.OrdinalIgnoreCase)) return false;
                exposure = time.Value;
                return exposure > 0;
            }

            public List<SequenceTarget> Finish() {
                var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var b in builders) {
                    b.Target.Lines = b.Ordered.ToList();
                    seen.TryGetValue(b.Target.Name, out int n);
                    seen[b.Target.Name] = n + 1;
                    // Identity = file + target name + which occurrence of that name. Survives reordering and edits to exposures.
                    b.Target.Id = StableId.From(source.ToLowerInvariant(), b.Target.Name.ToLowerInvariant(), n.ToString(CultureInfo.InvariantCulture));
                }
                return builders.Select(b => b.Target).ToList();
            }
        }

        // ---------- JSON helpers ----------

        private static string ShortType(JsonElement node) {
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty("$type", out var t) || t.ValueKind != JsonValueKind.String) return "";
            var full = t.GetString() ?? "";
            int comma = full.IndexOf(',');
            if (comma >= 0) full = full.Substring(0, comma);
            int dot = full.LastIndexOf('.');
            return dot >= 0 ? full.Substring(dot + 1) : full;
        }

        /// <summary>Items of a collection property, whether written as a plain array or as {"$values": [...]}.</summary>
        private static IEnumerable<JsonElement> Values(JsonElement node, string property) {
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(property, out var v)) yield break;
            if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("$values", out var inner)) v = inner;
            if (v.ValueKind != JsonValueKind.Array) yield break;
            foreach (var item in v.EnumerateArray()) yield return item;
        }

        private static string? ReadFilterName(JsonElement node) {
            if (!node.TryGetProperty("Filter", out var f) || f.ValueKind != JsonValueKind.Object) return null;
            var name = ReadString(f, "_name") ?? ReadString(f, "Name");
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }

        private static string? ReadString(JsonElement node, string property)
            => node.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static bool? ReadBool(JsonElement node, string property) {
            if (!node.TryGetProperty(property, out var v)) return null;
            return v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
        }

        private static double? ReadDouble(JsonElement node, string property) {
            if (!node.TryGetProperty(property, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
            if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return null;
        }

        private static int? ReadInt(JsonElement node, string property) {
            var d = ReadDouble(node, property);
            return d == null ? null : (int)Math.Round(d.Value);
        }

        /// <summary>NINA 3.3 expressions: {"Definition": "20", ...}. Only plain numbers are used.</summary>
        private static double? ReadExpressionDouble(JsonElement node, string property) {
            if (!node.TryGetProperty(property, out var e) || e.ValueKind != JsonValueKind.Object) return null;
            return ReadDouble(e, "Definition") ?? ReadDouble(e, "Value");
        }

        private static int? ReadExpressionInt(JsonElement node, string property) {
            var d = ReadExpressionDouble(node, property);
            return d == null ? null : (int)Math.Round(d.Value);
        }
    }
}
