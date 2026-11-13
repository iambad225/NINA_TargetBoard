using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TargetBoard.Core {

    /// <summary>Text formatting shared by the core and the plugin UI.</summary>
    public static class Fmt {

        /// <summary>"3h 05m", "45m", "0m".</summary>
        public static string Duration(TimeSpan span) {
            if (span < TimeSpan.Zero) span = TimeSpan.Zero;
            int totalMinutes = (int)Math.Round(span.TotalMinutes);
            int h = totalMinutes / 60, m = totalMinutes % 60;
            return h > 0 ? $"{h}h {m:00}m" : $"{m}m";
        }

        public static string DurationSeconds(double seconds) => Duration(TimeSpan.FromSeconds(Math.Max(0, seconds)));

        /// <summary>Exposure length: "300s", "0.5s", "2m".</summary>
        public static string Seconds(double seconds) {
            if (seconds >= 60 && Math.Abs(seconds % 60) < 0.001) return (seconds / 60).ToString("0", CultureInfo.InvariantCulture) + "m";
            return seconds.ToString("0.###", CultureInfo.InvariantCulture) + "s";
        }

        public static string ClockFromMinutes(double minutesAfterMidnight) {
            int total = (int)Math.Round(minutesAfterMidnight);
            total = ((total % 1440) + 1440) % 1440;
            return $"{total / 60:00}:{total % 60:00}";
        }

        public static bool TryParseClock(string text, out double minutes) {
            minutes = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (TimeSpan.TryParseExact(text.Trim(), new[] { @"h\:mm", @"hh\:mm", @"hhmm" }, CultureInfo.InvariantCulture, out var ts)
                && ts.TotalMinutes < 1440) {
                minutes = ts.TotalMinutes;
                return true;
            }
            return false;
        }

        public static string RaText(double raHours) {
            var t = TimeSpan.FromHours(((raHours % 24) + 24) % 24);
            return $"{(int)t.TotalHours:00}h {t.Minutes:00}m {t.Seconds:00}s";
        }

        public static string DecText(double decDeg) {
            var sign = decDeg < 0 ? "-" : "+";
            var a = Math.Abs(decDeg);
            int d = (int)a; int m = (int)((a - d) * 60); int s = (int)Math.Round(((a - d) * 60 - m) * 60);
            if (s == 60) { s = 0; m++; }
            if (m == 60) { m = 0; d++; }
            return $"{sign}{d:00}° {m:00}' {s:00}\"";
        }
    }

    public static class StableId {
        /// <summary>A short, stable hex id built from the given parts.</summary>
        public static string From(params string[] parts) {
            var text = string.Join("\u001f", parts);
            var hash = SHA1.HashData(Encoding.UTF8.GetBytes(text));
            return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
        }

        public static string New() => Guid.NewGuid().ToString("N").Substring(0, 12);
    }

    /// <summary>Event colors. Picked so they stay readable on NINA's dark themes.</summary>
    public static class Palette {
        public static readonly string[] Colors = {
            "#4F8EF7", "#E5735A", "#3FB68B", "#C77DDB", "#E8B04A",
            "#5BC0DE", "#F06292", "#9CCC65", "#A1887F", "#7986CB"
        };

        public static string ForKey(string key) {
            int sum = 0;
            foreach (var c in key ?? "") sum = unchecked(sum * 31 + c);
            return Colors[(int)((uint)sum % (uint)Colors.Length)];
        }
    }
}
