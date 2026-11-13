using System;

namespace TargetBoard.Core.Sky {

    public readonly record struct Observer(double LatitudeDeg, double LongitudeDeg, double ElevationM);

    public readonly record struct HorizontalPosition(double AltitudeDeg, double AzimuthDeg, double HourAngleHours);

    /// <summary>
    /// Fast positional astronomy for planning. Uses the same altitude formula as NINA's AstroUtil,
    /// with a closed-form sidereal time (IAU 1982) so thousands of samples per night stay cheap.
    /// No refraction, matching NINA's own altitude charts. Longitude is east-positive, as in NINA.
    /// </summary>
    public static class SkyMath {
        private const double Deg = Math.PI / 180.0;
        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static DateTime AsUtc(DateTime t) => t.Kind switch {
            DateTimeKind.Utc => t,
            DateTimeKind.Local => t.ToUniversalTime(),
            _ => DateTime.SpecifyKind(t, DateTimeKind.Utc)
        };

        public static double JulianDate(DateTime utc) => 2440587.5 + (AsUtc(utc) - UnixEpoch).TotalDays;

        public static double Normalize360(double deg) {
            deg %= 360.0;
            return deg < 0 ? deg + 360.0 : deg;
        }

        /// <summary>Wraps hours into [-12, 12).</summary>
        public static double NormalizeHourAngle(double hours) {
            hours = (hours + 12.0) % 24.0;
            if (hours < 0) hours += 24.0;
            return hours - 12.0;
        }

        /// <summary>Greenwich mean sidereal time in hours.</summary>
        public static double GmstHours(DateTime utc) {
            double d = JulianDate(utc) - 2451545.0;
            double t = d / 36525.0;
            double gmst = 280.46061837 + 360.98564736629 * d + 0.000387933 * t * t - t * t * t / 38710000.0;
            return Normalize360(gmst) / 15.0;
        }

        public static double LstHours(DateTime utc, double longitudeDeg) {
            double lst = GmstHours(utc) + longitudeDeg / 15.0;
            lst %= 24.0;
            return lst < 0 ? lst + 24.0 : lst;
        }

        /// <summary>Precesses J2000 coordinates to the given date (Meeus, chapter 21).</summary>
        public static (double RaHours, double DecDeg) PrecessFromJ2000(double raHours, double decDeg, DateTime utc) {
            double t = (JulianDate(utc) - 2451545.0) / 36525.0;
            double zeta = (2306.2181 * t + 0.30188 * t * t + 0.017998 * t * t * t) / 3600.0 * Deg;
            double z = (2306.2181 * t + 1.09468 * t * t + 0.018203 * t * t * t) / 3600.0 * Deg;
            double theta = (2004.3109 * t - 0.42665 * t * t - 0.041833 * t * t * t) / 3600.0 * Deg;

            double ra0 = raHours * 15.0 * Deg, dec0 = decDeg * Deg;
            double a = Math.Cos(dec0) * Math.Sin(ra0 + zeta);
            double b = Math.Cos(theta) * Math.Cos(dec0) * Math.Cos(ra0 + zeta) - Math.Sin(theta) * Math.Sin(dec0);
            double c = Math.Sin(theta) * Math.Cos(dec0) * Math.Cos(ra0 + zeta) + Math.Cos(theta) * Math.Sin(dec0);

            double ra = Math.Atan2(a, b) + z;
            double dec = Math.Asin(Math.Clamp(c, -1.0, 1.0));
            return (Normalize360(ra / Deg) / 15.0, dec / Deg);
        }

        /// <summary>Altitude, azimuth (north = 0, east = 90) and hour angle for coordinates of date.</summary>
        public static HorizontalPosition ToHorizontal(double raHours, double decDeg, Observer o, DateTime utc) {
            double ha = NormalizeHourAngle(LstHours(utc, o.LongitudeDeg) - raHours);
            double h = ha * 15.0 * Deg, dec = decDeg * Deg, lat = o.LatitudeDeg * Deg;

            double sinAlt = Math.Sin(dec) * Math.Sin(lat) + Math.Cos(dec) * Math.Cos(lat) * Math.Cos(h);
            double alt = Math.Asin(Math.Clamp(sinAlt, -1.0, 1.0));
            double az = Math.Atan2(-Math.Cos(dec) * Math.Sin(h),
                                   Math.Sin(dec) * Math.Cos(lat) - Math.Cos(dec) * Math.Sin(lat) * Math.Cos(h));
            return new HorizontalPosition(alt / Deg, Normalize360(az / Deg), ha);
        }

