using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using TargetBoard.Core;
using TargetBoard.Core.Engine;
using TargetBoard.Core.History;
using TargetBoard.Core.Models;
using TargetBoard.Core.Scheduling;
using TargetBoard.Core.Sequences;
using TargetBoard.Core.Sky;
using TargetBoard.Core.Storage;

namespace TargetBoard.Core.Tests {

    /// <summary>Minimal test runner: each test is a method; a failed check throws.</summary>
    public static class Program {
        private static int passed, failed;

        public static int Main() {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            Run(nameof(SkyMathMatchesAstropy), SkyMathMatchesAstropy);
            Run(nameof(TwilightMatchesAstropy), TwilightMatchesAstropy);
            Run(nameof(ReadsNinaSequenceFile), ReadsNinaSequenceFile);
            Run(nameof(ReadsNina33ExpressionsAndPlainArrays), ReadsNina33ExpressionsAndPlainArrays);
            Run(nameof(SubtractsTimeRanges), SubtractsTimeRanges);
            Run(nameof(VisibilityWindowsFollowConditions), VisibilityWindowsFollowConditions);
            Run(nameof(SchedulerPlacesClipsAndRejects), SchedulerPlacesClipsAndRejects);
            Run(nameof(EnginePlansTracksAndPersists), EnginePlansTracksAndPersists);
            Run(nameof(MilestonesFromResavedSequenceFile), MilestonesFromResavedSequenceFile);
            Run(nameof(StoreRecoversFromBadFile), StoreRecoversFromBadFile);
            Run(nameof(ScannerSkipsTemplatesAndOtherJson), ScannerSkipsTemplatesAndOtherJson);
            Run(nameof(NothingIsWrittenBeforeLoad), NothingIsWrittenBeforeLoad);
            Run(nameof(ReadsSimpleSequencerFiles), ReadsSimpleSequencerFiles);
            Run(nameof(TargetSetsLinkSortAndPlaceTogether), TargetSetsLinkSortAndPlaceTogether);
            Run(nameof(MarkedDoneStopsPlanningAndIsRecorded), MarkedDoneStopsPlanningAndIsRecorded);
            Run(nameof(MoonMatchesAstropy), MoonMatchesAstropy);
            Run(nameof(FramePlannerFollowsShootingOrder), FramePlannerFollowsShootingOrder);
            Run(nameof(OutcomesFromTheFrameLog), OutcomesFromTheFrameLog);
            Run(nameof(ReflowPushesAndCascades), ReflowPushesAndCascades);
            Run(nameof(ForcedPlacement), ForcedPlacement);
            Run(nameof(SettleCarriesAndCascades), SettleCarriesAndCascades);
            Run(nameof(ShiftPlanAfterAMissedNight), ShiftPlanAfterAMissedNight);
            Run(nameof(LoadsBoardFromVersion110), LoadsBoardFromVersion110);
            Run(nameof(MovesSeveralEventsTogether), MovesSeveralEventsTogether);
            Run(nameof(UndoAndLayoutSurviveARestart), UndoAndLayoutSurviveARestart);
            Console.WriteLine();
            Console.WriteLine($"{passed} passed, {failed} failed");
            return failed == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test) {
            try {
                test();
                passed++;
                Console.WriteLine($"PASS  {name}");
            } catch (Exception ex) {
                failed++;
                Console.WriteLine($"FAIL  {name}\n      {ex.Message}");
            }
        }

        private static void Check(bool condition, string message) {
            if (!condition) throw new Exception(message);
        }

        private static void Near(double actual, double expected, double tolerance, string what) {
            if (Math.Abs(actual - expected) > tolerance) throw new Exception($"{what}: expected {expected} ± {tolerance}, got {actual}");
        }

        private static void NearTime(DateTime actual, DateTime expected, TimeSpan tolerance, string what) {
            if ((actual - expected).Duration() > tolerance) throw new Exception($"{what}: expected {expected:u} ± {tolerance}, got {actual:u}");
        }

        private static DateTime Utc(string iso) => DateTime.SpecifyKind(DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), DateTimeKind.Utc);

        private static readonly Observer London = new(51.4779, -0.0015, 0);
        private static readonly Observer MaunaKea = new(19.8207, -155.468, 4200);
        private static readonly Observer Sydney = new(-33.8688, 151.2093, 0);
        private static TimeZoneInfo LondonZone => TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        private static TimeZoneInfo SydneyZone => TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");
        private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

        private static readonly (double Ra, double Dec) M31 = (0 + 42 / 60.0 + 44.3 / 3600.0, 41 + 16 / 60.0 + 9 / 3600.0);
        private static readonly (double Ra, double Dec) Ngc7293 = (22 + 29 / 60.0 + 38.5 / 3600.0, -(20 + 50 / 60.0 + 14 / 3600.0));

        // ------------------------------------------------------------------ sky

        /// <summary>Reference values from astropy 7 (ICRS to AltAz, no refraction).</summary>
        private static void SkyMathMatchesAstropy() {
            var cases = new (Observer obs, (double, double) target, string time, double alt, double az)[] {
                (London, M31, "2026-10-25T20:00:00Z", 63.0460, 97.2869),
                (London, M31, "2026-10-25T23:30:00Z", 75.2346, 233.2561),
                (London, M31, "2026-10-26T03:15:00Z", 41.3231, 288.0559),
                (London, M31, "2026-06-21T12:00:00Z", 37.5468, 291.9621),
                (London, Ngc7293, "2026-10-25T20:00:00Z", 17.7552, 176.5015),
                (London, Ngc7293, "2026-10-25T23:30:00Z", 6.0307, 225.3003),
                (Sydney, M31, "2026-10-25T20:00:00Z", -38.7275, 298.8872),
                (Sydney, Ngc7293, "2026-10-26T03:15:00Z", 1.0460, 114.4256),
                (Sydney, Ngc7293, "2026-06-21T12:00:00Z", 5.9775, 110.9618),
            };
            foreach (var c in cases) {
                var t = Utc(c.time);
                var (ra, dec) = SkyMath.PrecessFromJ2000(c.target.Item1, c.target.Item2, t);
                var p = SkyMath.ToHorizontal(ra, dec, c.obs, t);
                Near(p.AltitudeDeg, c.alt, 0.05, $"altitude {c.time}");
                double azTol = 0.05 / Math.Max(0.2, Math.Cos(c.alt * Math.PI / 180));
                double dAz = Math.Abs(((p.AzimuthDeg - c.az + 540) % 360) - 180);
                Near(dAz, 0, azTol, $"azimuth {c.time}");
            }
            Near(SkyMath.SunAltitude(Utc("2026-10-25T18:00:00Z"), London), -12.0525, 0.05, "sun London");
            Near(SkyMath.SunAltitude(Utc("2026-10-26T05:00:00Z"), London), -16.5379, 0.05, "sun London dawn");
            Near(SkyMath.SunAltitude(Utc("2026-10-26T05:00:00Z"), Sydney), 39.7132, 0.05, "sun Sydney");
        }

        private static void TwilightMatchesAstropy() {
            var tol = TimeSpan.FromMinutes(2);
            var london = new BuiltInSkyContext(London, LondonZone);
            var night = new DateOnly(2026, 10, 25);
            var astro = london.GetDarkWindow(night, DarkTimeMode.Astronomical)!.Value;
            NearTime(astro.StartUtc, Utc("2026-10-25T18:39:00Z"), tol, "London astronomical dusk");
            NearTime(astro.EndUtc, Utc("2026-10-26T04:51:00Z"), tol, "London astronomical dawn");
            var naut = london.GetDarkWindow(night, DarkTimeMode.Nautical)!.Value;
            NearTime(naut.StartUtc, Utc("2026-10-25T18:00:00Z"), tol, "London nautical dusk");
            NearTime(naut.EndUtc, Utc("2026-10-26T05:30:00Z"), tol, "London nautical dawn");

            Check(london.GetDarkWindow(new DateOnly(2026, 6, 21), DarkTimeMode.Astronomical) == null, "London has no astronomical darkness at midsummer");
            var june = london.GetDarkWindow(new DateOnly(2026, 6, 21), DarkTimeMode.Nautical)!.Value;
            NearTime(june.StartUtc, Utc("2026-06-21T22:24:00Z"), tol, "London June nautical dusk");
            NearTime(june.EndUtc, Utc("2026-06-22T01:41:00Z"), tol, "London June nautical dawn");

            // Sydney is UTC+11 in October: the local evening of the 25th is 09:47 UTC.
            var sydney = new BuiltInSkyContext(Sydney, SydneyZone);
            var syd = sydney.GetDarkWindow(night, DarkTimeMode.Astronomical)!.Value;
            NearTime(syd.StartUtc, Utc("2026-10-25T09:47:00Z"), tol, "Sydney dusk");
            NearTime(syd.EndUtc, Utc("2026-10-25T17:33:00Z"), tol, "Sydney dawn");
            Check(NightMath.NightOf(Utc("2026-10-25T17:00:00Z"), SydneyZone) == night, "04:00 local belongs to the previous evening's night");
        }

        // ------------------------------------------------------------------ sequence files

