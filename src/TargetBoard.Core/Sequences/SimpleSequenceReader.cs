using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using TargetBoard.Core.Models;

namespace TargetBoard.Core.Sequences {

    /// <summary>
    /// Reads NINA's Simple Sequencer files, read-only:
    ///   *.xml            one target   (root element CaptureSequenceList)
    ///   *.ninaTargetSet  many targets (root element ArrayOfCaptureSequenceList)
    /// Each CaptureSequence row gives a filter, an exposure time, TotalExposureCount (planned)
    /// and ProgressExposureCount (done). Disabled rows and non-light frames are skipped.
    /// </summary>
    public static class SimpleSequenceReader {

        public static bool IsSimpleSequenceFile(string path) {
            var ext = Path.GetExtension(path);
            return ext.Equals(".xml", StringComparison.OrdinalIgnoreCase) || ext.Equals(".ninaTargetSet", StringComparison.OrdinalIgnoreCase);
        }

        public static List<SequenceTarget> ReadFile(string path) {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return ReadStream(stream, Path.GetFullPath(path), File.GetLastWriteTimeUtc(path));
        }

        public static List<SequenceTarget> ReadXml(string xml, string sourcePath, DateTime fileWriteUtc) {
            using var reader = new StringReader(xml);
            using var x = XmlReader.Create(reader, Settings);
            return Read(XDocument.Load(x), sourcePath, fileWriteUtc);
        }

        private static readonly XmlReaderSettings Settings = new() {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true
        };

        private static List<SequenceTarget> ReadStream(Stream stream, string sourcePath, DateTime fileWriteUtc) {
            using var x = XmlReader.Create(stream, Settings);
            return Read(XDocument.Load(x), sourcePath, fileWriteUtc);
        }

        private static List<SequenceTarget> Read(XDocument doc, string source, DateTime writeUtc) {
            var root = doc.Root;
            var result = new List<SequenceTarget>();
            if (root == null) return result;

            IEnumerable<XElement> lists = root.Name.LocalName switch {
                "CaptureSequenceList" => new[] { root },
                "ArrayOfCaptureSequenceList" => root.Elements().Where(e => e.Name.LocalName == "CaptureSequenceList"),
                _ => Enumerable.Empty<XElement>()
            };

            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int index = 0;
            foreach (var list in lists) {
                index++;
                var target = ReadList(list, source, writeUtc, index);
                if (target == null) continue;
                seen.TryGetValue(target.Name, out int n);
                seen[target.Name] = n + 1;
                // Same identity rule as the JSON reader: file + name + which occurrence of that name.
                target.Id = StableId.From(source.ToLowerInvariant(), target.Name.ToLowerInvariant(), n.ToString(CultureInfo.InvariantCulture));
                result.Add(target);
            }
            return result;
        }

        private static SequenceTarget? ReadList(XElement list, string source, DateTime writeUtc, int index) {
            string name = (Attr(list, "TargetName") ?? "").Trim();

            // Decimal coordinates when present (J2000), otherwise the h/m/s attributes.
            double? ra = null, dec = null;
            var coords = Child(list, "Coordinates");
            if (coords != null) {
                ra = Number(Child(coords, "RA")?.Value);
                dec = Number(Child(coords, "Dec")?.Value);
            }
            if (ra == null || dec == null) {
                ra = (Number(Attr(list, "RAHours")) ?? 0) + (Number(Attr(list, "RAMinutes")) ?? 0) / 60.0 + (Number(Attr(list, "RASeconds")) ?? 0) / 3600.0;
                double decDeg = Number(Attr(list, "DecDegrees")) ?? 0;
                double abs = Math.Abs(decDeg) + (Number(Attr(list, "DecMinutes")) ?? 0) / 60.0 + (Number(Attr(list, "DecSeconds")) ?? 0) / 3600.0;
                bool negative = decDeg < 0 || string.Equals(Child(list, "NegativeDec")?.Value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
                dec = negative ? -abs : abs;
            }

            if (name.Length == 0) {
                if (ra == 0 && dec == 0) return null;
                name = Path.GetFileNameWithoutExtension(source) + (index > 1 ? $" #{index}" : "");
            }

            var target = new SequenceTarget {
                Name = name,
                ContainerName = name,
                RaHours = ((ra!.Value % 24) + 24) % 24,
                DecDegrees = dec!.Value,
                PositionAngle = Number(Attr(list, "PositionAngle")) ?? 0,
                SourceFile = source,
                FileWriteUtc = writeUtc,
                Order = string.Equals(Attr(list, "Mode"), "ROTATE", StringComparison.OrdinalIgnoreCase) ? ShootingOrder.Rotate : ShootingOrder.RowByRow
            };

            var lines = new Dictionary<string, ExposureLine>();
            var order = new List<ExposureLine>();
            foreach (var row in list.Elements().Where(e => e.Name.LocalName == "CaptureSequence")) {
                if (string.Equals(Child(row, "Enabled")?.Value?.Trim(), "false", StringComparison.OrdinalIgnoreCase)) continue;
                var imageType = Child(row, "ImageType")?.Value?.Trim();
                if (!string.IsNullOrEmpty(imageType) && !imageType.Equals("LIGHT", StringComparison.OrdinalIgnoreCase)) continue;
                double exposure = Number(Child(row, "ExposureTime")?.Value) ?? 0;
                if (exposure <= 0) continue;
                int planned = (int)Math.Max(0, Math.Round(Number(Child(row, "TotalExposureCount")?.Value) ?? 0));
                int done = (int)Math.Max(0, Math.Round(Number(Child(row, "ProgressExposureCount")?.Value) ?? 0));
                var filterName = Child(Child(row, "FilterType"), "Name")?.Value?.Trim();
                string filter = string.IsNullOrEmpty(filterName) ? SequenceReader.NoFilter : filterName;

                string key = ExposureLine.MakeKey(filter, exposure);
                if (!lines.TryGetValue(key, out var line)) {
                    line = new ExposureLine { Filter = filter, ExposureSeconds = exposure };
                    lines[key] = line;
                    order.Add(line);
                }
                line.Planned += planned;
                line.Done += Math.Min(done, planned);
            }
            // File order: it is the order NINA shoots the rows in.
            target.Lines = order;
            return target;
        }

        private static XElement? Child(XElement? e, string localName) => e?.Elements().FirstOrDefault(c => c.Name.LocalName == localName);

        private static string? Attr(XElement e, string name) => e.Attribute(name)?.Value;

        private static double? Number(string? text) {
            if (string.IsNullOrWhiteSpace(text)) return null;
            return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        }
    }
}