        /// <summary>Low-precision Sun position (about 0.01 degree), coordinates of date.</summary>
        public static (double RaHours, double DecDeg) Sun(DateTime utc) {
            double n = JulianDate(utc) - 2451545.0;
            double l = Normalize360(280.460 + 0.9856474 * n);
            double g = Normalize360(357.528 + 0.9856003 * n) * Deg;
            double lambda = (l + 1.915 * Math.Sin(g) + 0.020 * Math.Sin(2 * g)) * Deg;
            double eps = (23.439 - 0.0000004 * n) * Deg;
            double ra = Math.Atan2(Math.Cos(eps) * Math.Sin(lambda), Math.Cos(lambda));
            double dec = Math.Asin(Math.Sin(eps) * Math.Sin(lambda));
            return (Normalize360(ra / Deg) / 15.0, dec / Deg);
        }

        public static double SunAltitude(DateTime utc, Observer o) {
            var (ra, dec) = Sun(utc);
            return ToHorizontal(ra, dec, o, utc).AltitudeDeg;
        }

        /// <summary>
        /// Low-precision geocentric Moon position, coordinates of date (the Astronomical Almanac's short series,
        /// about 0.3 degree), and its horizontal parallax in degrees.
        /// </summary>
        public static (double RaHours, double DecDeg, double ParallaxDeg) Moon(DateTime utc) {
            double t = (JulianDate(utc) - 2451545.0) / 36525.0;
            double S(double a, double b) => Math.Sin((a + b * t) * Deg);
            double C(double a, double b) => Math.Cos((a + b * t) * Deg);
            double lambda = 218.32 + 481267.881 * t
                            + 6.29 * S(135.0, 477198.87) - 1.27 * S(259.3, -413335.36) + 0.66 * S(235.7, 890534.22)
                            + 0.21 * S(269.9, 954397.74) - 0.19 * S(357.5, 35999.05) - 0.11 * S(186.5, 966404.03);
            double beta = 5.13 * S(93.3, 483202.02) + 0.28 * S(228.2, 960400.89) - 0.28 * S(318.3, 6003.15) - 0.17 * S(217.6, -407332.21);
            double parallax = 0.9508 + 0.0518 * C(135.0, 477198.87) + 0.0095 * C(259.3, -413335.36)
                              + 0.0078 * C(235.7, 890534.22) + 0.0028 * C(269.9, 954397.74);
            double l = Normalize360(lambda) * Deg, b = beta * Deg;
            double eps = (23.439 - 0.0000004 * (JulianDate(utc) - 2451545.0)) * Deg;
            double x = Math.Cos(b) * Math.Cos(l);
            double y = Math.Cos(eps) * Math.Cos(b) * Math.Sin(l) - Math.Sin(eps) * Math.Sin(b);
            double z = Math.Sin(eps) * Math.Cos(b) * Math.Sin(l) + Math.Cos(eps) * Math.Sin(b);
            double ra = Math.Atan2(y, x);
            double dec = Math.Asin(Math.Clamp(z, -1.0, 1.0));
            return (Normalize360(ra / Deg) / 15.0, dec / Deg, parallax);
        }

        /// <summary>Altitude of the Moon as seen from the observer (parallax applied, no refraction).</summary>
        public static double MoonAltitude(DateTime utc, Observer o) {
            var (ra, dec, parallax) = Moon(utc);
            double geocentric = ToHorizontal(ra, dec, o, utc).AltitudeDeg;
            return geocentric - Math.Asin(Math.Sin(parallax * Deg) * Math.Cos(geocentric * Deg)) / Deg;
        }

        /// <summary>Approximate Moon illumination (0..1) from its mean age. Good to a few percent.</summary>
        public static double MoonIlluminationApprox(DateTime utc) {
            const double synodic = 29.530588853;
            double age = (JulianDate(utc) - 2451550.1) % synodic;
            if (age < 0) age += synodic;
            return (1 - Math.Cos(2 * Math.PI * age / synodic)) / 2;
        }
    }
}
