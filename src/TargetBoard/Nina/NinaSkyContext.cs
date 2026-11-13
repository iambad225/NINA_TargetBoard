using NINA.Astrometry;
using NINA.Astrometry.RiseAndSet;
using NINA.Core.Utility;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Concurrent;
using System.Globalization;
using TargetBoard.Core.Models;
using TargetBoard.Core.Sky;

namespace TargetBoard.Nina {

    /// <summary>
    /// Sky facts taken from NINA itself: the active profile's latitude, longitude, elevation and custom horizon,
    /// and NINA's own twilight calculation (the same one behind the altitude charts and "Wait for time" dawn/dusk).
    /// </summary>
    internal sealed class NinaSkyContext : ISkyContext {
        // NINA 3.2 can return wrong Sun/Moon values when plugins compute bodies on several threads at once
        // (fixed in 3.3), so calls into AstroUtil are made one at a time.
        private static readonly object AstroLock = new();

        private readonly IProfileService profileService;
        private readonly ConcurrentDictionary<string, TimeRange?> darkCache = new();
        private readonly ConcurrentDictionary<string, double?> moonCache = new();

        public NinaSkyContext(IProfileService profileService) {
            this.profileService = profileService;
        }

        private IAstrometrySettings Astro => profileService.ActiveProfile.AstrometrySettings;

        public Observer Observer => new(Astro.Latitude, Astro.Longitude, Astro.Elevation);

        public TimeZoneInfo TimeZone => TimeZoneInfo.Local;

        public bool HasCustomHorizon => Astro.Horizon != null;

        public string LocationKey => string.Join("|",
            Astro.Latitude.ToString("R", CultureInfo.InvariantCulture),
            Astro.Longitude.ToString("R", CultureInfo.InvariantCulture),
            Astro.Elevation.ToString("R", CultureInfo.InvariantCulture),
            Astro.HorizonFilePath ?? "");

        public double HorizonAltitude(double azimuthDeg) {
            var horizon = Astro.Horizon;
            if (horizon == null) return 0;
            try {
                return horizon.GetAltitude(azimuthDeg);
            } catch (Exception) {
                return 0;
            }
        }

        public double? GetMoonIllumination(DateOnly night) {
            string key = night.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "|" + LocationKey;
            return moonCache.GetOrAdd(key, _ => {
                try {
                    var midnight = new DateTime(night.Year, night.Month, night.Day, 23, 59, 0, DateTimeKind.Local);
                    var observer = new ObserverInfo { Latitude = Astro.Latitude, Longitude = Astro.Longitude, Elevation = Astro.Elevation };
                    lock (AstroLock) return AstroUtil.GetMoonIllumination(midnight, observer);
                } catch (Exception ex) {
                    Logger.Error("Target Board: moon illumination failed", ex);
                    return null;
                }
            });
        }

        public TimeRange? GetDarkWindow(DateOnly night, DarkTimeMode mode) {
            string key = night.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "|" + mode + "|" + LocationKey;
            return darkCache.GetOrAdd(key, _ => ComputeDark(night, mode));
        }

        private TimeRange? ComputeDark(DateOnly night, DarkTimeMode mode) {
            var observer = Observer;
            // Our own Sun model is the cross-check and the fallback for edge cases NINA's search does not cover.
            var fallback = new BuiltInSkyContext(observer, TimeZone).GetDarkWindow(night, mode);
            try {
                var reference = new DateTime(night.Year, night.Month, night.Day, 12, 0, 0, DateTimeKind.Local);
                var first = FromNina(reference, mode, observer);
                if (first == null) return fallback;
                if (fallback != null && !Agrees(first.Value, fallback.Value)) {
                    var second = FromNina(reference, mode, observer);
                    if (second != null && Agrees(second.Value, fallback.Value)) return second;
                    Logger.Warning($"Target Board: NINA twilight for {night:yyyy-MM-dd} ({first.Value.StartUtc:u} - {first.Value.EndUtc:u}) differs from the check value; using the check value.");
                    return fallback;
                }
                return first;
            } catch (Exception ex) {
                Logger.Error("Target Board: twilight calculation failed, using built-in value", ex);
                return fallback;
            }
        }

        private static TimeRange? FromNina(DateTime reference, DarkTimeMode mode, Observer o) {
            RiseAndSetEvent ev;
            lock (AstroLock) {
                ev = mode == DarkTimeMode.Nautical
                    ? AstroUtil.GetNauticalNightTimes(reference, o.LatitudeDeg, o.LongitudeDeg, o.ElevationM)
                    : AstroUtil.GetNightTimes(reference, o.LatitudeDeg, o.LongitudeDeg, o.ElevationM);
            }
            // NINA searches from local noon: "Set" is dusk (the Sun sinks below the limit), "Rise" is dawn.
            if (ev?.Set == null || ev.Rise == null) return null;
            var dusk = ToUtc(ev.Set.Value);
            var dawn = ToUtc(ev.Rise.Value);
            if (dawn <= dusk) return null;
            return new TimeRange(dusk, dawn);
        }

        private static bool Agrees(TimeRange a, TimeRange b)
            => (a.StartUtc - b.StartUtc).Duration() < TimeSpan.FromMinutes(15) && (a.EndUtc - b.EndUtc).Duration() < TimeSpan.FromMinutes(15);

        private static DateTime ToUtc(DateTime t) => t.Kind switch {
            DateTimeKind.Utc => t,
            DateTimeKind.Local => t.ToUniversalTime(),
            _ => DateTime.SpecifyKind(t, DateTimeKind.Local).ToUniversalTime()
        };
    }
}
