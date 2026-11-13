using System;
using System.Collections.Generic;
using System.Linq;
using TargetBoard.Core.Models;

namespace TargetBoard.Core.Sky {

    /// <summary>Works out when a target meets all of its conditions.</summary>
    public sealed class VisibilityCalculator {
        private readonly ISkyContext sky;

        public VisibilityCalculator(ISkyContext sky) {
            this.sky = sky;
        }

        public static TimeSpan DefaultStep { get; } = TimeSpan.FromMinutes(5);

        /// <summary>Coordinates of date for the middle of a range; precession over one night is negligible.</summary>
        public static (double Ra, double Dec) OfDate(double raJ2000, double decJ2000, TimeRange range)
            => SkyMath.PrecessFromJ2000(raJ2000, decJ2000, range.StartUtc + TimeSpan.FromTicks(range.Duration.Ticks / 2));

        public HorizontalPosition Position(double raOfDate, double decOfDate, DateTime utc)
            => SkyMath.ToHorizontal(raOfDate, decOfDate, sky.Observer, utc);

        public bool Passes(double raOfDate, double decOfDate, IReadOnlyList<ConditionSpec> conditions, DateTime utc) {
            if (conditions.Count == 0) return true;
            var pos = Position(raOfDate, decOfDate, utc);
            double? localMinutes = null;
            foreach (var c in conditions) {
                if (!c.Enabled) continue;
                switch (c.Kind) {
                    case ConditionKind.MinAltitude:
                        if (pos.AltitudeDeg < c.Value) return false;
                        break;
                    case ConditionKind.MaxAltitude:
                        if (pos.AltitudeDeg > c.Value) return false;
                        break;
                    case ConditionKind.AboveHorizon:
                        if (pos.AltitudeDeg < sky.HorizonAltitude(pos.AzimuthDeg) + c.Value) return false;
                        break;
                    case ConditionKind.HourAngle:
                        double lo = Math.Min(c.Value, c.Value2), hi = Math.Max(c.Value, c.Value2);
                        if (pos.HourAngleHours < lo || pos.HourAngleHours > hi) return false;
                        break;
                    case ConditionKind.TimeWindow:
                        localMinutes ??= NightMath.LocalMinutesOfDay(utc, sky.TimeZone);
                        double m = localMinutes.Value, from = c.Value, to = c.Value2;
                        bool inside = from <= to ? (m >= from && m <= to) : (m >= from || m <= to);
                        if (!inside) return false;
                        break;
                }
            }
            return true;
        }

        /// <summary>
        /// The parts of <paramref name="range"/> where every enabled condition holds.
        /// Samples every <paramref name="step"/> and refines each edge to about 20 seconds.
        /// </summary>
        public List<TimeRange> Windows(double raJ2000, double decJ2000, IReadOnlyList<ConditionSpec> conditions, TimeRange range, TimeSpan? step = null) {
            var result = new List<TimeRange>();
            if (range.IsEmpty) return result;
            var active = conditions.Where(c => c.Enabled).ToList();
            if (active.Count == 0) {
                result.Add(range);
                return result;
            }

            var (ra, dec) = OfDate(raJ2000, decJ2000, range);
            bool Test(DateTime t) => Passes(ra, dec, active, t);
            var s = step ?? DefaultStep;

            DateTime? open = null;
            var prevT = range.StartUtc;
            bool prev = Test(prevT);
            if (prev) open = prevT;

            while (prevT < range.EndUtc) {
                var t = prevT + s;
                if (t > range.EndUtc) t = range.EndUtc;
                bool now = Test(t);
                if (now && !prev) {
                    open = Edge(prevT, t, Test, risingEdge: true);
                } else if (!now && prev && open != null) {
                    result.Add(new TimeRange(open.Value, Edge(prevT, t, Test, risingEdge: false)));
                    open = null;
                }
                prev = now;
                prevT = t;
            }
            if (open != null) result.Add(new TimeRange(open.Value, range.EndUtc));
            result.RemoveAll(w => w.Duration < TimeSpan.FromMinutes(1));
            return result;
        }

        /// <summary>Altitude samples across a range, for drawing curves.</summary>
        public List<(DateTime Utc, double Altitude)> AltitudeCurve(double raJ2000, double decJ2000, TimeRange range, int points) {
            var list = new List<(DateTime, double)>();
            if (range.IsEmpty || points < 2) return list;
            var (ra, dec) = OfDate(raJ2000, decJ2000, range);
            for (int i = 0; i < points; i++) {
                var t = range.StartUtc + TimeSpan.FromTicks(range.Duration.Ticks * i / (points - 1));
                list.Add((t, Position(ra, dec, t).AltitudeDeg));
            }
            return list;
        }

        private static DateTime Edge(DateTime a, DateTime b, Func<DateTime, bool> test, bool risingEdge) {
            // On a rising edge "a" fails and "b" passes; on a falling edge the reverse.
            while ((b - a).TotalSeconds > 20) {
                var mid = a + TimeSpan.FromTicks((b - a).Ticks / 2);
                if (test(mid) == risingEdge) b = mid; else a = mid;
            }
            // Return the instant on the passing side so windows never include failing time.
            return risingEdge ? b : a;
        }
    }
}
