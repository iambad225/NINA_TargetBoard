using System;
using System.Collections.Concurrent;
using TargetBoard.Core.Models;

namespace TargetBoard.Core.Sky {

    /// <summary>
    /// Where the observer is and when it is dark. The plugin supplies NINA's own values
    /// (profile location, custom horizon, twilight times); tests use <see cref="BuiltInSkyContext"/>.
    /// </summary>
    public interface ISkyContext {
        Observer Observer { get; }
        TimeZoneInfo TimeZone { get; }
        bool HasCustomHorizon { get; }
        /// <summary>Changes whenever location or horizon changes, so cached windows can be dropped.</summary>
        string LocationKey { get; }
        /// <summary>The dark span for the night that starts on the evening of <paramref name="night"/>, or null if it never gets dark.</summary>
        TimeRange? GetDarkWindow(DateOnly night, DarkTimeMode mode);
        double? GetMoonIllumination(DateOnly night);
        double HorizonAltitude(double azimuthDeg);
    }

    /// <summary>Helpers for "nights", which run from local noon to the next local noon (as NINA does).</summary>
    public static class NightMath {
        public static DateTime ToLocal(DateTime utc, TimeZoneInfo zone) => TimeZoneInfo.ConvertTimeFromUtc(SkyMath.AsUtc(utc), zone);

        public static DateOnly NightOf(DateTime utc, TimeZoneInfo zone) {
            var local = ToLocal(utc, zone);
            var date = DateOnly.FromDateTime(local);
            return local.Hour >= 12 ? date : date.AddDays(-1);
        }

        public static DateTime LocalToUtc(DateTime localWallClock, TimeZoneInfo zone) {
            var unspecified = DateTime.SpecifyKind(localWallClock, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(unspecified)) unspecified = unspecified.AddHours(1);
            return TimeZoneInfo.ConvertTimeToUtc(unspecified, zone);
        }

        public static DateTime NoonUtc(DateOnly night, TimeZoneInfo zone)
            => LocalToUtc(night.ToDateTime(new TimeOnly(12, 0)), zone);

        public static TimeRange Span(DateOnly night, TimeZoneInfo zone)
            => new TimeRange(NoonUtc(night, zone), NoonUtc(night.AddDays(1), zone));

        /// <summary>Wall-clock hours after the night's local noon (12 = midnight). Used to place things on the calendar.</summary>
        public static double ClockHoursAfterNoon(DateTime utc, DateOnly night, TimeZoneInfo zone) {
            var local = ToLocal(utc, zone);
            return (local - night.ToDateTime(new TimeOnly(12, 0))).TotalHours;
        }

        /// <summary>The UTC instant at a wall-clock offset after the night's local noon.</summary>
        public static DateTime FromClockHoursAfterNoon(double hours, DateOnly night, TimeZoneInfo zone)
            => LocalToUtc(night.ToDateTime(new TimeOnly(12, 0)).AddHours(hours), zone);

        public static double LocalMinutesOfDay(DateTime utc, TimeZoneInfo zone) => ToLocal(utc, zone).TimeOfDay.TotalMinutes;
    }

    /// <summary>
    /// Self-contained sky context: computes twilight from a low-precision Sun.
    /// The plugin uses NINA's calculations instead; this one backs the tests and acts as a fallback.
    /// </summary>
    public sealed class BuiltInSkyContext : ISkyContext {
        private readonly Func<double, double>? horizon;
        private readonly ConcurrentDictionary<(DateOnly, DarkTimeMode), TimeRange?> cache = new();

        public BuiltInSkyContext(Observer observer, TimeZoneInfo zone, Func<double, double>? horizon = null) {
            Observer = observer;
            TimeZone = zone;
            this.horizon = horizon;
        }

        public Observer Observer { get; }
        public TimeZoneInfo TimeZone { get; }
        public bool HasCustomHorizon => horizon != null;
        public string LocationKey => $"{Observer.LatitudeDeg:R}|{Observer.LongitudeDeg:R}|{TimeZone.Id}|{HasCustomHorizon}";
        public double HorizonAltitude(double azimuthDeg) => horizon?.Invoke(azimuthDeg) ?? 0.0;

        public double? GetMoonIllumination(DateOnly night)
            => SkyMath.MoonIlluminationApprox(NightMath.LocalToUtc(night.ToDateTime(new TimeOnly(23, 59)), TimeZone));

        public TimeRange? GetDarkWindow(DateOnly night, DarkTimeMode mode)
            => cache.GetOrAdd((night, mode), key => Compute(key.Item1, key.Item2));

        private TimeRange? Compute(DateOnly night, DarkTimeMode mode) {
            double limit = mode == DarkTimeMode.Nautical ? -12.0 : -18.0;
            var span = NightMath.Span(night, TimeZone);
            var step = TimeSpan.FromMinutes(5);
            bool Dark(DateTime t) => SkyMath.SunAltitude(t, Observer) < limit;

            DateTime? start = null, end = null;
            bool prev = Dark(span.StartUtc);
            if (prev) start = span.StartUtc;
            for (var t = span.StartUtc + step; t <= span.EndUtc; t += step) {
                bool now = Dark(t);
                if (now && !prev && start == null) start = Refine(t - step, t, Dark);
                if (!now && prev && start != null) { end = Refine(t - step, t, Dark); break; }
                prev = now;
            }
            if (start == null) return null;
            return new TimeRange(start.Value, end ?? span.EndUtc);
        }

        /// <summary>Finds the switch time between two samples to within a few seconds.</summary>
        private static DateTime Refine(DateTime a, DateTime b, Func<DateTime, bool> test) {
            bool stateA = test(a);
            while ((b - a).TotalSeconds > 5) {
                var mid = a + TimeSpan.FromTicks((b - a).Ticks / 2);
                if (test(mid) == stateA) a = mid; else b = mid;
            }
            return b;
        }
    }
}
