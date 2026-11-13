using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TargetBoard.Core;
using TargetBoard.Core.Engine;
using TargetBoard.Core.Models;
using TargetBoard.Core.Scheduling;
using TargetBoard.Core.Sky;
using TargetBoard.Util;

namespace TargetBoard.Board {

    /// <summary>
    /// The calendar. Night and Week views are time grids (one column per night, dusk at the top, dawn at the bottom,
    /// like Google Calendar's day and week views). Month view is a grid of nights with event chips.
    /// Drawn by hand in OnRender so hundreds of blocks, windows and curves stay smooth while dragging.
    /// </summary>
    public sealed class CalendarCanvas : FrameworkElement {
        public const string DragFormat = "TargetBoard.TargetId";
        public const string GroupDragFormat = "TargetBoard.GroupId";

        public static readonly DependencyProperty SnapshotProperty = DependencyProperty.Register(
            nameof(Snapshot), typeof(BoardSnapshot), typeof(CalendarCanvas),
            new FrameworkPropertyMetadata(BoardSnapshot.Empty, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((CalendarCanvas)d).OnSnapshotChanged()));

        public static readonly DependencyProperty ControllerProperty = DependencyProperty.Register(
            nameof(Controller), typeof(IBoardController), typeof(CalendarCanvas),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public BoardSnapshot Snapshot {
            get => (BoardSnapshot)GetValue(SnapshotProperty);
            set => SetValue(SnapshotProperty, value);
        }

        public IBoardController? Controller {
            get => (IBoardController?)GetValue(ControllerProperty);
            set => SetValue(ControllerProperty, value);
        }

        private const double HeaderH = 42, Gutter = 44, WeekdayH = 22, ChipH = 15;

        private enum HitKind { Event, Resize, Session, Milestone, Header, More, Cell, Empty }

        private sealed record Hit(HitKind Kind, Rect Box, DateOnly Night, string Id = "", string TargetId = "", string Text = "");

        private enum DragMode { None, Pending, Move, Resize }

        private readonly List<Hit> hits = new();
        private DragMode dragMode;
        private Point pressPoint;
        private Hit? pressHit;
        private double grabOffsetHours;
        private PlacementPreview? preview;
        private GroupPlacementPreview? groupPreview;
        /// <summary>Several selected events being dragged together.</summary>
        private MultiMovePreview? multiPreview;
        private readonly List<(Point At, double Altitude, DateTime Utc, bool Single, string Color)> peaks = new();
        private Point previewPoint;
        private string previewKey = "";
        /// <summary>Events selected with click and Ctrl+click.</summary>
        private readonly HashSet<string> selectedEventIds = new();
        /// <summary>The event clicked last.</summary>
        private string? selectedEventId;
        /// <summary>The filter breakdown opened by double-clicking an event; Esc closes it.</summary>
        private FramePlanView? filterView;
        private string hoverKey = "";
        private readonly List<(Point At, double Altitude, string Label)> moonPeaks = new();

        private Typeface face = new("Segoe UI");
        private Typeface bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        private double dpi = 1;

        public CalendarCanvas() {
            Focusable = true;
            AllowDrop = true;
            ClipToBounds = true;
            FocusVisualStyle = null;
            ToolTipService.SetInitialShowDelay(this, 350);
            ToolTipService.SetShowDuration(this, 20000);
        }

        protected override Size MeasureOverride(Size available) {
            double w = double.IsInfinity(available.Width) ? 900 : available.Width;
            double h = double.IsInfinity(available.Height) ? 600 : available.Height;
            return new Size(w, Math.Max(h, 300));
        }

        // ================================================================== theme

        /// <summary>
        /// A frozen copy of a NINA theme brush as it is right now. NINA's own brushes may be live (not freezable),
        /// and a pen made from one cannot be frozen, so the calendar never uses them directly.
        /// </summary>
        private Brush Res(string key, Color fallback) {
            if (TryFindResource(key) is not Brush found) return Frozen(fallback);
            if (found is SolidColorBrush solid) {
                var c = solid.Color;
                return Frozen(Color.FromArgb((byte)Math.Round(c.A * Math.Clamp(solid.Opacity, 0, 1)), c.R, c.G, c.B));
            }
            var copy = found.CloneCurrentValue();
            if (copy.CanFreeze) copy.Freeze();
            return copy;
        }

        private static T FreezeIfCan<T>(T f) where T : Freezable {
            if (f.CanFreeze) f.Freeze();
            return f;
        }

        private static SolidColorBrush Frozen(Color c) {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private static Color WithAlpha(Brush brush, byte alpha) {
            var c = brush is SolidColorBrush s ? s.Color : Colors.Gray;
            return Color.FromArgb(alpha, c.R, c.G, c.B);
        }

        private static Brush Hatch(Color a, Color b, double size = 6) {
            var brush = new LinearGradientBrush {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = new Point(0, 0),
                EndPoint = new Point(size, size),
                SpreadMethod = GradientSpreadMethod.Repeat
            };
            brush.GradientStops.Add(new GradientStop(a, 0));
            brush.GradientStops.Add(new GradientStop(a, 0.5));
            brush.GradientStops.Add(new GradientStop(b, 0.5));
            brush.GradientStops.Add(new GradientStop(b, 1));
            brush.Freeze();
            return brush;
        }

        private static Brush TextOn(Color background) {
            double lum = (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255;
            return lum > 0.6 ? Brushes.Black : Brushes.White;
        }

        private sealed class Theme {
            public Brush Background = Brushes.Black, Panel = Brushes.Black, Cell = Brushes.Black, Text = Brushes.White,
                Muted = Brushes.Gray, Line = Brushes.Gray, Faint = Brushes.Gray, Twilight = Brushes.Black, Past = Brushes.Black,
                Now = Brushes.Red, Good = Brushes.Green, Warn = Brushes.Orange, Bad = Brushes.Red;
            public Pen LinePen = new(Brushes.Gray, 1), FaintPen = new(Brushes.Gray, 1), NowPen = new(Brushes.Red, 2);
        }

        private Theme LoadTheme() {
            var t = new Theme {
                Background = Res("BackgroundBrush", Color.FromRgb(0x1E, 0x1E, 0x1E)),
                Panel = Res("SecondaryBackgroundBrush", Color.FromRgb(0x2A, 0x2A, 0x2A)),
                Cell = Res("TertiaryBackgroundBrush", Color.FromRgb(0x33, 0x33, 0x33)),
                Text = Res("PrimaryBrush", Color.FromRgb(0xEE, 0xEE, 0xEE)),
                Muted = Res("ButtonForegroundDisabledBrush", Color.FromRgb(0x99, 0x99, 0x99)),
                Now = Frozen(Color.FromRgb(0xE5, 0x39, 0x35)),
                Good = Frozen(Color.FromRgb(0x43, 0xA0, 0x47)),
                Warn = Frozen(Color.FromRgb(0xF5, 0xA6, 0x23)),
                Bad = Frozen(Color.FromRgb(0xE5, 0x39, 0x35))
            };
            var line = Res("BorderBrush", Color.FromRgb(0x55, 0x55, 0x55));
            t.Line = line;
            t.Faint = Frozen(WithAlpha(line, 0x55));
            t.Twilight = Hatch(WithAlpha(t.Panel, 0xF0), WithAlpha(t.Background, 0xF0), 8);
            t.Past = Frozen(Color.FromArgb(0x55, 0, 0, 0));
            t.LinePen = FreezeIfCan(new Pen(line, 1));
            t.FaintPen = FreezeIfCan(new Pen(t.Faint, 1));
            t.NowPen = FreezeIfCan(new Pen(t.Now, 2));
            return t;
        }

        private void Text(DrawingContext dc, string text, double x, double y, double size, Brush brush, double maxWidth,
                          bool isBold = false, TextAlignment align = TextAlignment.Left) {
            if (string.IsNullOrEmpty(text) || maxWidth < 6) return;
            var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, isBold ? bold : face, size, brush, dpi) {
                MaxTextWidth = maxWidth,
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
                TextAlignment = align
            };
            dc.DrawText(ft, new Point(x, y));
        }

        // ================================================================== layout helpers

        private BoardSnapshot S => Snapshot ?? BoardSnapshot.Empty;
        private bool IsMonth => S.Mode == BoardViewMode.Month;
        private double ColW => S.Nights.Count == 0 ? 0 : Math.Max(1, (ActualWidth - Gutter) / S.Nights.Count);
        private double GridH => Math.Max(1, ActualHeight - HeaderH);
        private double AxisSpan => Math.Max(1, S.AxisEndHours - S.AxisStartHours);
        private double PxPerHour => GridH / AxisSpan;

        private double ColX(int i) => Gutter + i * ColW;

        private double YOfHours(double clockHours) => HeaderH + (clockHours - S.AxisStartHours) / AxisSpan * GridH;

        private double Y(DateTime utc, DateOnly night) => YOfHours(NightMath.ClockHoursAfterNoon(utc, night, S.Zone));

        private double HoursAtY(double y) => S.AxisStartHours + (y - HeaderH) / GridH * AxisSpan;

        private int ColumnAt(double x) {
            if (S.Nights.Count == 0 || x < Gutter) return -1;
            int i = (int)Math.Floor((x - Gutter) / ColW);
            return i >= 0 && i < S.Nights.Count ? i : -1;
        }

        private int Rows => Math.Max(1, (S.Nights.Count + 6) / 7);
        private double CellW => ActualWidth / 7;
        private double CellH => Math.Max(1, (ActualHeight - WeekdayH) / Rows);
        private Rect CellRect(int index) => new(index % 7 * CellW, WeekdayH + index / 7 * CellH, CellW, CellH);

        private int CellAt(Point p) {
            if (p.Y < WeekdayH || S.Nights.Count == 0) return -1;
            int col = (int)Math.Floor(p.X / CellW), row = (int)Math.Floor((p.Y - WeekdayH) / CellH);
            int i = row * 7 + col;
            return col >= 0 && col < 7 && i >= 0 && i < S.Nights.Count ? i : -1;
        }

        /// <summary>The night and time under a point. Month cells have no time.</summary>
        private bool TryMap(Point p, out DateOnly night, out DateTime? utc) {
            night = default;
            utc = null;
            if (IsMonth) {
                int i = CellAt(p);
                if (i < 0) return false;
                night = S.Nights[i].Night;
                return true;
            }
            int c = ColumnAt(p.X);
            if (c < 0 || p.Y < HeaderH) return false;
            night = S.Nights[c].Night;
            utc = NightMath.FromClockHoursAfterNoon(HoursAtY(p.Y), night, S.Zone);
            return true;
        }

        private int NightIndex(DateOnly night) {
            for (int i = 0; i < S.Nights.Count; i++) if (S.Nights[i].Night == night) return i;
            return -1;
        }

        // ================================================================== rendering

        protected override void OnRender(DrawingContext dc) {
            try {
                Render(dc);
            } catch (Exception ex) {
                Report("drawing", ex);
                try {
                    dc.DrawRectangle(Frozen(Color.FromRgb(0x20, 0x20, 0x20)), null, new Rect(0, 0, ActualWidth, ActualHeight));
                    Text(dc, "Target Board could not draw the calendar: " + ex.Message, 8, 8, 12, Brushes.OrangeRed, Math.Max(10, ActualWidth - 16));
                    Text(dc, "The details are in the NINA log. NINA itself is not affected.", 8, 28, 11, Brushes.LightGray, Math.Max(10, ActualWidth - 16));
                } catch (Exception) {
                    // Nothing more can be drawn.
                }
            }
        }

        private void Render(DrawingContext dc) {
            dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            hits.Clear();
            peaks.Clear();
            moonPeaks.Clear();
            var theme = LoadTheme();
            dc.DrawRectangle(theme.Background, null, new Rect(0, 0, ActualWidth, ActualHeight));
            if (S.Nights.Count == 0) {
                Text(dc, "Loading…", 0, ActualHeight / 2 - 8, 13, theme.Muted, ActualWidth, align: TextAlignment.Center);
                return;
            }
            if (IsMonth) RenderMonth(dc, theme); else RenderTimeGrid(dc, theme);
            DrawFilterView(dc, theme);
        }

        private void OnSnapshotChanged() => Guard(() => {
            // Keep the selection to events that still exist, and keep an open filter view up to date.
            var ids = new HashSet<string>(S.Events.Select(e => e.Id));
            selectedEventIds.RemoveWhere(id => !ids.Contains(id));
            if (selectedEventId != null && !ids.Contains(selectedEventId)) selectedEventId = null;
            if (filterView != null) filterView = Controller?.FramePlan(filterView.EventId);
        });

        // ------------------------------------------------------------------ night and week

        private void RenderTimeGrid(DrawingContext dc, Theme t) {
            var s = S;
            bool single = s.Mode == BoardViewMode.Night;
            double colW = ColW;
            double stripW = single ? 30 : 8;

            // Hour lines and labels.
            for (double h = Math.Ceiling(s.AxisStartHours); h <= s.AxisEndHours; h++) {
                double y = YOfHours(h);
                dc.DrawLine(t.FaintPen, new Point(Gutter, y), new Point(ActualWidth, y));
                var label = Fmt.ClockFromMinutes((h + 12) * 60);
                Text(dc, label, 2, y - (h == Math.Ceiling(s.AxisStartHours) && y - HeaderH < 8 ? 0 : 7), 10, t.Muted, Gutter - 6, align: TextAlignment.Right);
            }

            for (int i = 0; i < s.Nights.Count; i++) {
                var n = s.Nights[i];
                double x = ColX(i);
                var col = new Rect(x, HeaderH, colW, GridH);

                // Twilight and daylight outside the dark window.
                if (n.Dark is TimeRange dark) {
                    double y1 = Math.Clamp(Y(dark.StartUtc, n.Night), HeaderH, ActualHeight);
                    double y2 = Math.Clamp(Y(dark.EndUtc, n.Night), HeaderH, ActualHeight);
                    if (y1 > HeaderH) dc.DrawRectangle(t.Twilight, null, new Rect(x, HeaderH, colW, y1 - HeaderH));
                    if (y2 < ActualHeight) dc.DrawRectangle(t.Twilight, null, new Rect(x, y2, colW, ActualHeight - y2));
                    if (colW > 70) {
                        Text(dc, "dusk " + Clock(dark.StartUtc), x + 4, y1 - 14, 9.5, t.Muted, colW - 8, align: TextAlignment.Right);
                        Text(dc, "dawn " + Clock(dark.EndUtc), x + 4, y2 + 1, 9.5, t.Muted, colW - 8, align: TextAlignment.Right);
                    }
                } else {
                    dc.DrawRectangle(t.Twilight, null, col);
                    Text(dc, "No dark time", x, HeaderH + GridH / 2 - 8, 11, t.Muted, colW, align: TextAlignment.Center);
                }

                // Where the selected target meets its conditions.
                if (s.ActiveTargetId != null && s.ActiveWindows.TryGetValue(n.Night, out var windows)) {
                    var fill = Colors2.Brush(s.ActiveTargetColor, 0x30);
                    var edge = Colors2.Brush(s.ActiveTargetColor, 0xC0);
                    foreach (var w in windows) {
                        double y1 = Math.Clamp(Y(w.StartUtc, n.Night), HeaderH, ActualHeight);
                        double y2 = Math.Clamp(Y(w.EndUtc, n.Night), HeaderH, ActualHeight);
                        if (y2 - y1 < 1) continue;
                        dc.DrawRectangle(fill, null, new Rect(x + 1, y1, colW - 2, y2 - y1));
                        dc.DrawRectangle(edge, null, new Rect(x + 1, y1, 3, y2 - y1));
                    }
                }

                // Altitude of the selected target (0° at the column's left, 90° at its right).
                if (s.ActiveTargetId != null && s.ActiveAltitude.TryGetValue(n.Night, out var curve) && curve.Count > 1) {
                    DrawAltitude(dc, t, curve, n.Night, x, colW, single, s.ActiveTargetColor);
                }

                // The Moon's altitude, when switched on: a dotted light-grey line on the same scale.
                if (s.ShowMoon && s.MoonAltitude.TryGetValue(n.Night, out var moon) && moon.Count > 1) {
                    DrawMoon(dc, t, moon, n, x, colW, single);
                }

                // Already-past part of the night.
                if (s.NowUtc > n.Span.StartUtc) {
                    double yNow = n.Span.EndUtc <= s.NowUtc ? ActualHeight : Math.Clamp(Y(s.NowUtc, n.Night), HeaderH, ActualHeight);
                    if (yNow > HeaderH) dc.DrawRectangle(t.Past, null, new Rect(x, HeaderH, colW, yNow - HeaderH));
                }

                // Done sessions: a strip on the right edge.
                double stripX = x + colW - stripW - 3;
                foreach (var sess in s.Sessions.Where(v => v.Night == n.Night)) {
                    double y1 = Math.Clamp(Y(sess.StartUtc, n.Night), HeaderH, ActualHeight);
                    double y2 = Math.Clamp(Y(sess.EndUtc, n.Night), HeaderH, ActualHeight);
                    var box = new Rect(stripX, y1, stripW, Math.Max(3, y2 - y1));
                    var c = Colors2.Parse(sess.Color, Colors.Gray);
                    dc.DrawRoundedRectangle(Frozen(c), null, box, 2, 2);
                    if (single && box.Height > 14) Text(dc, "✓" + sess.Frames, box.X, box.Y + 1, 9.5, TextOn(c), box.Width, align: TextAlignment.Center);
                    hits.Add(new Hit(HitKind.Session, box, n.Night, sess.Key, sess.TargetId ?? "", $"{sess.Title}\n{sess.Detail}"));
                }

                // Planned events.
                double evX = x + 5, evW = Math.Max(10, colW - stripW - 12);
                foreach (var ev in s.Events.Where(v => v.Night == n.Night)) {
                    DrawEvent(dc, t, ev, new Rect(evX, Y(ev.StartUtc, n.Night), evW, Math.Max(6, Y(ev.EndUtc, n.Night) - Y(ev.StartUtc, n.Night))), single);
                }

                // The "now" line, as in Google Calendar.
                if (n.Span.Contains(s.NowUtc)) {
                    double y = Y(s.NowUtc, n.Night);
                    if (y >= HeaderH && y <= ActualHeight) {
                        dc.DrawLine(t.NowPen, new Point(x, y), new Point(x + colW, y));
                        dc.DrawEllipse(t.Now, null, new Point(x + 1, y), 4, 4);
                    }
                }

                // Column header.
                dc.DrawRectangle(t.Panel, null, new Rect(x, 0, colW, HeaderH));
                var header = new Rect(x, 0, colW, HeaderH);
                string day = n.Night.ToString(colW > 110 ? "ddd d MMM" : "ddd d", CultureInfo.CurrentCulture);
                if (n.IsTonight) {
                    dc.DrawRoundedRectangle(t.Now, null, new Rect(x + 4, 4, Math.Min(colW - 8, 110), 17), 8, 8);
                    Text(dc, "Tonight · " + day, x + 4, 5, 11.5, Brushes.White, Math.Min(colW - 8, 110), isBold: true, align: TextAlignment.Center);
                } else {
                    Text(dc, day, x + 6, 5, 11.5, n.IsPast ? t.Muted : t.Text, colW - 10, isBold: true);
                }
                string info = $"{Moon(n.MoonIllumination)} · {(n.Dark is TimeRange d ? Fmt.Duration(d.Duration) + " dark" : "no dark")}";
                if (n.Planned > TimeSpan.Zero && colW > 120) info += $" · {Fmt.Duration(n.Planned)} planned";
                Text(dc, info, x + 6, 24, 9.5, t.Muted, colW - 10);
                string headerTip = single ? "" : "Open this night";
                if (n.Missed > 0) {
                    string badge = colW >= 170 ? $"⚠ {n.Missed} missed" : $"⚠ {n.Missed}";
                    double bw = colW >= 170 ? 74 : 30;
                    var bbox = new Rect(x + colW - bw - 4, 4, bw, 16);
                    dc.DrawRoundedRectangle(Frozen(Color.FromArgb(0x50, 0xE5, 0x39, 0x35)), FreezeIfCan(new Pen(t.Bad, 1)), bbox, 8, 8);
                    Text(dc, badge, bbox.X, bbox.Y + 1, 10, Brushes.White, bbox.Width, isBold: true, align: TextAlignment.Center);
                    headerTip += (headerTip.Length > 0 ? "\n" : "") + $"{n.Missed} event{(n.Missed == 1 ? " was" : "s were")} missed this night.";
                }
                if (n.Partial > 0) headerTip += (headerTip.Length > 0 ? "\n" : "") + $"{n.Partial} event{(n.Partial == 1 ? " was" : "s were")} only partly done.";
                headerTip += (headerTip.Length > 0 ? "\n" : "") + "Right-click: shift the plan, add missed time, settle the night.";
                hits.Add(new Hit(HitKind.Header, header, n.Night, Text: headerTip));

                dc.DrawLine(t.LinePen, new Point(x, 0), new Point(x, ActualHeight));
            }
            dc.DrawLine(t.LinePen, new Point(0, HeaderH), new Point(ActualWidth, HeaderH));
            dc.DrawRectangle(t.Panel, null, new Rect(0, 0, Gutter, HeaderH));
            Text(dc, single ? "" : "Local", 2, 14, 9.5, t.Muted, Gutter - 6, align: TextAlignment.Right);

            DrawPeaks(dc, t);
            DrawTimeGridPreview(dc, t);
        }

        private void DrawAltitude(DrawingContext dc, Theme t, List<(DateTime Utc, double Altitude)> curve, DateOnly night, double x, double colW, bool single, string color) {
            double left = x + 6, width = colW - 12;
            if (single) {
                foreach (var alt in new[] { 30, 60 }) {
                    double ax = left + alt / 90.0 * width;
                    dc.DrawLine(t.FaintPen, new Point(ax, HeaderH), new Point(ax, ActualHeight));
                    Text(dc, alt + "°", ax - 20, HeaderH + 2, 9, t.Muted, 40, align: TextAlignment.Center);
                }
            }
            var geo = new StreamGeometry();
            (Point At, double Alt, DateTime Utc)? peak = null;
            using (var g = geo.Open()) {
                bool open = false;
                foreach (var (utc, alt) in curve) {
                    double y = Y(utc, night);
                    if (y < HeaderH || y > ActualHeight || alt < 0) { open = false; continue; }
                    var p = new Point(left + Math.Min(90, alt) / 90.0 * width, y);
                    if (!open) { g.BeginFigure(p, false, false); open = true; } else g.LineTo(p, true, false);
                    if (peak == null || alt > peak.Value.Alt) peak = (p, alt, utc);
                }
            }
            if (peak is { } top) peaks.Add((top.At, top.Alt, top.Utc, single, color));
            FreezeIfCan(geo);
            var pen = new Pen(Colors2.Brush(color, 0xD0), single ? 2 : 1.25) { DashStyle = single ? DashStyles.Solid : DashStyles.Dash };
            FreezeIfCan(pen);
            dc.DrawGeometry(null, pen, geo);
        }

        private static readonly Pen MoonPen = MakeMoonPen(1.6);
        private static readonly Pen MoonPenThin = MakeMoonPen(1.3);

        private static Pen MakeMoonPen(double width) {
            // Light grey at 70% opacity, dotted (round dots).
            var pen = new Pen(Frozen(Color.FromArgb(0xB3, 0xD6, 0xD6, 0xD6)), width) {
                DashStyle = new DashStyle(new[] { 0.0, 2.6 }, 0),
                DashCap = PenLineCap.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round
            };
            pen.Freeze();
            return pen;
        }

        private void DrawMoon(DrawingContext dc, Theme t, List<(DateTime Utc, double Altitude)> curve, NightView n, double x, double colW, bool single) {
            double left = x + 6, width = colW - 12;
            var geo = new StreamGeometry();
            (Point At, double Alt)? peak = null;
            using (var g = geo.Open()) {
                bool open = false;
                foreach (var (utc, alt) in curve) {
                    double y = Y(utc, n.Night);
                    if (y < HeaderH || y > ActualHeight || alt < 0) { open = false; continue; }
                    var p = new Point(left + Math.Min(90, alt) / 90.0 * width, y);
                    if (!open) { g.BeginFigure(p, false, false); open = true; } else g.LineTo(p, true, false);
                    if (peak == null || alt > peak.Value.Alt) peak = (p, alt);
                }
            }
            FreezeIfCan(geo);
            dc.DrawGeometry(null, single ? MoonPen : MoonPenThin, geo);
            if (peak is { } top) {
                string phase = n.MoonIllumination is double m ? $"{m * 100:0}%" : "";
                moonPeaks.Add((top.At, top.Alt, single ? $"☾ {phase} · {top.Alt:0}°" : $"☾ {phase}"));
            }
        }

        /// <summary>A dot and label at the highest point of each altitude curve on screen.</summary>
        private void DrawPeaks(DrawingContext dc, Theme t) {
            foreach (var (at, _, label) in moonPeaks) {
                dc.DrawEllipse(Frozen(Color.FromArgb(0xB3, 0xD6, 0xD6, 0xD6)), null, at, 3, 3);
                double w = label.Length * 6.2 + 8, h = 14;
                double lx = at.X + 6 + w > ActualWidth - 2 ? at.X - 6 - w : at.X + 6;
                var box = new Rect(lx, at.Y - h / 2, w, h);
                dc.DrawRoundedRectangle(Frozen(Color.FromArgb(0xB0, 0x18, 0x18, 0x18)), null, box, 3, 3);
                Text(dc, label, box.X + 3, box.Y + 1, 9.5, Frozen(Color.FromArgb(0xE0, 0xE0, 0xE0, 0xE0)), box.Width - 6, align: TextAlignment.Center);
            }
            foreach (var (at, alt, utc, single, color) in peaks) {
                var c = Colors2.Parse(color, Colors.SteelBlue);
                dc.DrawEllipse(Frozen(c), new Pen(t.Background, 1.5), at, single ? 4.5 : 3.5, single ? 4.5 : 3.5);
                string label = single ? $"Max {alt:0}° at {Clock(utc)}" : $"{alt:0}°";
                double w = single ? 118 : 34, h = single ? 17 : 14;
                // Keep the label inside the column: to the left of the dot when it sits near the right edge.
                double x = at.X + 7 + w > ActualWidth - 2 ? at.X - 7 - w : at.X + 7;
                var box = new Rect(x, at.Y - h / 2, w, h);
                dc.DrawRoundedRectangle(Frozen(Color.FromArgb(0xD8, 0x18, 0x18, 0x18)), new Pen(Frozen(c), 1), box, 3, 3);
                Text(dc, label, box.X + 3, box.Y + (single ? 1.5 : 1), single ? 11 : 9.5, Brushes.White, box.Width - 6, isBold: true, align: TextAlignment.Center);
            }
        }

        private static Pen DashedPen(Color color, double width) => FreezeIfCan(new Pen(Frozen(color), width) { DashStyle = DashStyles.Dash });

        private static readonly Color MissedRed = Color.FromRgb(0xE5, 0x39, 0x35);
        private static readonly Color WarnAmber = Color.FromRgb(0xF5, 0xA6, 0x23);

        private static string Glyph(EventStatus status) => status switch {
            EventStatus.Done => "✓ ",
            EventStatus.Partial => "◐ ",
            EventStatus.Missed => "✗ ",
            EventStatus.Unknown => "? ",
            _ => ""
        };

        private string EventTip(EventView ev) {
            var tip = $"{ev.Title}\n{ev.Detail}";
            if (ev.StatusText.Length > 0) tip += "\n" + ev.StatusText;
            if (ev.OutsideConditions > TimeSpan.Zero) tip += $"\n⚠ {Fmt.Duration(ev.OutsideConditions)} outside this target's conditions{(ev.Forced ? " (placed with Shift)" : "")}";
            tip += ev.IsSettled
                ? "\nDouble-click: frames taken · right-click for more"
                : "\nDouble-click: filters · drag to move (hold Shift to ignore conditions) · Ctrl+click: select several · right-click for more";
            return tip;
        }

        private void DrawEvent(DrawingContext dc, Theme t, EventView ev, Rect box, bool single) {
            var c = Colors2.Parse(ev.Color, Colors.SteelBlue);
            bool missed = ev.Status == EventStatus.Missed;
            bool finished = ev.IsPast || ev.IsSettled;
            bool isSelected = selectedEventIds.Contains(ev.Id);
            bool isDragged = (preview != null && preview.EventId == ev.Id && dragMode is DragMode.Move or DragMode.Resize)
                             || (multiPreview != null && multiPreview.Items.Any(i => i.EventId == ev.Id || i.ReplanOfEventId == ev.Id));
            bool outside = !finished && ev.OutsideConditions > TimeSpan.Zero;

            // A settled event shrank to what was done: its planned block stays as a faint dashed outline.
            if (!missed && ev.PlannedStartUtc is DateTime ps && ev.PlannedEndUtc is DateTime pe) {
                double gy1 = Y(ps, ev.Night), gy2 = Y(pe, ev.Night);
                dc.DrawRoundedRectangle(null, DashedPen(Color.FromArgb(0x80, c.R, c.G, c.B), 1), new Rect(box.X, gy1, box.Width, Math.Max(6, gy2 - gy1)), 4, 4);
            }

            // Finished events are the same block at reduced opacity.
            if (finished) dc.PushOpacity(missed ? 0.75 : 0.45);
            Brush fill;
            Pen pen;
            if (missed) {
                fill = Hatch(Color.FromArgb(0x38, MissedRed.R, MissedRed.G, MissedRed.B), Color.FromArgb(0x10, MissedRed.R, MissedRed.G, MissedRed.B), 8);
                pen = DashedPen(MissedRed, 1.5);
            } else {
                fill = ev.TargetMissing ? Hatch(Color.FromArgb(0xE0, c.R, c.G, c.B), Color.FromArgb(0x40, c.R, c.G, c.B)) : Colors2.Brush(ev.Color, 0xE0);
                pen = outside ? DashedPen(Color.FromArgb(0xFF, (byte)Math.Min(255, c.R + 70), (byte)Math.Min(255, c.G + 70), (byte)Math.Min(255, c.B + 70)), 2)
                              : FreezeIfCan(new Pen(Colors2.Brush(ev.Color, 0xFF), 1));
            }
            if (isDragged) fill = Colors2.Brush(ev.Color, 0x40);
            dc.DrawRoundedRectangle(fill, pen, box, 4, 4);

            var fg = missed ? Frozen(MissedRed) : TextOn(Color.FromArgb(255, c.R, c.G, c.B));
            string title = Glyph(ev.Status) + (missed ? "Missed · " : "") + ev.Title + (ev.TargetMissing ? " (missing)" : "");
            if (box.Height >= 13) Text(dc, title, box.X + 5, box.Y + 1, 11, fg, box.Width - 8 - (outside ? 14 : 0), isBold: true);
            if (box.Height >= 28) Text(dc, ev.Detail, box.X + 5, box.Y + 15, 9.5, fg, box.Width - 8);
            if (box.Height >= 42 && ev.StatusText.Length > 0 && ev.Status != EventStatus.Upcoming) Text(dc, ev.StatusText, box.X + 5, box.Y + 28, 9.5, fg, box.Width - 8);
            if (finished) dc.Pop();

            if (outside && box.Height >= 9) DrawWarning(dc, new Point(box.Right - 3, box.Bottom - 3), Math.Min(14, box.Height - 2));
            if (isSelected) dc.DrawRoundedRectangle(null, FreezeIfCan(new Pen(t.Text, 2)), new Rect(box.X - 1, box.Y - 1, box.Width + 2, box.Height + 2), 5, 5);

            hits.Add(new Hit(HitKind.Event, box, ev.Night, ev.Id, ev.TargetId, EventTip(ev)));
            if (box.Height >= 12 && !ev.IsSettled) hits.Add(new Hit(HitKind.Resize, new Rect(box.X, box.Bottom - 5, box.Width, 7), ev.Night, ev.Id, ev.TargetId, "Drag to change the length (hold Shift to go past the target's conditions)"));
        }

        /// <summary>A small amber warning triangle with "!" whose bottom-right corner sits at <paramref name="corner"/>.</summary>
        private void DrawWarning(DrawingContext dc, Point corner, double size) {
            if (size < 7) return;
            double w = size * 1.1;
            var geo = new StreamGeometry();
            using (var g = geo.Open()) {
                g.BeginFigure(new Point(corner.X - w / 2, corner.Y - size), true, true);
                g.LineTo(new Point(corner.X, corner.Y), true, false);
                g.LineTo(new Point(corner.X - w, corner.Y), true, false);
            }
            FreezeIfCan(geo);
            dc.DrawGeometry(Frozen(WarnAmber), FreezeIfCan(new Pen(Frozen(Color.FromRgb(0x30, 0x20, 0x00)), 1)), geo);
            Text(dc, "!", corner.X - w, corner.Y - size + size * 0.18, size * 0.72, Brushes.Black, w, isBold: true, align: TextAlignment.Center);
        }

        // ------------------------------------------------------------------ filter view (double-click an event)

        private static Color FilterColor(string filter) {
            var f = (filter ?? "").Trim().ToUpperInvariant().Replace("-", "").Replace(" ", "");
            return f switch {
                "HA" or "H" or "HALPHA" or "HYDROGEN" or "HA7NM" or "HA3NM" => Color.FromRgb(0xE5, 0x48, 0x4D),
                "OIII" or "O3" or "O" or "OXYGEN" => Color.FromRgb(0x2E, 0xC4, 0xB6),
                "SII" or "S2" or "S" or "SULFUR" or "SULPHUR" => Color.FromRgb(0xF5, 0xA5, 0x24),
                "L" or "LUM" or "LUMINANCE" or "CLEAR" or "C" or "NOFILTER" or "(NOFILTER)" or "UVIR" or "UVIRCUT" => Color.FromRgb(0xD8, 0xD8, 0xD8),
                "R" or "RED" => Color.FromRgb(0xFF, 0x6B, 0x6B),
                "G" or "GREEN" => Color.FromRgb(0x4C, 0xC3, 0x8A),
                "B" or "BLUE" => Color.FromRgb(0x4D, 0x8D, 0xFF),
                _ => Colors2.Parse(Palette.ForKey(filter ?? ""), Colors.SlateGray)
            };
        }

        private void OpenFilterView(string eventId) {
            var fv = Controller?.FramePlan(eventId);
            if (fv == null) return;
            filterView = fv;
            Focus();
            Controller?.ShowMessage("");
            InvalidateVisual();
        }

        private double DrawWrapped(DrawingContext dc, string text, double x, double y, double size, Brush brush, double maxWidth, int maxLines, bool isBold = false) {
            if (string.IsNullOrEmpty(text) || maxWidth < 10) return 0;
            var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, isBold ? bold : face, size, brush, dpi) {
                MaxTextWidth = maxWidth,
                MaxLineCount = maxLines,
                Trimming = TextTrimming.CharacterEllipsis
            };
            dc.DrawText(ft, new Point(x, y));
            return ft.Height;
        }

        private void DrawFilterView(DrawingContext dc, Theme t) {
            var fv = filterView;
            if (fv == null) return;
            dc.DrawRectangle(Frozen(Color.FromArgb(0xA0, 0, 0, 0)), null, new Rect(0, 0, ActualWidth, ActualHeight));
            var c = Colors2.Parse(fv.Color, Colors.SteelBlue);
            var panelBg = Frozen(Color.FromArgb(0xF4, 0x20, 0x20, 0x22));

            // Totals per filter: taken, then planned.
            var rows = new List<(string Text, Color Swatch, bool Taken)>();
            foreach (var g in fv.Taken.GroupBy(x => (x.Filter, x.ExposureSeconds))) {
                int frames = g.Sum(x => x.Frames);
                rows.Add(($"{g.Key.Filter} {Fmt.Seconds(g.Key.ExposureSeconds)} × {frames} taken · {Fmt.DurationSeconds(frames * g.Key.ExposureSeconds)}", FilterColor(g.Key.Filter), true));
            }
            foreach (var g in fv.Planned.GroupBy(x => (x.Filter, x.ExposureSeconds))) {
                int frames = g.Sum(x => x.Frames);
                rows.Add(($"{g.Key.Filter} {Fmt.Seconds(g.Key.ExposureSeconds)} × {frames} planned · {Fmt.DurationSeconds(frames * g.Key.ExposureSeconds)}", FilterColor(g.Key.Filter), false));
            }

            double panelW = Math.Min(290, Math.Max(200, ActualWidth - Gutter - 20));
            double panelH = 62 + rows.Count * 18 + 48;
            Rect panel;
            int i = IsMonth ? -1 : NightIndex(fv.Night);
            if (i >= 0) {
                // The block in its column, widened so each run of frames can be read, lined up with the time axis.
                double bw = Math.Min(Math.Max(ColW - 10, 170), Math.Max(120, ActualWidth - Gutter - panelW - 30));
                double bx = ColX(i) + 5;
                if (bx + bw > ActualWidth - 4) bx = ActualWidth - 4 - bw;
                double y1 = Y(fv.Block.StartUtc, fv.Night), y2 = Y(fv.Block.EndUtc, fv.Night);
                var block = new Rect(bx, y1, bw, Math.Max(18, y2 - y1));
                dc.DrawRoundedRectangle(panelBg, FreezeIfCan(new Pen(Frozen(c), 2)), block, 5, 5);
                foreach (var seg in fv.Taken) DrawSegment(dc, seg, block, fv.Night, taken: true);
                foreach (var seg in fv.Planned) DrawSegment(dc, seg, block, fv.Night, taken: false);

                double px = block.Right + 10;
                if (px + panelW > ActualWidth - 4) px = block.X - 10 - panelW;
                if (px < 4) px = 4;
                double py = Math.Clamp(block.Y, HeaderH + 4, Math.Max(HeaderH + 4, ActualHeight - panelH - 4));
                panel = new Rect(px, py, panelW, panelH);
            } else {
                panel = new Rect((ActualWidth - panelW) / 2, Math.Max(8, (ActualHeight - panelH) / 2), panelW, panelH);
            }

            dc.DrawRoundedRectangle(panelBg, FreezeIfCan(new Pen(Frozen(c), 1.5)), panel, 6, 6);
            double y = panel.Y + 8, x = panel.X + 10, w = panel.Width - 20;
            Text(dc, fv.Title, x, y, 13, Brushes.White, w, isBold: true);
            y += 19;
            string when = $"{fv.Night.ToString("ddd d MMM", CultureInfo.CurrentCulture)} · {Clock(fv.Block.StartUtc)}–{Clock(fv.Block.EndUtc)} · {Fmt.Duration(fv.Block.Duration)}";
            Text(dc, when, x, y, 10, t.Muted, w);
            y += 18;
            if (rows.Count == 0) {
                Text(dc, "No frames.", x, y, 10.5, t.Text, w);
                y += 18;
            }
            foreach (var (text, swatch, taken) in rows) {
                var sw = new Rect(x, y + 3, 10, 10);
                if (taken) dc.DrawRectangle(Frozen(swatch), null, sw);
                else dc.DrawRectangle(Frozen(Color.FromArgb(0x60, swatch.R, swatch.G, swatch.B)), DashedPen(swatch, 1), sw);
                Text(dc, text, x + 16, y, 10.5, Brushes.White, w - 16);
                y += 18;
            }
            y += 4;
            y += DrawWrapped(dc, fv.Note, x, y, 9.5, t.Muted, w, 3);
            Text(dc, "Esc or click to close", x, panel.Bottom - 16, 9, t.Muted, w, align: TextAlignment.Right);
        }

        private void DrawSegment(DrawingContext dc, FrameSegment seg, Rect block, DateOnly night, bool taken) {
            double y1 = Math.Clamp(Y(seg.StartUtc, night), block.Top + 1, block.Bottom - 1);
            double y2 = Math.Clamp(Y(seg.EndUtc, night), block.Top + 1, block.Bottom - 1);
            if (y2 - y1 < 0.6) y2 = y1 + 0.6;
            var col = FilterColor(seg.Filter);
            var r = new Rect(block.X + 3, y1, block.Width - 6, y2 - y1);
            if (taken) dc.DrawRectangle(Frozen(Color.FromArgb(0xE0, col.R, col.G, col.B)), null, r);
            else dc.DrawRectangle(Frozen(Color.FromArgb(0x70, col.R, col.G, col.B)), r.Height >= 4 ? DashedPen(col, 1) : null, r);
            if (r.Height >= 12) {
                string label = $"{seg.Filter} {Fmt.Seconds(seg.ExposureSeconds)} × {seg.Frames}{(taken ? "" : " planned")}";
                if (r.Height >= 25) label += $"  {Clock(seg.StartUtc)}–{Clock(seg.EndUtc)}";
                Text(dc, label, r.X + 4, r.Y + Math.Max(0, (Math.Min(r.Height, 16) - 13) / 2), 10, TextOn(col), r.Width - 8, isBold: true);
            }
        }

        private void DrawGroupPreviewTimeGrid(DrawingContext dc, Theme t, GroupPlacementPreview g) {
            int i = NightIndex(g.Night);
            if (i < 0) return;
            double x = ColX(i), colW = ColW;
            var outline = !g.CanCommit ? t.Bad : g.Overflow > TimeSpan.FromSeconds(30) ? t.Warn : t.Good;
            var dashed = FreezeIfCan(new Pen(outline, 2) { DashStyle = DashStyles.Dash });
            foreach (var p in g.Panels) {
                if (p.Result.Placed is not TimeRange placed) continue;
                var c = Colors2.Parse(S.TargetColors.TryGetValue(p.TargetId, out var hex) ? hex : null, Colors.SteelBlue);
                double y1 = Y(placed.StartUtc, g.Night), y2 = Y(placed.EndUtc, g.Night);
                var box = new Rect(x + 4, y1, colW - 8, Math.Max(6, y2 - y1));
                dc.DrawRoundedRectangle(Frozen(Color.FromArgb(0x90, c.R, c.G, c.B)), dashed, box, 4, 4);
                string name = S.TargetNames.TryGetValue(p.TargetId, out var n) ? n : "";
                Text(dc, name, box.X + 5, box.Y + 2, 10.5, TextOn(c), box.Width - 8, isBold: true);
                if (box.Height > 28) Text(dc, $"{Clock(placed.StartUtc)}–{Clock(placed.EndUtc)}", box.X + 5, box.Y + 16, 9.5, TextOn(c), box.Width - 8);
            }
            var msgBox = new Rect(x + 4, HeaderH + 2, colW - 8, 16);
            dc.DrawRoundedRectangle(Frozen(Color.FromArgb(0xE0, 0x20, 0x20, 0x20)), null, msgBox, 3, 3);
            Text(dc, g.Message, msgBox.X + 4, msgBox.Y + 1, 10, outline, msgBox.Width - 8);
        }

        private void DrawMultiPreviewTimeGrid(DrawingContext dc, Theme t, MultiMovePreview m) {
            foreach (var item in m.Items) {
                int i = NightIndex(item.Night);
                if (i < 0 || item.Result.Placed is not TimeRange placed) continue;
                double x = ColX(i), colW = ColW;
                var c = Colors2.Parse(S.TargetColors.TryGetValue(item.TargetId, out var hex) ? hex : null, Colors.SteelBlue);
                var outline = !m.CanCommit ? t.Bad : item.Result.Outcome == PlacementOutcome.Clipped ? t.Warn : t.Good;
                double y1 = Y(placed.StartUtc, item.Night), y2 = Y(placed.EndUtc, item.Night);
                var box = new Rect(x + 4, y1, colW - 8, Math.Max(6, y2 - y1));
                dc.DrawRoundedRectangle(Frozen(Color.FromArgb(0x90, c.R, c.G, c.B)), FreezeIfCan(new Pen(outline, 2) { DashStyle = DashStyles.Dash }), box, 4, 4);
                string name = S.TargetNames.TryGetValue(item.TargetId, out var n) ? n : "";
                Text(dc, name, box.X + 5, box.Y + 2, 10.5, TextOn(c), box.Width - 8, isBold: true);
                if (box.Height > 28) Text(dc, $"{Clock(placed.StartUtc)}–{Clock(placed.EndUtc)}", box.X + 5, box.Y + 16, 9.5, TextOn(c), box.Width - 8);
            }
            int ai = NightIndex(m.Night);
            if (ai < 0) return;
            var msgBox = new Rect(ColX(ai) + 4, HeaderH + 2, ColW - 8, 16);
            var brush = !m.CanCommit ? t.Bad : m.Outcome == PlacementOutcome.Clipped ? t.Warn : t.Good;
            dc.DrawRoundedRectangle(Frozen(Color.FromArgb(0xE0, 0x20, 0x20, 0x20)), null, msgBox, 3, 3);
            Text(dc, m.Message, msgBox.X + 4, msgBox.Y + 1, 10, brush, msgBox.Width - 8);
        }

        private void DrawTimeGridPreview(DrawingContext dc, Theme t) {
            if (multiPreview != null) { DrawMultiPreviewTimeGrid(dc, t, multiPreview); return; }
            if (groupPreview != null) { DrawGroupPreviewTimeGrid(dc, t, groupPreview); return; }
            if (preview == null) return;
            int i = NightIndex(preview.Night);
            if (i < 0) return;
            double x = ColX(i), colW = ColW;
            var r = preview.Result;
            var color = Colors2.Parse(S.TargetColors.TryGetValue(preview.TargetId, out var hexColor) ? hexColor : null, Colors.SteelBlue);
            var outline = r.Outcome switch { PlacementOutcome.Fits => t.Good, PlacementOutcome.Clipped => t.Warn, _ => t.Bad };
            var dashed = new Pen(outline, 2) { DashStyle = DashStyles.Dash };
            FreezeIfCan(dashed);

            if (r.Placed is TimeRange placed) {
                double y1 = Y(placed.StartUtc, preview.Night), y2 = Y(placed.EndUtc, preview.Night);
                var box = new Rect(x + 4, y1, colW - 8, Math.Max(6, y2 - y1));
                dc.DrawRoundedRectangle(Frozen(Color.FromArgb(0x90, color.R, color.G, color.B)), dashed, box, 4, 4);
                Text(dc, $"{Clock(placed.StartUtc)}–{Clock(placed.EndUtc)} · {Fmt.Duration(placed.Duration)}", box.X + 5, box.Y + 2, 10.5, TextOn(color), box.Width - 8, isBold: true);
                if (r.Overflow > TimeSpan.FromSeconds(30)) {
                    double h = Math.Min(ActualHeight - box.Bottom, r.Overflow.TotalHours * PxPerHour);
                    if (h > 2) {
                        var over = new Rect(box.X, box.Bottom, box.Width, h);
                        dc.DrawRectangle(Hatch(Color.FromArgb(0x90, 0xE5, 0x39, 0x35), Color.FromArgb(0x30, 0xE5, 0x39, 0x35)), null, over);
                        Text(dc, $"{Fmt.Duration(r.Overflow)} does not fit", over.X + 5, over.Y + 2, 10, t.Text, over.Width - 8, isBold: true);
                    }
                }
            } else {
                var box = new Rect(x + 4, Math.Clamp(previewPoint.Y, HeaderH, ActualHeight - 20), colW - 8, Math.Max(20, Math.Min(r.Requested.TotalHours * PxPerHour, 60)));
                dc.DrawRoundedRectangle(Frozen(Color.FromArgb(0x30, 0xE5, 0x39, 0x35)), dashed, box, 4, 4);
            }
            var msgBox = new Rect(x + 4, HeaderH + 2, colW - 8, 16);
            dc.DrawRoundedRectangle(Frozen(Color.FromArgb(0xE0, 0x20, 0x20, 0x20)), null, msgBox, 3, 3);
            Text(dc, r.Message, msgBox.X + 4, msgBox.Y + 1, 10, outline, msgBox.Width - 8);
        }

        // ------------------------------------------------------------------ month

        private void RenderMonth(DrawingContext dc, Theme t) {
            var s = S;
            int focusMonth = s.Nights[s.Nights.Count / 2].Night.Month;
            double cw = CellW, ch = CellH;

            dc.DrawRectangle(t.Panel, null, new Rect(0, 0, ActualWidth, WeekdayH));
            for (int c = 0; c < 7 && c < s.Nights.Count; c++) {
                Text(dc, s.Nights[c].Night.ToString("dddd", CultureInfo.CurrentCulture), c * cw, 4, 11, t.Muted, cw, isBold: true, align: TextAlignment.Center);
            }

            for (int i = 0; i < s.Nights.Count; i++) {
                var n = s.Nights[i];
                var cell = CellRect(i);
                bool inMonth = n.Night.Month == focusMonth;
                dc.DrawRectangle(inMonth ? t.Background : t.Panel, t.FaintPen, cell);

                // Tint nights where the selected target can be shot.
                TimeSpan valid = TimeSpan.Zero;
                if (s.ActiveTargetId != null && s.ActiveWindows.TryGetValue(n.Night, out var windows)) {
                    valid = TimeRange.Total(windows);
                    if (valid > TimeSpan.Zero) {
                        byte a = (byte)Math.Clamp(0x14 + valid.TotalHours * 6, 0x14, 0x50);
                        dc.DrawRectangle(Colors2.Brush(s.ActiveTargetColor, a), null, cell);
                    }
                }
                if (n.IsPast) dc.DrawRectangle(t.Past, null, cell);

                // Date and moon.
                var dateBox = new Rect(cell.X + 3, cell.Y + 3, 24, 17);
                if (n.IsTonight) {
                    dc.DrawEllipse(t.Now, null, new Point(dateBox.X + 10, dateBox.Y + 8.5), 10, 9);
                    Text(dc, n.Night.Day.ToString(CultureInfo.CurrentCulture), dateBox.X, dateBox.Y + 1, 11.5, Brushes.White, 20, isBold: true, align: TextAlignment.Center);
                } else {
                    Text(dc, n.Night.Day.ToString(CultureInfo.CurrentCulture), dateBox.X + 2, dateBox.Y + 1, 11.5, inMonth ? t.Text : t.Muted, 22, isBold: true);
                }
                hits.Add(new Hit(HitKind.Header, dateBox, n.Night, Text: "Open this night"));
                string right = Moon(n.MoonIllumination);
                if (valid > TimeSpan.Zero && cw > 120) right = $"{Fmt.Duration(valid)} usable · " + right;
                Text(dc, right, cell.X + 28, cell.Y + 5, 9.5, t.Muted, cw - 32, align: TextAlignment.Right);

                // Mini timeline of the night along the bottom of the cell.
                var bar = new Rect(cell.X + 4, cell.Bottom - 9, cw - 8, 5);
                DrawMiniTimeline(dc, t, n, bar, windows: s.ActiveTargetId != null && s.ActiveWindows.TryGetValue(n.Night, out var w2) ? w2 : null);

                // Chips: planned events, then done sessions, then milestones.
                double y = cell.Y + 22, bottom = cell.Bottom - 12;
                var chips = new List<(HitKind Kind, string Id, string TargetId, string Label, string Color, bool Filled, string Tip, EventView? Ev)>();
                foreach (var ev in s.Events.Where(e => e.Night == n.Night).OrderBy(e => e.StartUtc)) {
                    string mark = ev.OutsideConditions > TimeSpan.Zero && !ev.IsPast ? "⚠ " : "";
                    chips.Add((HitKind.Event, ev.Id, ev.TargetId, $"{Glyph(ev.Status)}{mark}{Clock(ev.StartUtc)} {ev.Title}", ev.Color, true, EventTip(ev), ev));
                }
                foreach (var se in s.Sessions.Where(e => e.Night == n.Night).OrderBy(e => e.StartUtc)) {
                    // Frames taken with no event behind them; frames inside events show on the event itself.
                    if (s.Events.Any(e => e.Night == n.Night && e.TargetId == se.TargetId)) continue;
                    chips.Add((HitKind.Session, se.Key, se.TargetId ?? "", $"✓ {se.Title} · {se.Frames}", se.Color, false, $"{se.Title} (not planned)\n{se.Detail}", null));
                }

                int fit = Math.Max(0, (int)((bottom - y) / (ChipH + 2)));
                if (chips.Count > fit && fit > 0) fit--;
                for (int k = 0; k < chips.Count && k < fit; k++) {
                    var chip = chips[k];
                    var box = new Rect(cell.X + 3, y, cw - 6, ChipH);
                    var c = Colors2.Parse(chip.Color, Colors.Gray);
                    bool dragged = preview != null && preview.EventId == chip.Id && dragMode == DragMode.Move;
                    if (chip.Filled && chip.Ev is EventView cev) {
                        bool sel = selectedEventIds.Contains(chip.Id);
                        bool missedChip = cev.Status == EventStatus.Missed;
                        bool finishedChip = cev.IsPast || cev.IsSettled;
                        if (finishedChip) dc.PushOpacity(missedChip ? 0.75 : 0.45);
                        if (missedChip) {
                            dc.DrawRoundedRectangle(null, DashedPen(MissedRed, 1.2), box, 3, 3);
                            Text(dc, chip.Label, box.X + 4, box.Y + 1, 10, Frozen(MissedRed), box.Width - 6);
                        } else {
                            var chipPen = cev.OutsideConditions > TimeSpan.Zero && !finishedChip ? DashedPen(WarnAmber, 1.2) : null;
                            dc.DrawRoundedRectangle(Colors2.Brush(chip.Color, dragged ? (byte)0x40 : (byte)0xE0), chipPen, box, 3, 3);
                            Text(dc, chip.Label, box.X + 4, box.Y + 1, 10, TextOn(c), box.Width - 6);
                        }
                        if (finishedChip) dc.Pop();
                        if (sel) dc.DrawRoundedRectangle(null, FreezeIfCan(new Pen(t.Text, 1.5)), box, 3, 3);
                    } else {
                        dc.DrawRoundedRectangle(null, new Pen(Colors2.Brush(chip.Color, 0xC0), 1), box, 3, 3);
                        Text(dc, chip.Label, box.X + 4, box.Y + 1, 10, t.Text, box.Width - 6);
                    }
                    hits.Add(new Hit(chip.Kind, box, n.Night, chip.Id, chip.TargetId, chip.Tip));
                    y += ChipH + 2;
                }
                if (chips.Count > fit) {
                    var more = new Rect(cell.X + 3, y, cw - 6, ChipH);
                    Text(dc, $"+{chips.Count - fit} more", more.X + 4, more.Y + 1, 10, t.Muted, more.Width - 6, isBold: true);
                    hits.Add(new Hit(HitKind.More, more, n.Night, Text: "Open this night"));
                }

                hits.Insert(0, new Hit(HitKind.Cell, cell, n.Night));
            }

            DrawMonthPreview(dc, t);
        }

        private void DrawMiniTimeline(DrawingContext dc, Theme t, NightView n, Rect bar, List<TimeRange>? windows) {
            double span = AxisSpan;
            double X(DateTime utc) => bar.X + Math.Clamp((NightMath.ClockHoursAfterNoon(utc, n.Night, S.Zone) - S.AxisStartHours) / span, 0, 1) * bar.Width;
            dc.DrawRoundedRectangle(t.Panel, null, bar, 2, 2);
            if (n.Dark is TimeRange dark) dc.DrawRectangle(t.Cell, null, new Rect(X(dark.StartUtc), bar.Y, Math.Max(0, X(dark.EndUtc) - X(dark.StartUtc)), bar.Height));
            if (windows != null) {
                var edge = Colors2.Brush(S.ActiveTargetColor, 0x90);
                foreach (var w in windows) dc.DrawRectangle(edge, null, new Rect(X(w.StartUtc), bar.Y + bar.Height - 1.5, Math.Max(1, X(w.EndUtc) - X(w.StartUtc)), 1.5));
            }
            foreach (var ev in S.Events.Where(e => e.Night == n.Night)) {
                dc.DrawRectangle(Colors2.Brush(ev.Color), null, new Rect(X(ev.StartUtc), bar.Y, Math.Max(1.5, X(ev.EndUtc) - X(ev.StartUtc)), bar.Height - 1.5));
            }
        }

        private void DrawMonthPreview(DrawingContext dc, Theme t) {
            if (multiPreview != null) {
                var mbrush = !multiPreview.CanCommit ? t.Bad : multiPreview.Outcome == PlacementOutcome.Clipped ? t.Warn : t.Good;
                foreach (var night in multiPreview.Items.Select(i => i.Night).Distinct()) {
                    int mi = NightIndex(night);
                    if (mi < 0) continue;
                    var mcell = CellRect(mi);
                    dc.DrawRectangle(null, FreezeIfCan(new Pen(mbrush, 2.5)), new Rect(mcell.X + 1.5, mcell.Y + 1.5, mcell.Width - 3, mcell.Height - 3));
                }
                int ami = NightIndex(multiPreview.Night);
                if (ami >= 0) {
                    var acell = CellRect(ami);
                    var mbox = new Rect(acell.X + 3, acell.Bottom - 30, acell.Width - 6, 16);
                    dc.DrawRoundedRectangle(Frozen(Color.FromArgb(0xE0, 0x20, 0x20, 0x20)), null, mbox, 3, 3);
                    Text(dc, multiPreview.Message, mbox.X + 4, mbox.Y + 1, 10, mbrush, mbox.Width - 8, isBold: true);
                }
                return;
            }
            if (groupPreview != null) {
                int gi = NightIndex(groupPreview.Night);
                if (gi < 0) return;
                var gcell = CellRect(gi);
                var gbrush = !groupPreview.CanCommit ? t.Bad : groupPreview.Overflow > TimeSpan.FromSeconds(30) ? t.Warn : t.Good;
                dc.DrawRectangle(null, new Pen(gbrush, 2.5), new Rect(gcell.X + 1.5, gcell.Y + 1.5, gcell.Width - 3, gcell.Height - 3));
                var gbox = new Rect(gcell.X + 3, gcell.Bottom - 30, gcell.Width - 6, 16);
                dc.DrawRoundedRectangle(Frozen(Color.FromArgb(0xE0, 0x20, 0x20, 0x20)), null, gbox, 3, 3);
                Text(dc, groupPreview.Message, gbox.X + 4, gbox.Y + 1, 10, gbrush, gbox.Width - 8, isBold: true);
                return;
            }
            if (preview == null) return;
            int i = NightIndex(preview.Night);
            if (i < 0) return;
            var cell = CellRect(i);
            var r = preview.Result;
            var brush = r.Outcome switch { PlacementOutcome.Fits => t.Good, PlacementOutcome.Clipped => t.Warn, _ => t.Bad };
            dc.DrawRectangle(null, new Pen(brush, 2.5), new Rect(cell.X + 1.5, cell.Y + 1.5, cell.Width - 3, cell.Height - 3));
            var msg = r.Placed is TimeRange p ? $"{Clock(p.StartUtc)}–{Clock(p.EndUtc)} · {Fmt.Duration(p.Duration)}" : r.Message;
            var box = new Rect(cell.X + 3, cell.Bottom - 30, cell.Width - 6, 16);
            dc.DrawRoundedRectangle(Frozen(Color.FromArgb(0xE0, 0x20, 0x20, 0x20)), null, box, 3, 3);
            Text(dc, msg, box.X + 4, box.Y + 1, 10, brush, box.Width - 8, isBold: true);
        }

        private string Clock(DateTime utc) => NightMath.ToLocal(utc, S.Zone).ToString("HH:mm", CultureInfo.InvariantCulture);

        private static string Moon(double? illumination) => illumination is double m ? $"Moon {m * 100:0}%" : "";

        // ================================================================== error guard

        private static string lastReported = "";

        private void Guard(Action action, [System.Runtime.CompilerServices.CallerMemberName] string where = "") {
            try {
                action();
            } catch (Exception ex) {
                Report(where, ex);
                try {
                    if (dragMode != DragMode.None || preview != null || groupPreview != null || multiPreview != null) EndDrag();
                    Controller?.ShowMessage("Something went wrong in the calendar: " + ex.Message + " (details in the NINA log)");
                } catch (Exception) {
                    // Leave it; NINA keeps running.
                }
            }
        }

        private static void Report(string where, Exception ex) {
            var key = where + "|" + ex.GetType().FullName + "|" + ex.Message;
            if (key == lastReported) return;
            lastReported = key;
            try { NINA.Core.Utility.Logger.Error($"Target Board calendar: {where} failed", ex); } catch (Exception) { }
        }

        // ================================================================== mouse: move, resize, select

        private Hit? HitAt(Point p) {
            for (int i = hits.Count - 1; i >= 0; i--) {
                if (hits[i].Box.Contains(p)) return hits[i];
            }
            return null;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) => Guard(() => HandleMouseLeftButtonDown(e));

        private static bool CtrlDown => (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        private static bool ShiftDown => (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        private void HandleMouseLeftButtonDown(MouseButtonEventArgs e) {
            base.OnMouseLeftButtonDown(e);
            Focus();
            var controller = Controller;
            if (controller == null) return;
            // Any click closes the filter view.
            if (filterView != null) {
                filterView = null;
                InvalidateVisual();
                e.Handled = true;
                return;
            }
            var p = e.GetPosition(this);
            var hit = HitAt(p);

            if (hit?.Kind is HitKind.Header or HitKind.More) {
                if (!(hit.Kind == HitKind.Header && S.Mode == BoardViewMode.Night)) controller.OpenNight(hit.Night);
                e.Handled = true;
                return;
            }
            if (hit?.Kind is HitKind.Event or HitKind.Resize) {
                if (e.ClickCount == 2 && !CtrlDown) {
                    EndDrag();
                    OpenFilterView(hit.Id);
                    e.Handled = true;
                    return;
                }
                if (CtrlDown) {
                    // Ctrl+click adds or removes one event from the selection; no drag.
                    if (!selectedEventIds.Remove(hit.Id)) { selectedEventIds.Add(hit.Id); selectedEventId = hit.Id; }
                    else if (selectedEventId == hit.Id) selectedEventId = selectedEventIds.FirstOrDefault();
                    controller.ShowMessage(SelectionText());
                    InvalidateVisual();
                    e.Handled = true;
                    return;
                }
                if (IsMonth && hit.Kind == HitKind.Resize) hit = hit with { Kind = HitKind.Event };
                if (!selectedEventIds.Contains(hit.Id)) {
                    selectedEventIds.Clear();
                    selectedEventIds.Add(hit.Id);
                }
                selectedEventId = hit.Id;
                pressHit = hit;
                pressPoint = p;
                dragMode = DragMode.Pending;
                var ev = S.Events.FirstOrDefault(x => x.Id == hit.Id);
                if (ev != null && !IsMonth) grabOffsetHours = HoursAtY(p.Y) - NightMath.ClockHoursAfterNoon(ev.StartUtc, ev.Night, S.Zone);
                CaptureMouse();
                InvalidateVisual();
                e.Handled = true;
                return;
            }
            if (hit?.Kind is HitKind.Session or HitKind.Milestone && !string.IsNullOrEmpty(hit.TargetId)) {
                controller.SelectTarget(hit.TargetId);
                e.Handled = true;
                return;
            }

            if (!CtrlDown) {
                selectedEventIds.Clear();
                selectedEventId = null;
            }
            // Double-click on empty time: place the selected target there (like creating an event in a calendar).
            if (e.ClickCount == 2 && controller.SelectedTargetId is string targetId && TryMap(p, out var night, out var utc)) {
                var pv = controller.PreviewNew(targetId, night, utc, ShiftDown);
                controller.Commit(pv);
            }
            InvalidateVisual();
        }

        private string SelectionText() => selectedEventIds.Count switch {
            0 => "",
            1 => "1 event selected.",
            _ => $"{selectedEventIds.Count} events selected."
        };

        protected override void OnMouseMove(MouseEventArgs e) => Guard(() => HandleMouseMove(e));

        private void HandleMouseMove(MouseEventArgs e) {
            base.OnMouseMove(e);
            var p = e.GetPosition(this);
            var controller = Controller;

            if (dragMode != DragMode.None && pressHit != null && controller != null && e.LeftButton == MouseButtonState.Pressed) {
                if (dragMode == DragMode.Pending) {
                    if ((p - pressPoint).Length < 4) return;
                    dragMode = pressHit.Kind == HitKind.Resize ? DragMode.Resize : DragMode.Move;
                }
                UpdateInternalPreview(controller, p);
                return;
            }
            if (filterView != null) { Cursor = null; return; }

            var hit = HitAt(p);
            Cursor = hit?.Kind switch {
                HitKind.Resize => Cursors.SizeNS,
                HitKind.Event => Cursors.SizeAll,
                HitKind.Header or HitKind.More or HitKind.Session or HitKind.Milestone => Cursors.Hand,
                _ => null
            };
            string key = hit == null ? "" : hit.Kind + hit.Id + hit.Night;
            if (key != hoverKey) {
                hoverKey = key;
                ToolTip = string.IsNullOrEmpty(hit?.Text) ? null : hit!.Text;
            }
        }

        private void UpdateInternalPreview(IBoardController controller, Point p) {
            var ev = S.Events.FirstOrDefault(x => x.Id == pressHit!.Id);
            if (ev == null) return;
            bool force = ShiftDown;
            PlacementPreview? next = null;
            string key;
            if (dragMode == DragMode.Resize && !IsMonth) {
                var end = NightMath.FromClockHoursAfterNoon(HoursAtY(p.Y), ev.Night, S.Zone);
                key = "r" + Scheduler.Snap(end, 5).Ticks + (force ? "f" : "");
                if (key != previewKey) next = controller.PreviewResize(ev.Id, end, force);
            } else {
                if (!TryMap(p, out var night, out var utc)) return;
                DateTime desired;
                if (IsMonth) {
                    // Same clock time on the new night.
                    desired = NightMath.FromClockHoursAfterNoon(NightMath.ClockHoursAfterNoon(ev.StartUtc, ev.Night, S.Zone), night, S.Zone);
                } else {
                    desired = NightMath.FromClockHoursAfterNoon(HoursAtY(p.Y) - grabOffsetHours, night, S.Zone);
                }
                if (selectedEventIds.Count > 1 && selectedEventIds.Contains(ev.Id)) {
                    // Dragging one of several selected events moves them all.
                    key = "mm" + night.DayNumber + "|" + Scheduler.Snap(desired, 5).Ticks + (force ? "f" : "");
                    previewPoint = p;
                    if (key == previewKey) return;
                    previewKey = key;
                    preview = null;
                    multiPreview = controller.PreviewMoveMany(selectedEventIds.ToList(), ev.Id, night, IsMonth ? null : desired, force);
                    controller.ShowMessage(multiPreview.Message);
                    InvalidateVisual();
                    return;
                }
                key = "m" + night.DayNumber + "|" + Scheduler.Snap(desired, 5).Ticks + (force ? "f" : "");
                if (key != previewKey) next = controller.PreviewMove(ev.Id, night, desired, force);
            }
            previewPoint = p;
            if (next != null) {
                previewKey = key;
                preview = next;
                controller.ShowMessage(next.Result.Message);
                InvalidateVisual();
            }
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => Guard(() => HandleMouseLeftButtonUp(e));

        private void HandleMouseLeftButtonUp(MouseButtonEventArgs e) {
            base.OnMouseLeftButtonUp(e);
            var controller = Controller;
            var hit = pressHit;
            var mode = dragMode;
            var pv = preview;
            var mpv = multiPreview;
            EndDrag();
            if (controller == null || hit == null) return;
            if (mode is DragMode.Move or DragMode.Resize) {
                if (mpv != null) controller.CommitMany(mpv);
                else if (pv != null) controller.Commit(pv);
            } else if (mode == DragMode.Pending) {
                // A plain click: this event alone is selected, and its target too.
                selectedEventIds.Clear();
                selectedEventIds.Add(hit.Id);
                selectedEventId = hit.Id;
                if (!string.IsNullOrEmpty(hit.TargetId)) controller.SelectTarget(hit.TargetId);
                InvalidateVisual();
            }
            e.Handled = true;
        }

        protected override void OnLostMouseCapture(MouseEventArgs e) => Guard(() => HandleLostMouseCapture(e));

        private void HandleLostMouseCapture(MouseEventArgs e) {
            base.OnLostMouseCapture(e);
            if (dragMode != DragMode.None) EndDrag();
        }

        private void EndDrag() {
            dragMode = DragMode.None;
            pressHit = null;
            preview = null;
            groupPreview = null;
            multiPreview = null;
            previewKey = "";
            if (IsMouseCaptured) ReleaseMouseCapture();
            InvalidateVisual();
        }

        protected override void OnKeyDown(KeyEventArgs e) => Guard(() => HandleKeyDown(e));

        private void HandleKeyDown(KeyEventArgs e) {
            base.OnKeyDown(e);
            var controller = Controller;
            if (controller == null) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

            if (key is Key.LeftShift or Key.RightShift && dragMode is DragMode.Move or DragMode.Resize && pressHit != null) {
                // Shift pressed mid-drag: try again ignoring the target's conditions.
                previewKey = "";
                UpdateInternalPreview(controller, previewPoint);
                return;
            }
            if (key == Key.Escape) {
                if (dragMode != DragMode.None) { EndDrag(); controller.ShowMessage("Cancelled."); }
                else if (filterView != null) { filterView = null; InvalidateVisual(); controller.ShowMessage(""); }
                else if (selectedEventIds.Count > 0) { selectedEventIds.Clear(); selectedEventId = null; InvalidateVisual(); controller.ShowMessage(""); }
                e.Handled = true;
            } else if (key == Key.Delete && selectedEventIds.Count > 0 && dragMode == DragMode.None) {
                controller.RemoveEvents(selectedEventIds.ToList());
                selectedEventIds.Clear();
                selectedEventId = null;
                e.Handled = true;
            } else if (ctrl && key == Key.Z) {
                controller.Undo();
                e.Handled = true;
            } else if (ctrl && key is Key.Right or Key.Left && selectedEventIds.Count > 0 && dragMode == DragMode.None) {
                controller.ShiftEvents(selectedEventIds.ToList(), key == Key.Right ? 1 : -1);
                e.Handled = true;
            }
        }

        protected override void OnKeyUp(KeyEventArgs e) => Guard(() => HandleKeyUp(e));

        private void HandleKeyUp(KeyEventArgs e) {
            base.OnKeyUp(e);
            var controller = Controller;
            if (controller != null && e.Key is Key.LeftShift or Key.RightShift && dragMode is DragMode.Move or DragMode.Resize && pressHit != null) {
                previewKey = "";
                UpdateInternalPreview(controller, previewPoint);
            }
        }

        private int wheelRemainder;

        protected override void OnMouseWheel(MouseWheelEventArgs e) => Guard(() => HandleMouseWheel(e));

        /// <summary>The wheel moves the calendar a night at a time: down goes ahead, up goes back.</summary>
        private void HandleMouseWheel(MouseWheelEventArgs e) {
            base.OnMouseWheel(e);
            e.Handled = true;
            var controller = Controller;
            if (controller == null || filterView != null || dragMode != DragMode.None) return;
            // Touchpads send small steps; act on each whole notch (120).
            wheelRemainder += e.Delta;
            int notches = wheelRemainder / 120;
            if (notches == 0) return;
            wheelRemainder -= notches * 120;
            controller.ScrollDays(-notches);
        }

        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e) => Guard(() => HandleMouseRightButtonUp(e));

        private void HandleMouseRightButtonUp(MouseButtonEventArgs e) {
            base.OnMouseRightButtonUp(e);
            var controller = Controller;
            if (controller == null || dragMode != DragMode.None) return;
            if (filterView != null) { filterView = null; InvalidateVisual(); }
            var p = e.GetPosition(this);
            var hit = HitAt(p);
            ContextMenu? menu;
            if (hit != null && hit.Kind is HitKind.Event or HitKind.Resize) menu = EventMenu(controller, hit);
            else if (hit != null && hit.Kind is HitKind.Header or HitKind.Cell) menu = NightMenu(controller, hit.Night);
            else if (TryMap(p, out var night, out _)) menu = NightMenu(controller, night);
            else menu = UndoOnlyMenu(controller);
            if (menu == null || menu.Items.Count == 0) return;
            menu.PlacementTarget = this;
            menu.IsOpen = true;
            e.Handled = true;
        }

        private ContextMenu EventMenu(IBoardController controller, Hit hit) {
            if (!selectedEventIds.Contains(hit.Id)) {
                selectedEventIds.Clear();
                selectedEventIds.Add(hit.Id);
            }
            selectedEventId = hit.Id;
            InvalidateVisual();
            var ids = selectedEventIds.ToList();
            var evs = S.Events.Where(x => selectedEventIds.Contains(x.Id)).ToList();
            int k = ids.Count;
            string some = k == 1 ? "" : $" {k} events";
            var menu = new ContextMenu();
            var targetId = hit.TargetId;
            var night = hit.Night;

            if (k == 1) menu.Items.Add(MakeItem(evs.FirstOrDefault()?.IsSettled == true || evs.FirstOrDefault()?.IsPast == true ? "Show frames taken" : "Show filters", () => OpenFilterView(hit.Id)));
            menu.Items.Add(MakeItem(k == 1 ? "Remove event" : $"Remove{some}", () => {
                controller.RemoveEvents(ids);
                selectedEventIds.Clear();
                selectedEventId = null;
            }));
            bool anyAhead = evs.Any(x => !x.IsSettled && !x.IsPast);
            var missed = evs.Where(x => x.Status == EventStatus.Missed).Select(x => x.Id).ToList();
            if (anyAhead || missed.Count > 0) menu.Items.Add(MakeItem(k == 1 ? "Move one night later" : $"Move{some} one night later", () => controller.ShiftEvents(ids, 1)));
            if (anyAhead) menu.Items.Add(MakeItem(k == 1 ? "Move one night earlier" : $"Move{some} one night earlier", () => controller.ShiftEvents(ids, -1)));
            if (k == 1 && anyAhead) menu.Items.Add(MakeItem("Split in two", () => controller.SplitEvent(hit.Id)));
            if (missed.Count > 0) {
                menu.Items.Add(new Separator());
                menu.Items.Add(MakeItem(missed.Count == 1 ? "Add the missed time to this target's next event" : "Add the missed time to each target's next event", () => controller.AddMissedTime(missed)));
                var firstMissedNight = evs.Where(x => x.Status == EventStatus.Missed).Min(x => x.Night);
                menu.Items.Add(MakeItem($"Shift the plan one night later from {firstMissedNight.ToString("ddd d MMM", CultureInfo.CurrentCulture)}", () => controller.ShiftPlanFrom(firstMissedNight)));
            }
            menu.Items.Add(new Separator());
            if (k == 1) menu.Items.Add(MakeItem("Select target", () => controller.SelectTarget(targetId)));
            if (S.Mode != BoardViewMode.Night) menu.Items.Add(MakeItem("Open this night", () => controller.OpenNight(night)));
            AddUndo(controller, menu);
            return menu;
        }

        private ContextMenu NightMenu(IBoardController controller, DateOnly night) {
            var n = S.Nights.FirstOrDefault(x => x.Night == night);
            var menu = new ContextMenu();
            string day = night.ToString("ddd d MMM", CultureInfo.CurrentCulture);
            if (n != null && n.Missed > 0) {
                var missed = S.Events.Where(x => x.Night == night && x.Status == EventStatus.Missed).Select(x => x.Id).ToList();
                menu.Items.Add(MakeItem($"Shift the plan one night later from {day}", () => controller.ShiftPlanFrom(night)));
                menu.Items.Add(MakeItem("Add the missed time to each target's next event", () => controller.AddMissedTime(missed)));
            } else if (S.Events.Any(x => x.Night >= night && !x.IsSettled && !x.IsPast)) {
                menu.Items.Add(MakeItem($"Shift the plan one night later from {day}", () => controller.ShiftPlanFrom(night)));
            }
            if (n != null && n.CanSettle) menu.Items.Add(MakeItem($"Settle {day} now (fit events to what was done)", () => controller.SettleNight(night)));
            if (S.Mode != BoardViewMode.Night) menu.Items.Add(MakeItem("Open this night", () => controller.OpenNight(night)));
            AddUndo(controller, menu);
            return menu;
        }

        private ContextMenu? UndoOnlyMenu(IBoardController controller) {
            if (!S.CanUndo) return null;
            var menu = new ContextMenu();
            AddUndo(controller, menu);
            return menu;
        }

        private void AddUndo(IBoardController controller, ContextMenu menu) {
            if (!S.CanUndo) return;
            if (menu.Items.Count > 0 && menu.Items[^1] is not Separator) menu.Items.Add(new Separator());
            menu.Items.Add(MakeItem($"Undo {S.UndoLabel} (Ctrl+Z)", controller.Undo));
        }

        private static MenuItem MakeItem(string header, Action action) {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => Util.Safe.Run(header, action);
            return item;
        }

        // ================================================================== drag in from the target list

        protected override void OnDragEnter(DragEventArgs e) => Guard(() => HandleDragEnter(e));

        private void HandleDragEnter(DragEventArgs e) {
            base.OnDragEnter(e);
            UpdateExternalPreview(e);
        }

        protected override void OnDragOver(DragEventArgs e) => Guard(() => HandleDragOver(e));

        private void HandleDragOver(DragEventArgs e) {
            base.OnDragOver(e);
            UpdateExternalPreview(e);
        }

        private void UpdateExternalPreview(DragEventArgs e) {
            e.Handled = true;
            e.Effects = DragDropEffects.None;
            var controller = Controller;
            if (controller == null) return;
            if (e.Data.GetDataPresent(GroupDragFormat) && e.Data.GetData(GroupDragFormat) is string groupId) {
                var gp = e.GetPosition(this);
                previewPoint = gp;
                if (!TryMap(gp, out var gNight, out var gUtc)) {
                    if (groupPreview != null) { groupPreview = null; previewKey = ""; InvalidateVisual(); }
                    return;
                }
                string gKey = "g" + groupId + gNight.DayNumber + "|" + (gUtc is DateTime gu ? Scheduler.Snap(gu, 5).Ticks : 0);
                if (gKey != previewKey) {
                    previewKey = gKey;
                    groupPreview = controller.PreviewGroup(groupId, gNight, gUtc);
                    controller.ShowMessage(groupPreview.Message);
                    InvalidateVisual();
                }
                e.Effects = groupPreview != null && groupPreview.CanCommit ? DragDropEffects.Copy : DragDropEffects.None;
                return;
            }
            if (!e.Data.GetDataPresent(DragFormat) || e.Data.GetData(DragFormat) is not string targetId) return;
            var p = e.GetPosition(this);
            previewPoint = p;
            if (!TryMap(p, out var night, out var utc)) {
                if (preview != null) { preview = null; previewKey = ""; InvalidateVisual(); }
                return;
            }
            bool force = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0;
            string key = "n" + targetId + night.DayNumber + "|" + (utc is DateTime u ? Scheduler.Snap(u, 5).Ticks : 0) + (force ? "f" : "");
            if (key != previewKey) {
                previewKey = key;
                preview = controller.PreviewNew(targetId, night, utc, force);
                controller.ShowMessage(preview.Result.Message);
                InvalidateVisual();
            }
            e.Effects = preview != null && preview.Result.CanCommit ? DragDropEffects.Copy : DragDropEffects.None;
        }

        protected override void OnDragLeave(DragEventArgs e) => Guard(() => HandleDragLeave(e));

        private void HandleDragLeave(DragEventArgs e) {
            base.OnDragLeave(e);
            preview = null;
            groupPreview = null;
            previewKey = "";
            InvalidateVisual();
        }

        protected override void OnDrop(DragEventArgs e) => Guard(() => HandleDrop(e));

        private void HandleDrop(DragEventArgs e) {
            base.OnDrop(e);
            var pv = preview;
            var gpv = groupPreview;
            preview = null;
            groupPreview = null;
            previewKey = "";
            if (gpv != null) Controller?.CommitGroup(gpv);
            else if (pv != null) Controller?.Commit(pv);
            InvalidateVisual();
            e.Handled = true;
        }
    }
}