        private static void ReadsNinaSequenceFile() {
            var targets = SequenceReader.ReadFile(Fixture("Autumn Galaxies.json"));
            Check(targets.Count == 2, $"expected 2 targets (placeholder skipped), got {targets.Count}");

            var m31 = targets.Single(t => t.Name == "M 31");
            Near(m31.RaHours, M31.Ra, 1e-9, "M31 RA");
            Near(m31.DecDegrees, M31.Dec, 1e-9, "M31 Dec");
            Near(m31.PositionAngle, 35, 1e-9, "position angle");
            Check(!m31.IsOpenEnded, "M31 loops a fixed number of times");

            ExposureLine Line(SequenceTarget t, string f, double s) => t.Lines.Single(l => l.Filter == f && l.ExposureSeconds == s);
            var ha = Line(m31, "Ha", 300); Check(ha.Planned == 20 && ha.Done == 7, $"Ha {ha.Done}/{ha.Planned}");
            var o3 = Line(m31, "OIII", 300); Check(o3.Planned == 20 && o3.Done == 0, $"OIII {o3.Done}/{o3.Planned}");
            var l = Line(m31, "OIII", 60); Check(l.Planned == 10 && l.Done == 10, $"L (after OIII switch) {l.Done}/{l.Planned}");
            var r = Line(m31, "R", 120); Check(r.Planned == 15 && r.Done == 7, $"R nested loops {r.Done}/{r.Planned}");
            Check(m31.Lines.Count == 4, "flat frames are not counted");
            Near(m31.RemainingSeconds, 13 * 300 + 20 * 300 + 8 * 120, 1e-6, "remaining seconds");

            Check(m31.Captured.Count == 2 && m31.Captured.Sum(c => c.Count) == 17, "ExposureInfoList read");
            var mins = m31.ImportedConditions.Where(c => c.Kind == ConditionKind.MinAltitude).Select(c => c.Value).OrderBy(v => v).ToList();
            Check(mins.SequenceEqual(new[] { 30.0, 35.0 }), $"imported altitude conditions: {string.Join(",", mins)}");

            var ngc = targets.Single(t => t.Name == "NGC 7293");
            Near(ngc.DecDegrees, Ngc7293.Dec, 1e-9, "negative declination");
            Check(ngc.IsOpenEnded, "time condition without a loop count is open-ended");
            Check(ngc.ImportedConditions.Any(c => c.Kind == ConditionKind.AboveHorizon && c.Value == 5), "above-horizon offset imported");
            Check(Line(ngc, "Ha", 600).Planned == 12, "NGC 7293 Ha");

            var again = SequenceReader.ReadFile(Fixture("Autumn Galaxies.json"));
            Check(again.Select(t => t.Id).SequenceEqual(targets.Select(t => t.Id)), "target ids are stable between reads");
        }

        private static void ReadsNina33ExpressionsAndPlainArrays() {
            const string json = @"{
              ""$type"": ""NINA.Sequencer.Container.DeepSkyObjectContainer, NINA.Sequencer"",
              ""Name"": ""IC 1396"",
              ""Target"": { ""TargetName"": ""IC 1396"", ""InputCoordinates"": { ""RAHours"": 21, ""RAMinutes"": 39, ""RASeconds"": 6, ""NegativeDec"": false, ""DecDegrees"": 57, ""DecMinutes"": 30, ""DecSeconds"": 0 } },
              ""Conditions"": [],
              ""Items"": [
                { ""$type"": ""NINA.Sequencer.SequenceItem.Imaging.SmartExposure, NINA.Sequencer"",
                  ""Conditions"": [ { ""$type"": ""NINA.Sequencer.Conditions.LoopCondition, NINA.Sequencer"", ""CompletedIterations"": 4,
                                     ""IterationsExpression"": { ""$type"": ""NINA.Sequencer.Logic.Expression, NINA.Sequencer"", ""Definition"": ""12"" } } ],
                  ""Items"": [
                    { ""$type"": ""NINA.Sequencer.SequenceItem.FilterWheel.SwitchFilter, NINA.Sequencer"", ""Filter"": { ""Name"": ""SII"" } },
                    { ""$type"": ""NINA.Sequencer.SequenceItem.Imaging.TakeExposure, NINA.Sequencer"", ""ImageType"": ""LIGHT"",
                      ""ExposureTimeExpression"": { ""Definition"": ""180"" } }
                  ] }
              ]
            }";
            var t = SequenceReader.ReadJson(json, "/tmp/ic1396.json", DateTime.UtcNow).Single();
            var line = t.Lines.Single();
            Check(line.Filter == "SII" && line.ExposureSeconds == 180 && line.Planned == 12 && line.Done == 4, $"got {line.Filter} {line.ExposureSeconds}s {line.Done}/{line.Planned}");
        }

        // ------------------------------------------------------------------ windows and placement

        private static void SubtractsTimeRanges() {
            var b = Utc("2026-10-25T18:00:00Z");
            TimeRange R(double h1, double h2) => new(b.AddHours(h1), b.AddHours(h2));
            var free = TimeRange.Subtract(new[] { R(0, 10) }, new[] { R(1, 2), R(1.5, 3), R(9, 11), R(-2, -1) });
            Check(free.Count == 2 && free[0] == R(0, 1) && free[1] == R(3, 9), "subtract overlapping busy spans");
            Check(TimeRange.Subtract(new[] { R(0, 1) }, new[] { R(0, 1) }).Count == 0, "fully covered");
        }

        private static void VisibilityWindowsFollowConditions() {
            var sky = new BuiltInSkyContext(London, LondonZone, az => az >= 150 && az <= 210 ? 10 : 0);
            var calc = new VisibilityCalculator(sky);
            var dark = sky.GetDarkWindow(new DateOnly(2026, 10, 25), DarkTimeMode.Astronomical)!.Value;

            var high = calc.Windows(M31.Ra, M31.Dec, new[] { ConditionSpec.MinAltitude(70) }, dark);
            Check(high.Count == 1, $"one window above 70°, got {high.Count}");
            var (ra, dec) = VisibilityCalculator.OfDate(M31.Ra, M31.Dec, dark);
            Near(calc.Position(ra, dec, high[0].StartUtc).AltitudeDeg, 70, 0.1, "altitude at window start");
            Near(calc.Position(ra, dec, high[0].EndUtc).AltitudeDeg, 70, 0.1, "altitude at window end");
            Check(high[0].StartUtc > dark.StartUtc && high[0].EndUtc < dark.EndUtc, "window inside dark time");

            var all = calc.Windows(M31.Ra, M31.Dec, new[] { ConditionSpec.MinAltitude(30) }, dark);
            Check(all.Count == 1 && all[0].StartUtc == dark.StartUtc, "M31 is above 30° from dusk");

            // Low southern target behind a 10° custom horizon to the south.
            var ngcHorizon = calc.Windows(Ngc7293.Ra, Ngc7293.Dec, new[] { ConditionSpec.AboveHorizon(5) }, dark);
            var ngcFlat = calc.Windows(Ngc7293.Ra, Ngc7293.Dec, new[] { ConditionSpec.MinAltitude(5) }, dark);
            Check(ngcFlat.Count == 1, "NGC 7293 has one window above a flat 5°");
            // Behind the 10° wall (az 150-210) it needs 15°; it dips below that at about az 200 and reappears past az 210.
            Check(ngcHorizon.Count == 2, $"custom horizon splits the window in two, got {ngcHorizon.Count}");
            var (ngcRa, ngcDec) = VisibilityCalculator.OfDate(Ngc7293.Ra, Ngc7293.Dec, dark);
            var gapStart = calc.Position(ngcRa, ngcDec, ngcHorizon[0].EndUtc);
            Near(gapStart.AltitudeDeg, 15, 0.1, "first window ends at horizon (10°) + 5°");
            Near(calc.Position(ngcRa, ngcDec, ngcHorizon[1].StartUtc).AzimuthDeg, 210, 0.2, "second window starts where the wall ends");
            Check(TimeRange.Total(ngcHorizon) < TimeRange.Total(ngcFlat), "custom horizon cuts time");
            Check(calc.Windows(Ngc7293.Ra, Ngc7293.Dec, new[] { ConditionSpec.MinAltitude(30) }, dark).Count == 0, "never reaches 30° from London");

            // Local time window that wraps past midnight (London is UTC+0 after 25 Oct).
            var wrap = new ConditionSpec { Kind = ConditionKind.TimeWindow, Value = 22 * 60, Value2 = 2 * 60 };
            var w = calc.Windows(M31.Ra, M31.Dec, new[] { wrap }, dark).Single();
            NearTime(w.StartUtc, Utc("2026-10-25T22:00:00Z"), TimeSpan.FromMinutes(1), "time window start");
            NearTime(w.EndUtc, Utc("2026-10-26T02:00:00Z"), TimeSpan.FromMinutes(1), "time window end");

            var east = new ConditionSpec { Kind = ConditionKind.HourAngle, Value = -12, Value2 = 0 };
            var e = calc.Windows(M31.Ra, M31.Dec, new[] { east }, dark).Single();
            Near(calc.Position(ra, dec, e.EndUtc).HourAngleHours, 0, 0.02, "east-only window ends at the meridian");

            var off = ConditionSpec.MinAltitude(89); off.Enabled = false;
            Check(calc.Windows(M31.Ra, M31.Dec, new[] { off }, dark).Single() == dark, "disabled conditions are ignored");
        }

        private static void SchedulerPlacesClipsAndRejects() {
            var b = Utc("2026-10-25T18:00:00Z");
            var valid = new List<TimeRange> { new(b, b.AddHours(10)) };
            var busy = new List<TimeRange> { new(b.AddHours(2), b.AddHours(4)) };
            var opt = new PlacementOptions { SnapMinutes = 5, MinimumLength = TimeSpan.FromMinutes(15) };

            var fits = Scheduler.Place(b.AddMinutes(7), TimeSpan.FromHours(1), valid, busy, opt);
            Check(fits.Outcome == PlacementOutcome.Fits && fits.Placed!.Value.StartUtc == b.AddMinutes(5), $"snap to 5 min: {fits.Placed}");

            var clipped = Scheduler.Place(b.AddHours(1), TimeSpan.FromHours(2), valid, busy, opt);
            Check(clipped.Outcome == PlacementOutcome.Clipped, "runs into the busy block");
            Check(clipped.Placed!.Value.EndUtc == b.AddHours(2) && clipped.Overflow == TimeSpan.FromHours(1), $"clipped at the next event, overflow {clipped.Overflow}");
            Check(clipped.Message.Contains("another event"), clipped.Message);

            var dawn = Scheduler.Place(b.AddHours(9), TimeSpan.FromHours(3), valid, busy, opt);
            Check(dawn.Outcome == PlacementOutcome.Clipped && dawn.Overflow == TimeSpan.FromHours(2), "clipped at dawn");
            Check(dawn.Message.Contains("dawn"), dawn.Message);

            var inBusy = Scheduler.Place(b.AddHours(3), TimeSpan.FromHours(1), valid, busy, opt);
            Check(inBusy.Placed!.Value.StartUtc == b.AddHours(4), "dropped on another event: moves to the nearest free time");

            var before = Scheduler.Place(b.AddHours(-3), TimeSpan.FromHours(1), valid, busy, opt);
            Check(before.Placed!.Value.StartUtc == b, "dropped before dusk: starts at dusk");

            var day = Scheduler.Place(null, TimeSpan.FromHours(1), valid, new[] { new TimeRange(b, b.AddHours(1)) }, opt);
            Check(day.Placed!.Value.StartUtc == b.AddHours(1), "whole-day drop takes the first free slot");

            var none = Scheduler.Place(b, TimeSpan.FromHours(1), new List<TimeRange>(), busy, opt);
            Check(none.Outcome == PlacementOutcome.Rejected && none.Message.Contains("never meets"), none.Message);

            var full = Scheduler.Place(b, TimeSpan.FromHours(1), valid, new[] { new TimeRange(b, b.AddHours(10)) }, opt);
            Check(full.Outcome == PlacementOutcome.Rejected, "night is full");

            var overlap = Scheduler.Place(b.AddHours(2.5), TimeSpan.FromHours(1), valid, busy, new PlacementOptions { AllowOverlap = true });
            Check(overlap.Placed!.Value.StartUtc == b.AddHours(2.5), "overlap allowed when set");

            var past = Scheduler.Place(b, TimeSpan.FromHours(1), valid, busy, new PlacementOptions { NotBeforeUtc = b.AddHours(11) });
            Check(past.Outcome == PlacementOutcome.Rejected && past.Message.Contains("passed"), past.Message);

            Check(Scheduler.Unscheduled(3600, 10, TimeSpan.FromMinutes(30)) == TimeSpan.FromMinutes(36), "unscheduled time with overhead");
        }

        // ------------------------------------------------------------------ engine end to end

        private static (BoardEngine Engine, string Folder, Func<DateTime> Clock, Action<DateTime> SetClock) NewEngine() {
            var folder = Path.Combine(Path.GetTempPath(), "tb-test-" + Guid.NewGuid().ToString("N"));
            var now = Utc("2026-10-25T15:00:00Z");
            DateTime Clock() => now;
            var engine = new BoardEngine(new BoardStore(folder), new BuiltInSkyContext(London, LondonZone), Clock);
            engine.Load();
            return (engine, folder, Clock, t => now = t);
        }

        private static void EnginePlansTracksAndPersists() {
            var (engine, folder, clock, setClock) = NewEngine();
            File.SetLastWriteTimeUtc(Fixture("Autumn Galaxies.json"), Utc("2026-10-20T06:00:00Z"));
            var (found, report) = SequenceScanner.Scan(new[] { Path.Combine(AppContext.BaseDirectory, "Fixtures") }, Array.Empty<string>(), true);
            Check(report.Errors.Count == 0 && found.Count == 2, $"scan: {found.Count} targets, errors: {string.Join("; ", report.Errors)}");
            engine.ApplyScan(found, report);

            var m31 = engine.GetTargets().Single(t => t.Name == "M 31");
            var kinds = m31.Conditions.Select(c => $"{c.Kind}:{c.Value}:{c.Source}").ToList();
            Check(kinds.SequenceEqual(new[] { "MinAltitude:30:Default", "AboveHorizon:0:Default", "MinAltitude:35:Sequence" }), string.Join(" ", kinds));
            Near(m31.Unscheduled.TotalSeconds, Math.Ceiling(10860 * 1.15 / 300) * 300, 1, "to place = remaining + 15% overhead, rounded up to the 5 min snap");
            var first = engine.Milestones().Single();
            Check(first.Kind == MilestoneKind.PartComplete && first.Source == MilestoneSource.FirstScan && first.TimeUtc == Utc("2026-10-20T06:00:00Z"),
                  "the finished 60s line is noted on first scan, dated by the file");

            var night = new DateOnly(2026, 10, 25);
            var p = engine.PreviewNew(m31.Id, night, Utc("2026-10-25T19:02:00Z"));
            Check(p.Result.Outcome == PlacementOutcome.Fits, p.Result.Message);
            Check(p.Result.Placed!.Value.StartUtc == Utc("2026-10-25T19:00:00Z"), $"start {p.Result.Placed}");
            Check(p.Result.Placed!.Value.Duration.TotalMinutes % 5 == 0, "length rounded to the snap step");
            var ev = engine.Commit(p)!;
            Check(engine.GetTarget(m31.Id)!.Unscheduled == TimeSpan.Zero, "fully scheduled after the drop");

            var ngc = engine.GetTargets().Single(t => t.Name == "NGC 7293");
            var bad = engine.PreviewNew(ngc.Id, night, Utc("2026-10-25T20:00:00Z"));
            Check(bad.Result.Outcome == PlacementOutcome.Rejected, "NGC 7293 never clears its 30° default from London");
            engine.SetConditions(ngc.Id, new[] { ConditionSpec.AboveHorizon(5) });
            var ok = engine.PreviewNew(ngc.Id, night, Utc("2026-10-25T22:00:00Z"));
            Check(ok.Result.Outcome == PlacementOutcome.Clipped && ok.Result.Placed!.Value.StartUtc == ev.EndUtc,
                  $"after loosening: starts where M31 ends and is clipped by setting: {ok.Result.Message}");

            // Planned time never goes past what the sequence still needs.
            var extra = engine.PreviewNew(m31.Id, night, Utc("2026-10-26T03:30:00Z"), TimeSpan.FromHours(3));
            Check(extra.Result.Outcome == PlacementOutcome.Rejected && extra.Result.Message.Contains("fully planned"), extra.Result.Message);
            var grow = engine.PreviewResize(ev.Id, ev.EndUtc.AddHours(2));
            Check(grow.Result.Placed!.Value.EndUtc == ev.EndUtc && grow.Result.Message.Contains("plan is full"), $"a full target's event can't grow: {grow.Result.Message}");

            var moved = engine.PreviewMove(ev.Id, night.AddDays(2), Utc("2026-10-27T21:00:00Z"));
            Check(moved.Result.Outcome == PlacementOutcome.Fits, moved.Result.Message);
            engine.Commit(moved);
            Check(engine.GetEvent(ev.Id)!.Night == night.AddDays(2), "event moved to another night");

            var resized = engine.PreviewResize(ev.Id, Utc("2026-10-27T22:07:00Z"));
            engine.Commit(resized);
            Check(engine.GetEvent(ev.Id)!.EndUtc == Utc("2026-10-27T22:05:00Z"), "resize snaps the end");
            var freed = engine.GetTarget(m31.Id)!.Unscheduled;
            Check(freed > TimeSpan.Zero, "shortening returns time to unscheduled");
            var capped = engine.PreviewNew(m31.Id, night.AddDays(3), Utc("2026-10-28T20:00:00Z"), TimeSpan.FromHours(4));
            Check(capped.Result.Placed!.Value.Duration == freed && capped.Result.Message.Contains("all this target still needs"),
                  $"a fixed drop size is cut to what is left: {capped.Result.Placed!.Value.Duration} vs {freed}");
            var regrow = engine.PreviewResize(ev.Id, Utc("2026-10-28T03:00:00Z"));
            Check(regrow.Result.Placed!.Value.Duration <= TimeSpan.FromMinutes(65) + freed, "growing back stops at the budget");
            Check(engine.SplitEvent(ev.Id) && engine.Events().Count(e => e.TargetId == m31.Id) == 2, "split in two");

            var past = engine.PreviewNew(m31.Id, night.AddDays(-1), null);
            Check(past.Result.Outcome == PlacementOutcome.Rejected, "cannot plan a past night");

            // Live frames: 13 more Ha subs finish the Ha line.
            for (int i = 0; i < 13; i++) {
                engine.RecordCapture(new CaptureRecord {
                    TimeUtc = Utc("2026-10-27T21:00:00Z").AddSeconds(i * 310), ExposureSeconds = 300,
                    TargetName = "M31", RaHours = M31.Ra, DecDegrees = M31.Dec, Filter = "Ha"
                });
            }
            var after = engine.GetTarget(m31.Id)!;
            Check(after.LiveFrames == 13 && after.Lines.Single(l => l.Filter == "Ha").Done == 20, "live frames counted");
            var part = engine.Milestones().Single(m => m.Kind == MilestoneKind.PartComplete && m.Source == MilestoneSource.LiveCapture);
            Check(part.Source == MilestoneSource.LiveCapture && part.Filter == "Ha" && part.TimeUtc == Utc("2026-10-27T21:00:00Z").AddSeconds(12 * 310),
                  $"Ha completion recorded at the last frame: {part.Describe()} {part.TimeUtc:u}");

            var snap = engine.BuildSnapshot(BoardViewMode.Week, night, 7, m31.Id);
            Check(snap.Nights.Count == 7 && snap.Events.Count == 2 && snap.Sessions.Count == 1 && snap.Milestones.Count == 1, "week snapshot contents");
            Check(snap.Sessions[0].Frames == 13 && snap.Sessions[0].Night == night.AddDays(2), "session on the right night");
            Check(snap.AxisStartHours == 5 && (snap.AxisEndHours == 18 || snap.AxisEndHours == 19), $"axis {snap.AxisStartHours}-{snap.AxisEndHours}");
            Check(snap.ActiveWindows.Count == 7 && snap.ActiveAltitude.Count == 7, "active target overlays");
            var month = engine.BuildSnapshot(BoardViewMode.Month, new DateOnly(2026, 10, 1), 31, null);
            Check(month.Nights.Count == 31 && month.ActiveAltitude.Count == 0, "month snapshot");

            // Everything survives a restart.
            var reloaded = new BoardEngine(new BoardStore(folder), new BuiltInSkyContext(London, LondonZone), clock);
            reloaded.Load();
            reloaded.ApplyScan(found, report);
            Check(reloaded.Events().Count == engine.Events().Count, "events persisted");
            Check(reloaded.Captures().Count == 13 && reloaded.GetTarget(m31.Id)!.LiveFrames == 13, "capture log persisted");
            Check(reloaded.Milestones().Count == 2, "milestones persisted and not duplicated");
            Check(reloaded.GetTarget(ngc.Id)!.Conditions.Single().Kind == ConditionKind.AboveHorizon, "edited conditions persisted");
            Check(File.Exists(Path.Combine(folder, "board.json.bak")), "backup kept");
            Directory.Delete(folder, true);
        }

        private static void MilestonesFromResavedSequenceFile() {
            var (engine, folder, _, setClock) = NewEngine();
            var seqFolder = Path.Combine(folder, "seq");
            Directory.CreateDirectory(seqFolder);
            var path = Path.Combine(seqFolder, "Autumn Galaxies.json");
            File.Copy(Fixture("Autumn Galaxies.json"), path);
            File.SetLastWriteTimeUtc(path, Utc("2026-10-20T06:00:00Z"));
            var (found, report) = SequenceScanner.Scan(new[] { seqFolder }, Array.Empty<string>(), false);
            engine.ApplyScan(found, report);
            var id = found.Single(t => t.Name == "M 31").Id;
            Check(engine.Milestones().Count(m => m.Kind == MilestoneKind.PartComplete && m.Source == MilestoneSource.FirstScan) == 1, "already-finished line noted on first scan");

            // NINA ran the rest of M31 and saved the file.
            var text = File.ReadAllText(path)
                .Replace(@"""CompletedIterations"": 7, ""Iterations"": 20", @"""CompletedIterations"": 20, ""Iterations"": 20")
                .Replace(@"""CompletedIterations"": 0, ""Iterations"": 20", @"""CompletedIterations"": 20, ""Iterations"": 20")
                .Replace(@"""CompletedIterations"": 1, ""Iterations"": 3", @"""CompletedIterations"": 3, ""Iterations"": 3");
            File.WriteAllText(path, text);
            var saved = Utc("2026-10-26T05:10:00Z");
            File.SetLastWriteTimeUtc(path, saved);
            setClock(Utc("2026-10-26T09:00:00Z"));
            (found, report) = SequenceScanner.Scan(new[] { seqFolder }, Array.Empty<string>(), false);
            engine.ApplyScan(found, report);

            var ms = engine.Milestones().Where(m => m.TargetId == id && m.TimeUtc == saved).ToList();
            Check(ms.Count(m => m.Kind == MilestoneKind.Progress) == 3, $"progress on Ha, OIII, R: {ms.Count(m => m.Kind == MilestoneKind.Progress)}");
            Check(ms.Count(m => m.Kind == MilestoneKind.PartComplete) == 3, "three lines finished");
            Check(ms.Count(m => m.Kind == MilestoneKind.TargetComplete) == 1, "whole target finished");
            Check(engine.GetTarget(id)!.IsComplete && engine.GetTarget(id)!.CompletedUtc == saved, "completion date stored");

            // The user resets progress in NINA: the target reopens.
            File.Copy(Fixture("Autumn Galaxies.json"), path, true);
            File.SetLastWriteTimeUtc(path, Utc("2026-10-27T12:00:00Z"));
            (found, report) = SequenceScanner.Scan(new[] { seqFolder }, Array.Empty<string>(), false);
            engine.ApplyScan(found, report);
            Check(engine.Milestones().Any(m => m.Kind == MilestoneKind.Reopened) && !engine.GetTarget(id)!.IsComplete, "reopened");
            Directory.Delete(folder, true);
        }

        private static void StoreRecoversFromBadFile() {
            var folder = Path.Combine(Path.GetTempPath(), "tb-store-" + Guid.NewGuid().ToString("N"));
            var store = new BoardStore(folder);
            var data = new BoardData();
            data.Settings.OverheadPercent = 7;
            store.SaveBoard(data);
            data.Settings.OverheadPercent = 9;
            store.SaveBoard(data);
            File.WriteAllText(store.BoardPath, "{ this is not json");
            var loaded = store.LoadBoard();
            Check(loaded.Settings.OverheadPercent == 7, $"fell back to backup, got {loaded.Settings.OverheadPercent}");
            Check(Directory.GetFiles(folder, "board.json.unreadable-*").Length == 1, "bad file kept for inspection");

            File.AppendAllText(store.CapturesPath, "{\"timeUtc\":\"2026-10-25T20:00:00Z\",\"exposureSeconds\":60}\n{\"timeUtc\":\"2026-10-");
            Check(store.LoadCaptures().Count == 1, "half-written capture line skipped");
            Directory.Delete(folder, true);
        }

        private static void ScannerSkipsTemplatesAndOtherJson() {
            var folder = Path.Combine(Path.GetTempPath(), "tb-scan-" + Guid.NewGuid().ToString("N"));
            var sub = Path.Combine(folder, "Targets");
            var own = Path.Combine(folder, "TargetBoard");
            Directory.CreateDirectory(sub);
            Directory.CreateDirectory(own);
            File.Copy(Fixture("Autumn Galaxies.json"), Path.Combine(sub, "a.json"));
            File.Copy(Fixture("Autumn Galaxies.json"), Path.Combine(folder, "b.template.json"));
            File.Copy(Fixture("Autumn Galaxies.json"), Path.Combine(own, "c.json"));
            File.WriteAllText(Path.Combine(folder, "settings.json"), "{\"unrelated\": true}");
            File.WriteAllText(Path.Combine(folder, "broken.json"), "{\"$type\": \"NINA.Sequencer.Container.SequenceRootContainer\", ");
            var (targets, report) = SequenceScanner.Scan(new[] { folder, sub }, Array.Empty<string>(), true, new[] { own });
            Check(targets.Count == 2, $"one file read once (templates, own folder and duplicates skipped), got {targets.Count} targets");
            Check(report.Errors.Count == 1 && report.Errors[0].StartsWith("broken.json"), $"broken file reported: {string.Join("; ", report.Errors)}");
            Directory.Delete(folder, true);
        }

        private static void NothingIsWrittenBeforeLoad() {
            var folder = Path.Combine(Path.GetTempPath(), "tb-early-" + Guid.NewGuid().ToString("N"));
            var store = new BoardStore(folder);
            var saved = new BoardData();
            saved.Events.Add(new PlanEvent { Id = "keep", TargetId = "t", Night = new DateOnly(2026, 10, 25), StartUtc = Utc("2026-10-25T20:00:00Z"), EndUtc = Utc("2026-10-25T21:00:00Z") });
            store.SaveBoard(saved);

            var engine = new BoardEngine(store, new BuiltInSkyContext(London, LondonZone));
            engine.UpdateSettings(s => s.LastView = BoardViewMode.Month);
            Check(engine.RecordCapture(new CaptureRecord { TimeUtc = DateTime.UtcNow, ExposureSeconds = 60, TargetName = "x" }) == null, "frames before load are not logged");
            engine.Load();
            Check(engine.Events().Single().Id == "keep", "the saved plan survived a settings change made before loading");
            Directory.Delete(folder, true);
        }

        private const string SimpleRow = @"
  <CaptureSequence>
    <Enabled>{0}</Enabled>
    <ExposureTime>{1}</ExposureTime>
    <ImageType>{2}</ImageType>
    {3}
    <Gain>-1</Gain>
    <TotalExposureCount>{4}</TotalExposureCount>
    <ProgressExposureCount>{5}</ProgressExposureCount>
  </CaptureSequence>";

        private static string Row(bool enabled, double exp, string type, string? filter, int total, int done)
            => string.Format(CultureInfo.InvariantCulture, SimpleRow, enabled ? "true" : "false", exp, type,
                             filter == null ? "" : $"<FilterType><Name>{filter}</Name><Position>1</Position></FilterType>", total, done);

        private static void ReadsSimpleSequencerFiles() {
            string single = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<CaptureSequenceList xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" TargetName=""Test Nebula"" Mode=""STANDARD""
    RAHours=""5"" RAMinutes=""35"" RASeconds=""17.3"" DecDegrees=""-5"" DecMinutes=""23"" DecSeconds=""28"" PositionAngle=""12.5"">
{Row(true, 300, "LIGHT", "Ha", 20, 7)}
{Row(true, 300, "LIGHT", "Ha", 10, 10)}
{Row(true, 120, "LIGHT", null, 30, 45)}
{Row(false, 60, "LIGHT", "L", 99, 0)}
{Row(true, 2, "FLAT", "L", 30, 0)}
  <NegativeDec>true</NegativeDec>
</CaptureSequenceList>";
            var t = SimpleSequenceReader.ReadXml(single, "/tmp/test.xml", Utc("2026-10-20T06:00:00Z")).Single();
            Check(t.Name == "Test Nebula", t.Name);
            Near(t.RaHours, 5 + 35 / 60.0 + 17.3 / 3600, 1e-9, "RA from attributes");
            Near(t.DecDegrees, -(5 + 23 / 60.0 + 28 / 3600.0), 1e-9, "negative Dec from attributes");
            Near(t.PositionAngle, 12.5, 1e-9, "position angle");
            Check(t.Lines.Count == 2, $"disabled and flat rows skipped, two lines left: {t.Lines.Count}");
            var ha = t.Lines.Single(l => l.Filter == "Ha");
            Check(ha.Planned == 30 && ha.Done == 17, $"rows with the same filter and exposure add up: {ha.Done}/{ha.Planned}");
            var nf = t.Lines.Single(l => l.Filter == SequenceReader.NoFilter);
            Check(nf.Planned == 30 && nf.Done == 30, "progress never exceeds the total");

            string set = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<ArrayOfCaptureSequenceList xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"">
  <CaptureSequenceList TargetName=""Panel 1"" Mode=""ROTATE"" RAHours=""0"" RAMinutes=""0"" RASeconds=""0"" DecDegrees=""0"" DecMinutes=""0"" DecSeconds=""0"">
{Row(true, 300, "LIGHT", "O", 48, 48)}
    <Coordinates><RA>20.9346</RA><Dec>31.7712</Dec><Epoch>J2000</Epoch></Coordinates>
  </CaptureSequenceList>
  <CaptureSequenceList TargetName="""" Mode=""STANDARD"" RAHours=""20"" RAMinutes=""56"" RASeconds=""4"" DecDegrees=""31"" DecMinutes=""0"" DecSeconds=""19"">
{Row(true, 300, "LIGHT", "O", 48, 0)}
  </CaptureSequenceList>
</ArrayOfCaptureSequenceList>";
            var both = SimpleSequenceReader.ReadXml(set, "/tmp/Veil.ninaTargetSet", Utc("2026-10-20T06:00:00Z"));
            Check(both.Count == 2, "target set gives one target per list");
            Near(both[0].RaHours, 20.9346, 1e-9, "decimal RA preferred");
            Near(both[0].DecDegrees, 31.7712, 1e-9, "decimal Dec preferred");
            Check(both[0].IsComplete && !both[1].IsComplete, "progress per panel");
            Check(both[1].Name == "Veil #2", $"unnamed panel named after the file: {both[1].Name}");
            Check(both[0].Id != both[1].Id, "distinct ids");

            // The scanner finds .xml and .ninaTargetSet files and ignores unrelated XML.
            var folder = Path.Combine(Path.GetTempPath(), "tb-simple-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Test Nebula.xml"), single);
            File.WriteAllText(Path.Combine(folder, "Veil.ninaTargetSet"), set);
            File.WriteAllText(Path.Combine(folder, "other.xml"), "<?xml version=\"1.0\"?><settings><a>1</a></settings>");
            var (targets, report) = SequenceScanner.Scan(new[] { folder }, Array.Empty<string>(), true);
            Check(report.FilesRead == 2 && targets.Count == 3 && report.Errors.Count == 0, $"scan: {report.FilesRead} files, {targets.Count} targets, {report.Errors.Count} errors");
            Directory.Delete(folder, true);
        }

        private static void MarkedDoneStopsPlanningAndIsRecorded() {
            var (engine, folder, _, setClock) = NewEngine();
            var (found, report) = SequenceScanner.Scan(new[] { Path.Combine(AppContext.BaseDirectory, "Fixtures") }, Array.Empty<string>(), true);
            engine.ApplyScan(found, report);
            var m31 = engine.GetTargets().Single(t => t.Name == "M 31");
            var night = new DateOnly(2026, 10, 25);
            var ev = engine.Commit(engine.PreviewNew(m31.Id, night, Utc("2026-10-25T19:00:00Z"), TimeSpan.FromHours(1)))!;
            var fileText = File.ReadAllText(Fixture("Autumn Galaxies.json"));

            setClock(Utc("2026-10-25T16:00:00Z"));
            Check(engine.SetMarkedDone(new[] { m31.Id }, true) == 1, "marked one target");
            Check(engine.SetMarkedDone(new[] { m31.Id }, true) == 0, "marking twice changes nothing");
            var done = engine.GetTarget(m31.Id)!;
            Check(done.IsComplete && done.MarkedDone && done.CompletedUtc == Utc("2026-10-25T16:00:00Z"), "counts as complete, dated when marked");
            Check(done.Progress < 1 && done.RemainingSeconds > 0, "real progress is still shown");
            Check(done.Unscheduled == TimeSpan.Zero && done.OverPlanned == TimeSpan.Zero, "nothing to place and no over-planned warning");
            Check(engine.GetEvent(ev.Id) != null, "its planned event stays on the calendar");
            var refused = engine.PreviewNew(m31.Id, night, Utc("2026-10-25T22:00:00Z"));
            Check(refused.Result.Outcome == PlacementOutcome.Rejected && refused.Result.Message.Contains("marked done"), refused.Result.Message);
            var mark = engine.Milestones().Last(m => m.TargetId == m31.Id);
            Check(mark.Kind == MilestoneKind.TargetComplete && mark.Source == MilestoneSource.Manual && mark.Describe() == "M 31 marked done", mark.Describe());
            Check(File.ReadAllText(Fixture("Autumn Galaxies.json")) == fileText, "the sequence file is not touched");

            // Survives a restart.
            var again = new BoardEngine(new BoardStore(folder), new BuiltInSkyContext(London, LondonZone), () => Utc("2026-10-25T17:00:00Z"));
            again.Load();
            again.ApplyScan(found, report);
            Check(again.GetTarget(m31.Id)!.MarkedDone, "marked done is saved");
            Check(again.Milestones().Any(m => m.Source == MilestoneSource.Manual), "the manual milestone is saved");

            setClock(Utc("2026-10-25T16:30:00Z"));
            Check(engine.SetMarkedDone(new[] { m31.Id }, false) == 1, "unmarked");
            var open = engine.GetTarget(m31.Id)!;
            Check(!open.IsComplete && !open.MarkedDone && open.Unscheduled > TimeSpan.Zero, "plannable again");
            Check(engine.Milestones().Last(m => m.TargetId == m31.Id).Describe() == "M 31 marked not done", "unmarking is recorded too");
        }

        // ------------------------------------------------------------------ 1.1: moon, padding, forcing, outcomes, settle, shift

        private static void MoonMatchesAstropy() {
            // Reference: astropy get_body("moon"), topocentric, no refraction.
            var cases = new (Observer O, string T, double Alt)[] {
                (London, "2026-10-25T20:00:00Z", 35.1106), (London, "2026-10-26T02:30:00Z", 39.7600), (London, "2027-01-15T18:00:00Z", 51.4405),
                (London, "2026-11-10T04:00:00Z", -41.0752), (Sydney, "2026-11-03T22:00:00Z", 47.3130), (Sydney, "2026-11-10T04:00:00Z", 64.8267),
                (MaunaKea, "2026-10-25T08:00:00Z", 69.4001), (MaunaKea, "2026-07-02T09:00:00Z", 25.8907), (MaunaKea, "2027-01-15T06:00:00Z", 55.0023)
            };
            foreach (var (o, t, alt) in cases) Near(SkyMath.MoonAltitude(Utc(t), o), alt, 0.3, $"Moon altitude at {t}");
        }

        private static void FramePlannerFollowsShootingOrder() {
            var lines = new List<ExposureLine> {
                new() { Filter = "Ha", ExposureSeconds = 300, Planned = 10, Done = 6 },
                new() { Filter = "OIII", ExposureSeconds = 300, Planned = 4, Done = 0 }
            };
            var t0 = Utc("2026-10-26T20:00:00Z");
            var slots = new[] { new FrameSlot("a", t0, t0.AddMinutes(30)), new FrameSlot("b", t0.AddDays(1), t0.AddDays(1).AddMinutes(30)) };
            var rows = FramePlanner.Forecast(lines, ShootingOrder.RowByRow, 0, slots);
            Check(rows["a"].Count == 2 && rows["a"][0].Filter == "Ha" && rows["a"][0].Frames == 4 && rows["a"][1].Frames == 2, "row by row: Ha x4 then OIII x2");
            Check(rows["b"].Count == 1 && rows["b"][0].Filter == "OIII" && rows["b"][0].Frames == 2, "the next event takes what is left");
            var rot = FramePlanner.Forecast(lines, ShootingOrder.Rotate, 0, slots);
            Check(rot["a"].Count == 6 && rot["a"].Select(s => s.Filter).SequenceEqual(new[] { "Ha", "OIII", "Ha", "OIII", "Ha", "OIII" }), "rotate alternates rows");
            var withOverhead = FramePlanner.Forecast(lines, ShootingOrder.RowByRow, 20, slots);
            Check(withOverhead["a"].Sum(s => s.Frames) == 5, "with 20% overhead a 30 min slot holds 5 frames of 300 s");
        }

        private static void OutcomesFromTheFrameLog() {
            var zone = LondonZone;
            var night = new DateOnly(2026, 10, 25);
            PlanEvent Ev(string id, string start, string end) => new() { Id = id, TargetId = "t", Night = night, StartUtc = Utc(start), EndUtc = Utc(end) };
            var done = Ev("done", "2026-10-25T19:00:00Z", "2026-10-25T20:00:00Z");
            var part = Ev("part", "2026-10-25T20:00:00Z", "2026-10-25T21:00:00Z");
            var miss = Ev("miss", "2026-10-25T21:00:00Z", "2026-10-25T22:00:00Z");
            var run = Ev("run", "2026-10-25T22:00:00Z", "2026-10-25T23:00:00Z");
            var later = Ev("later", "2026-10-26T01:00:00Z", "2026-10-26T02:00:00Z");
            var caps = new List<CaptureRecord>();
            for (int i = 0; i < 12; i++) caps.Add(new CaptureRecord { TargetId = "t", TimeUtc = Utc("2026-10-25T19:00:00Z").AddMinutes(5 * i), ExposureSeconds = 300, Filter = "Ha" });
            for (int i = 0; i < 5; i++) caps.Add(new CaptureRecord { TargetId = "t", TimeUtc = Utc("2026-10-25T20:10:00Z").AddMinutes(5 * i), ExposureSeconds = 300, Filter = "OIII" });
            caps.Add(new CaptureRecord { TargetId = "t", TimeUtc = Utc("2026-10-25T22:05:00Z"), ExposureSeconds = 300, Filter = "Ha" });
            var now = Utc("2026-10-25T22:30:00Z");
            var r = OutcomeTracker.Evaluate(new[] { done, part, miss, run, later }, caps, 0, now, zone, logging: true);
            Check(r["done"].Status == EventStatus.Done && r["done"].Frames == 12, $"done: {r["done"].Status}");
            Check(r["part"].Status == EventStatus.Partial && r["part"].Frames == 5, $"partly done: {r["part"].Status}");
            Check(r["miss"].Status == EventStatus.Missed, "missed");
            Check(r["run"].Status == EventStatus.Running && r["run"].Frames == 1, "running");
            Check(r["later"].Status == EventStatus.Upcoming, "upcoming");
            var off = OutcomeTracker.Evaluate(new[] { miss }, Array.Empty<CaptureRecord>(), 0, now, zone, logging: false);
            Check(off["miss"].Status == EventStatus.Unknown, "with the frame log off a past event is unknown, not missed");
        }

        private static void ReflowPushesAndCascades() {
            var night = new DateOnly(2026, 10, 26);
            DateTime At(DateOnly n, int hour) => n.ToDateTime(new TimeOnly(0, 0), DateTimeKind.Utc).AddHours(hour);
            IReadOnlyList<TimeRange> Windows(string target, DateOnly n) => new[] { new TimeRange(At(n, 19), At(n, 23)) };
            var items = new[] {
                new ReflowItem { Id = "a", TargetId = "x", Night = night, DesiredStartUtc = At(night, 19), Length = TimeSpan.FromHours(2) },
                new ReflowItem { Id = "b", TargetId = "y", Night = night, DesiredStartUtc = At(night, 20), Length = TimeSpan.FromHours(1) },
                new ReflowItem { Id = "c", TargetId = "z", Night = night, DesiredStartUtc = At(night, 21), Length = TimeSpan.FromHours(2) },
                new ReflowItem { Id = "d", TargetId = "w", Night = night.AddDays(1), DesiredStartUtc = At(night.AddDays(1), 19), Length = TimeSpan.FromMinutes(45) }
            };
            // A fixed (forced) block on the second night from 20:00 to 21:00.
            IEnumerable<TimeRange> Fixed(DateOnly n) => n == night.AddDays(1) ? new[] { new TimeRange(At(n, 20), At(n, 21)) } : Array.Empty<TimeRange>();
            var r = Reflow.Run(items, Windows, Fixed, new ReflowOptions());
            var p = r.Placed.ToDictionary(x => x.Id);
            Check(!p["a"].Moved && p["a"].Block.StartUtc == At(night, 19), "a is where it was");
            Check(p["b"].Moved && p["b"].Block.StartUtc == At(night, 21), $"b is pushed to after a: {p["b"].Block.StartUtc:t}");
            Check(p["c"].Night == night && p["c"].Block.StartUtc == At(night, 22) && p["c"].Overflow == TimeSpan.FromHours(1),
                  $"c keeps its night, shortened to the hour left: {p["c"].Night} {p["c"].Block.StartUtc:t} overflow {p["c"].Overflow}");

            // With a 90 minute minimum, the hour left is too little: c goes to the next night instead.
            var r2 = Reflow.Run(items, Windows, Fixed, new ReflowOptions { MinimumLength = TimeSpan.FromMinutes(90) });
            var q = r2.Placed.ToDictionary(x => x.Id);
            Check(q["c"].Night == night.AddDays(1) && q["c"].Block.StartUtc == At(night.AddDays(1), 21) && q["c"].Overflow == TimeSpan.Zero,
                  $"c takes the first slot that holds 2 h on the next night, after the fixed block: {q["c"].Night} {q["c"].Block.StartUtc:g}");
            Check(q["d"].Night == night.AddDays(1) && q["d"].Block.StartUtc == At(night.AddDays(1), 19) && !q["d"].Moved,
                  $"d stays where it is, before the fixed block: {q["d"].Night} {q["d"].Block.StartUtc:t}");
            var tooBig = Reflow.Run(new[] { new ReflowItem { Id = "e", TargetId = "x", Night = night, DesiredStartUtc = At(night, 19), Length = TimeSpan.FromHours(5) } },
                                    Windows, _ => Array.Empty<TimeRange>(), new ReflowOptions { HorizonNights = 3 });
            Check(tooBig.Placed.Single().Overflow == TimeSpan.FromHours(1), "an event longer than its window is shortened to fit, never split");
            var noRoom = Reflow.Run(new[] { new ReflowItem { Id = "f", TargetId = "x", Night = night, DesiredStartUtc = At(night, 19), Length = TimeSpan.FromHours(1) } },
                                    Windows, n => new[] { new TimeRange(At(n, 18), At(n, 23)) }, new ReflowOptions { HorizonNights = 3 });
            Check(noRoom.Unplaced.SequenceEqual(new[] { "f" }), "with no room on any night within the horizon it is given up");
        }

        private static (BoardEngine Engine, string Folder, Action<DateTime> SetClock, TargetSummary M31, TargetSummary Ngc) FixtureEngine() {
            var (engine, folder, _, setClock) = NewEngine();
            var (found, report) = SequenceScanner.Scan(new[] { Path.Combine(AppContext.BaseDirectory, "Fixtures") }, Array.Empty<string>(), true);
            engine.ApplyScan(found, report);
            var m31 = engine.GetTargets().Single(t => t.Name == "M 31");
            var ngc = engine.GetTargets().Single(t => t.Name == "NGC 7293");
            return (engine, folder, setClock, m31, ngc);
        }

        private static void NoOverlaps(BoardEngine engine, string when) {
            foreach (var night in engine.Events().Where(e => !e.IsSettled).GroupBy(e => e.Night)) {
                var list = night.OrderBy(e => e.StartUtc).ToList();
                for (int i = 1; i < list.Count; i++) Check(list[i].StartUtc >= list[i - 1].EndUtc, $"{when}: events overlap on {night.Key}");
            }
        }

        private static void ForcedPlacement() {
            var (engine, _, _, m31, ngc) = FixtureEngine();
            var night = new DateOnly(2026, 10, 25);
            var p = engine.PreviewNew(m31.Id, night, Utc("2026-10-25T19:02:00Z"), TimeSpan.FromHours(1));
            Check(p.Result.CanCommit && p.Result.Placed!.Value.StartUtc == Utc("2026-10-25T19:00:00Z") && p.Result.Placed!.Value.Duration == TimeSpan.FromHours(1),
                  $"a 1 h block starts where dropped: {p.Result.Placed}");
            var ev = engine.Commit(p)!;
            Check(engine.GetTarget(m31.Id)!.ScheduledAhead == TimeSpan.FromHours(1), "the whole block counts against the budget");
            var next = engine.PreviewNew(m31.Id, night, Utc("2026-10-25T20:00:00Z"), TimeSpan.FromHours(1));
            Check(next.Result.Placed!.Value.StartUtc >= ev.EndUtc, "the next block keeps clear of the first");

            var blocked = engine.PreviewNew(ngc.Id, night, Utc("2026-10-25T21:30:00Z"), TimeSpan.FromHours(1));
            Check(blocked.Result.Outcome == PlacementOutcome.Rejected && blocked.Result.Message.Contains("Hold Shift"), $"without Shift: {blocked.Result.Message}");
            var forced = engine.PreviewNew(ngc.Id, night, Utc("2026-10-25T21:30:00Z"), TimeSpan.FromHours(1), force: true);
            Check(forced.Result.CanCommit && forced.Forced && forced.OutsideConditions > TimeSpan.Zero, $"with Shift: {forced.Result.Message}");
            var fe = engine.Commit(forced)!;
            Check(fe.Forced, "the event remembers it was forced");
            var view = engine.BuildSnapshot(BoardViewMode.Night, night, 1, null).Events.Single(e => e.Id == fe.Id);
            Check(view.OutsideConditions > TimeSpan.Zero, "the calendar is told how much is outside the conditions");
            var low = engine.PreviewNew(ngc.Id, night, Utc("2026-10-26T04:00:00Z"), TimeSpan.FromHours(1), force: true);
            Check(low.Result.Placed == null || low.Result.Placed.Value.EndUtc <= Utc("2026-10-26T01:00:00Z"), "Shift never places a target below the horizon");
        }

        private static void SettleCarriesAndCascades() {
            var (engine, _, setClock, m31, ngc) = FixtureEngine();
            var n1 = new DateOnly(2026, 10, 25);
            var n2 = n1.AddDays(1);
            engine.SetConditions(ngc.Id, new[] { ConditionSpec.AboveHorizon(5) });
            Check(engine.AutoSettleIfDue() == null, "the first run only marks where settling starts");

            var a = engine.Commit(engine.PreviewNew(m31.Id, n1, Utc("2026-10-25T19:00:00Z"), TimeSpan.FromHours(2)))!;
            var miss = engine.Commit(engine.PreviewNew(ngc.Id, n1, null, TimeSpan.FromMinutes(30)))!;
            var b = engine.Commit(engine.PreviewNew(m31.Id, n2, Utc("2026-10-26T19:00:00Z"), TimeSpan.FromHours(1)))!;
            var c = engine.Commit(engine.PreviewNew(ngc.Id, n2, b.EndUtc, TimeSpan.FromMinutes(30)))!;
            Check(c.StartUtc == b.EndUtc, $"NGC 7293 sits right after M 31 on the second night ({c.StartUtc:t} vs {b.EndUtc:t})");

            // 10 Ha frames on the first night: about 58 of the 120 minutes.
            setClock(Utc("2026-10-25T19:10:00Z"));
            for (int i = 0; i < 10; i++) {
                engine.RecordCapture(new CaptureRecord { TimeUtc = Utc("2026-10-25T19:05:00Z").AddMinutes(6 * i), ExposureSeconds = 300, TargetName = "M 31", RaHours = 0.712, DecDegrees = 41.27, Filter = "Ha" });
            }
            setClock(Utc("2026-10-26T07:30:00Z"));
            var early = engine.AutoSettleIfDue();
            Check(early == null && !engine.GetEvent(a.Id)!.IsSettled, "nothing changes before the settle time");
            var shown = engine.BuildSnapshot(BoardViewMode.Week, n1, 2, null);
            Check(shown.Events.Single(e => e.Id == a.Id).Status == EventStatus.Partial && shown.Events.Single(e => e.Id == miss.Id).Status == EventStatus.Missed,
                  "statuses show before settling");
            Check(shown.Nights[0].Missed == 1 && shown.Nights[0].CanSettle, "the night header knows about the missed event");

            setClock(Utc("2026-10-26T08:05:00Z"));
            var report = engine.AutoSettleIfDue();
            Check(report != null && report.Changed > 0, "the morning settle runs after 08:00");
            var settled = engine.GetEvent(a.Id)!;
            Check(settled.Outcome == EventOutcome.Partial && settled.Frames == 10, $"partly done: {settled.Outcome} {settled.Frames}");
            Check(settled.StartUtc == Utc("2026-10-25T19:05:00Z") && settled.EndUtc == Utc("2026-10-25T20:04:00Z"), $"the block shrinks to when frames were taken: {settled.StartUtc:t}-{settled.EndUtc:t}");
            Check(settled.PlannedStartUtc == a.StartUtc && settled.PlannedEndUtc == a.EndUtc, "the planned block is kept");
            Check(engine.GetEvent(miss.Id)!.Outcome == EventOutcome.Missed, "missed is recorded");
            var grown = engine.GetEvent(b.Id)!;
            var shortfall = TimeSpan.FromHours(2) - TimeSpan.FromSeconds(10 * 300 * 1.15);
            Check(Math.Abs((grown.Duration - TimeSpan.FromHours(1) - shortfall).TotalMinutes) <= 1, $"the shortfall goes onto the next M 31 event: {grown.Duration}");
            var pushed = engine.GetEvent(c.Id)!;
            Check(pushed.StartUtc >= grown.EndUtc || pushed.Night != n2, "NGC 7293 is pushed out of the way");
            NoOverlaps(engine, "after settling");
            Check(engine.GetTarget(m31.Id)!.OverPlanned == TimeSpan.Zero, "carrying never over-plans");
            Check(engine.AutoSettleIfDue() == null, "each night is settled once");

            Check(engine.Undo() == "the morning settle", "the settle can be undone");
            Check(!engine.GetEvent(a.Id)!.IsSettled && engine.GetEvent(b.Id)!.Duration == TimeSpan.FromHours(1), "undo puts the plan back");
            Check(engine.AutoSettleIfDue() == null, "an undone settle stays undone");
            var manual = engine.SettleNight(n1);
            Check(manual.Changed > 0 && engine.GetEvent(a.Id)!.Outcome == EventOutcome.Partial, "a night can be settled by hand");
        }

        private static void ShiftPlanAfterAMissedNight() {
            var (engine, _, setClock, m31, ngc) = FixtureEngine();
            var n1 = new DateOnly(2026, 10, 25);
            engine.SetConditions(ngc.Id, new[] { ConditionSpec.AboveHorizon(5) });
            var a = engine.Commit(engine.PreviewNew(m31.Id, n1, Utc("2026-10-25T19:00:00Z"), TimeSpan.FromHours(1)))!;
            var b = engine.Commit(engine.PreviewNew(m31.Id, n1.AddDays(1), Utc("2026-10-26T19:00:00Z"), TimeSpan.FromHours(1)))!;
            var c = engine.Commit(engine.PreviewNew(ngc.Id, n1.AddDays(1), b.EndUtc, TimeSpan.FromMinutes(30)))!;
            setClock(Utc("2026-10-26T09:00:00Z"));
            var r = engine.ShiftPlanFrom(n1);
            Check(r.Changed >= 3, $"shift changed {r.Changed}: {r}");
            Check(engine.GetEvent(a.Id)!.Outcome == EventOutcome.Missed, "the missed event stays as a record");
            var copy = engine.Events().Single(e => e.TargetId == m31.Id && !e.IsSettled && e.Night == n1.AddDays(1));
            Check(copy.Duration == TimeSpan.FromHours(1) && NightMath.ToLocal(copy.StartUtc, LondonZone).Hour == 19, $"its time is planned again on the next night at the same clock time: {copy.StartUtc:g}");
            Check(engine.GetEvent(b.Id)!.Night == n1.AddDays(2) && engine.GetEvent(c.Id)!.Night == n1.AddDays(2), "the later events move one night on");
            NoOverlaps(engine, "after shifting");
            engine.Undo();
            Check(engine.GetEvent(a.Id)!.Outcome == null && engine.GetEvent(b.Id)!.Night == n1.AddDays(1) && engine.Events().Count == 3, "undo puts it all back");

            var add = engine.AddMissedTime(new[] { a.Id });
            Check(engine.GetEvent(a.Id)!.Outcome == EventOutcome.Missed && engine.GetEvent(b.Id)!.Duration == TimeSpan.FromHours(2), $"missed time grows the next event: {add}");
            NoOverlaps(engine, "after adding missed time");
            var frames = engine.FramePlan(b.Id)!;
            Check(frames.Planned.Sum(s => s.Frames) > 0 && frames.Taken.Count == 0, "the filter view forecasts the frames of an upcoming event");
        }

        private static void MovesSeveralEventsTogether() {
            var (engine, _, setClock, m31, ngc) = FixtureEngine();
            var n1 = new DateOnly(2026, 10, 25);
            var n2 = n1.AddDays(1);
            engine.SetConditions(ngc.Id, new[] { ConditionSpec.AboveHorizon(5) });
            var a = engine.Commit(engine.PreviewNew(m31.Id, n1, Utc("2026-10-25T19:00:00Z"), TimeSpan.FromHours(1)))!;
            var b = engine.Commit(engine.PreviewNew(m31.Id, n2, Utc("2026-10-26T19:00:00Z"), TimeSpan.FromHours(1)))!;
            var c = engine.Commit(engine.PreviewNew(ngc.Id, n2, b.EndUtc, TimeSpan.FromMinutes(30)))!;
            var d = engine.Commit(engine.PreviewNew(m31.Id, n2.AddDays(1), Utc("2026-10-27T19:00:00Z"), TimeSpan.FromHours(1)))!;

            // Two nights on, where nothing is planned: both keep their times and the gap between them.
            var far = engine.PreviewMoveMany(new[] { b.Id, c.Id }, b.Id, n2.AddDays(2), NightMath.FromClockHoursAfterNoon(NightMath.ClockHoursAfterNoon(b.StartUtc, n2, LondonZone), n2.AddDays(2), LondonZone));
            Check(far.CanCommit && far.Outcome == PlacementOutcome.Fits && far.Items.Count == 2, far.Message);
            Check(engine.CommitMany(far) == 2, "both moved");
            var b2 = engine.GetEvent(b.Id)!;
            var c2 = engine.GetEvent(c.Id)!;
            Check(b2.Night == n2.AddDays(2) && c2.Night == n2.AddDays(2) && c2.StartUtc - b2.StartUtc == c.StartUtc - b.StartUtc, "same nights moved, same gap kept");
            Check(engine.GetEvent(d.Id)!.StartUtc == d.StartUtc, "an event that was not selected stays put");
            Check(engine.Undo() == "moving 2 events" && engine.GetEvent(b.Id)!.Night == n2 && engine.GetEvent(c.Id)!.Night == n2, "one undo puts both back");

            // One night on, onto an event that is not selected: it stays, and the moved ones find free time around it.
            var onto = engine.PreviewMoveMany(new[] { b.Id, c.Id }, b.Id, n2.AddDays(1), d.StartUtc);
            if (onto.CanCommit) engine.CommitMany(onto);
            Check(engine.GetEvent(d.Id)!.StartUtc == d.StartUtc && engine.GetEvent(d.Id)!.EndUtc == d.EndUtc, "the event in the way is never moved");
            NoOverlaps(engine, "after moving onto another event");
            engine.Undo();

            // The next morning: the first night's event was missed. Moving it with a later one plans it again.
            setClock(Utc("2026-10-26T09:00:00Z"));
            var withMissed = engine.PreviewMoveMany(new[] { a.Id, b.Id }, b.Id, n2.AddDays(1), null);
            Check(withMissed.CanCommit && withMissed.Items.Any(i => i.ReplanOfEventId == a.Id), withMissed.Message);
            engine.CommitMany(withMissed);
            Check(engine.GetEvent(a.Id)!.Outcome == EventOutcome.Missed, "the missed event stays as a record");
            Check(engine.Events().Any(e => e.TargetId == m31.Id && e.Night == n2 && !e.IsSettled && e.Id != b.Id), "its time is planned again a night later");
            Check(engine.GetEvent(b.Id)!.Night == n2.AddDays(1), "the other event moved a night too");
            NoOverlaps(engine, "after moving a missed event");
        }

        private static void LoadsBoardFromVersion110() {
            // 1.1.0.0 saved padding on targets and events; later versions ignore it and keep each block as it was.
            var folder = Path.Combine(Path.GetTempPath(), "tb-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "board.json"), @"{
  ""version"": 1,
  ""settings"": { ""overheadPercent"": 15 },
  ""targets"": { ""t1"": { ""id"": ""t1"", ""name"": ""M 31"", ""paddingMinutes"": 10, ""paddingSide"": ""Both"" } },
  ""events"": [ { ""id"": ""e1"", ""targetId"": ""t1"", ""night"": ""2026-10-27"", ""startUtc"": ""2026-10-27T19:00:00Z"", ""endUtc"": ""2026-10-27T21:00:00Z"",
                 ""padBeforeMinutes"": 10, ""padAfterMinutes"": 10 } ]
}");
            var store = new BoardStore(folder);
            var board = store.LoadBoard();
            Check(board.Events.Count == 1 && board.Events[0].Duration == TimeSpan.FromHours(2), "the event keeps its block");
            Check(board.Targets.ContainsKey("t1"), "the target loads");
        }

        private static void UndoAndLayoutSurviveARestart() {
            var (engine, folder, _, _) = NewEngine();
            var (found, report) = SequenceScanner.Scan(new[] { Path.Combine(AppContext.BaseDirectory, "Fixtures") }, Array.Empty<string>(), true);
            engine.ApplyScan(found, report);
            var m31 = engine.GetTargets().Single(t => t.Name == "M 31");
            var night = new DateOnly(2026, 10, 25);
            var first = engine.Commit(engine.PreviewNew(m31.Id, night, Utc("2026-10-25T19:00:00Z"), TimeSpan.FromHours(1)))!;
            var second = engine.Commit(engine.PreviewNew(m31.Id, night.AddDays(1), Utc("2026-10-26T19:00:00Z"), TimeSpan.FromHours(1)))!;
            engine.UpdateLayout(s => { s.ListPanelWidth = 420; s.ListShare = 2; s.DetailsShare = 1; });

            BoardEngine Restart() {
                var e = new BoardEngine(new BoardStore(folder), new BuiltInSkyContext(London, LondonZone), () => Utc("2026-10-25T17:00:00Z"));
                e.Load();
                e.ApplyScan(found, report);
                return e;
            }

            var again = Restart();
            Check(again.UndoLabel == "placing M 31", $"undo is still there after a restart: {again.UndoLabel}");
            Check(again.Settings.ListPanelWidth == 420 && again.Settings.ListShare == 2 && again.Settings.DetailsShare == 1, "panel sizes are saved");
            Check(again.Undo() != null && again.GetEvent(second.Id) == null && again.GetEvent(first.Id) != null, "undo after a restart removes the last event");
            Check(again.GetEvent(first.Id)!.StartUtc.Kind == DateTimeKind.Utc, "times come back as UTC");

            var third = Restart();
            Check(third.GetEvent(second.Id) == null && third.GetEvent(first.Id) != null, "the undo itself is saved");
            Check(third.Undo() != null && third.GetEvent(first.Id) == null, "and the step before it is still there");
            Check(third.Undo() == null, "nothing left to undo");

            File.WriteAllText(Path.Combine(folder, "undo.json"), "{ not json");
            File.WriteAllText(Path.Combine(folder, "undo.json.bak"), "{ not json");
            var fourth = Restart();
            Check(fourth.Undo() == null && fourth.GetTargets().Count > 0, "a damaged undo file costs only the undo history");
            Directory.Delete(folder, true);
        }

        private static void TargetSetsLinkSortAndPlaceTogether() {
            var (engine, folder, _, _) = NewEngine();
            var seq = Path.Combine(folder, "seq");
            Directory.CreateDirectory(seq);
            string Panel(string name, double ra, double dec, int total, int done) =>
                $@"<CaptureSequenceList TargetName=""{name}"" Mode=""STANDARD""><Coordinates><RA>{ra.ToString(CultureInfo.InvariantCulture)}</RA><Dec>{dec.ToString(CultureInfo.InvariantCulture)}</Dec></Coordinates>{Row(true, 300, "LIGHT", "Ha", total, done)}</CaptureSequenceList>";
            File.WriteAllText(Path.Combine(seq, "Veil.ninaTargetSet"),
                $"<?xml version=\"1.0\"?><ArrayOfCaptureSequenceList>{Panel("Veil P1", 20.9346, 31.7712, 12, 12)}{Panel("Veil P2", 20.9346, 31.38, 12, 0)}{Panel("Veil P3", 20.9346, 31.0, 12, 0)}</ArrayOfCaptureSequenceList>");
            File.WriteAllText(Path.Combine(seq, "Veil P2.xml"), "<?xml version=\"1.0\"?>" + Panel("Veil P2", 20.9346, 31.38, 12, 0));
            File.WriteAllText(Path.Combine(seq, "M 31.xml"), "<?xml version=\"1.0\"?>" + Panel("M 31", 0.7123, 41.27, 24, 6));
            File.SetCreationTimeUtc(Path.Combine(seq, "M 31.xml"), Utc("2026-01-01T00:00:00Z"));
            File.SetCreationTimeUtc(Path.Combine(seq, "Veil.ninaTargetSet"), Utc("2026-09-01T00:00:00Z"));
            var (found, report) = SequenceScanner.Scan(new[] { seq }, Array.Empty<string>(), false);
            engine.ApplyScan(found, report);

            var all = engine.GetTargets();
            var p2 = all.Single(t => t.Name == "Veil P2" && t.GroupId != null);
            var copy = all.Single(t => t.Name == "Veil P2" && t.GroupId == null);
            Check(p2.GroupName == "Veil" && p2.GroupIndex == 1, "panel knows its target set and position");
            Check(copy.CopyOfId == p2.Id && p2.Copies.Single().Id == copy.Id, "the loose .xml is linked to its panel");
            Check(all.Single(t => t.Name == "M 31").GroupId == null, "a single-target file is not a group");

            var units = TargetOrdering.Arrange(all.Where(t => t.CopyOfId == null), TargetSort.CreatedNewest);
            Check(units.Count == 2 && units[0].IsGroup && units[0].Members.Select(m => m.Name).SequenceEqual(new[] { "Veil P1", "Veil P2", "Veil P3" }),
                  "newest first, the set is one entry with its panels in file order");
            Check(TargetOrdering.Arrange(all.Where(t => t.CopyOfId == null), TargetSort.CreatedOldest)[0].Name == "M 31", "oldest first");
            Check(TargetOrdering.Arrange(all.Where(t => t.CopyOfId == null), TargetSort.Progress)[0].Name == "Veil", "most progress first (12 of 36 vs 6 of 24)");

            // Frames named after a panel go to the set's panel, not the loose copy.
            engine.RecordCapture(new CaptureRecord { TimeUtc = Utc("2026-10-25T21:00:00Z"), ExposureSeconds = 300, TargetName = "Veil P2", RaHours = 20.9346, DecDegrees = 31.38, Filter = "Ha" });
            Check(engine.Captures().Single().TargetId == p2.Id, "capture matched to the set's panel");

            // ...unless NINA says it was running the loose .xml.
            CaptureRecord Shot(string? file, string? title) => engine.RecordCapture(new CaptureRecord {
                TimeUtc = Utc("2026-10-25T21:10:00Z"), ExposureSeconds = 300, TargetName = "Veil P2", RaHours = 20.9346, DecDegrees = 31.38,
                Filter = "Ha", SourceFile = file, SequenceTitle = title })!;
            Check(Shot(Path.Combine(seq, "Veil P2.xml"), "Veil").TargetId == copy.Id, "the running .xml wins over a stale set title");
            Check(Shot(Path.Combine(seq, ".", "VEIL P2.xml"), null).TargetId == copy.Id, "paths compare without case or dots");
            Check(Shot(null, "Veil").TargetId == p2.Id, "the set's title credits the set's panel");
            Check(Shot(null, "Veil P2").TargetId == copy.Id, "a title naming the .xml credits the .xml");
            Check(Shot(Path.Combine(seq, "elsewhere.xml"), "Veil P2").TargetId == p2.Id, "an unknown file falls back to the old rule, ignoring the title");
            Check(Shot(null, null).TargetId == p2.Id, "no word from NINA: the old rule");

            // Drop the whole set: the finished panel is skipped, the others go back to back.
            var night = new DateOnly(2026, 10, 25);
            var g = engine.PreviewGroup(p2.GroupId!, night, Utc("2026-10-25T19:00:00Z"));
            Check(g.Panels.Count == 2 && g.Panels[0].TargetId == p2.Id, $"two unfinished panels placed in order: {g.Message}");
            var first = g.Panels[0].Result.Placed!.Value;
            var second = g.Panels[1].Result.Placed!.Value;
            Check(second.StartUtc == first.EndUtc, "back to back");
            Check(engine.CommitGroup(g) == 2 && engine.GetTarget(p2.Id)!.Unscheduled == TimeSpan.Zero, "committed, nothing left to place for P2");
            var again = engine.PreviewGroup(p2.GroupId!, night.AddDays(1), null);
            Check(again.Panels.Count == 0 && !again.CanCommit, "a fully planned set has nothing more to drop");
            Directory.Delete(folder, true);
        }
    }
}
