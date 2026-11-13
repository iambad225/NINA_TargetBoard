using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace TargetBoard.Util {

    /// <summary>
    /// Mouse wheel scrolling with a speed factor and an eased glide.
    /// Put <c>SmoothScroll.Speed</c> on a ScrollViewer to turn it on; 1 is Windows' usual step. It only ever sees the wheel
    /// while the mouse is over that ScrollViewer.
    /// <c>SmoothScroll.Smooth</c> switches the glide off (jump straight to the new position).
    /// The content must scroll by pixels (the ScrollViewer default; a ListBox would need CanContentScroll="False").
    /// </summary>
    public static class SmoothScroll {

        public static readonly DependencyProperty SpeedProperty = DependencyProperty.RegisterAttached(
            "Speed", typeof(double), typeof(SmoothScroll), new PropertyMetadata(double.NaN, OnSpeedChanged));

        public static double GetSpeed(DependencyObject d) => (double)d.GetValue(SpeedProperty);
        public static void SetSpeed(DependencyObject d, double value) => d.SetValue(SpeedProperty, value);

        public static readonly DependencyProperty SmoothProperty = DependencyProperty.RegisterAttached(
            "Smooth", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(true));

        public static bool GetSmooth(DependencyObject d) => (bool)d.GetValue(SmoothProperty);
        public static void SetSmooth(DependencyObject d, bool value) => d.SetValue(SmoothProperty, value);

        /// <summary>Height of one wheel "line" in pixels, as WPF uses for pixel scrolling.</summary>
        private const double LinePixels = 16;

        private static readonly ConditionalWeakTable<ScrollViewer, Glide> Glides = new();

        private static void OnSpeedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) {
            if (d is not UIElement element) return;
            element.PreviewMouseWheel -= OnWheel;
            if (!double.IsNaN((double)e.NewValue)) element.PreviewMouseWheel += OnWheel;
        }

        private static void OnWheel(object sender, MouseWheelEventArgs e) {
            if (e.Handled || sender is not DependencyObject host) return;
            Safe.Run("scrolling", () => {
                var viewer = host as ScrollViewer ?? FindViewer(host);
                if (viewer == null || viewer.ScrollableHeight <= 0) return;
                // A text box or inner list that can scroll itself keeps the wheel.
                if (InnerScrollerTakesIt(e.OriginalSource as DependencyObject, viewer)) return;

                double speed = GetSpeed(host);
                if (double.IsNaN(speed) || speed <= 0) speed = 1;
                int lines = SystemParameters.WheelScrollLines;
                double step = lines > 0 ? lines * LinePixels : viewer.ViewportHeight;   // -1 means "one page per notch"
                double delta = -e.Delta / 120.0 * step * speed;

                e.Handled = true;
                Glides.GetValue(viewer, v => new Glide(v)).By(delta, GetSmooth(host));
            });
        }

        private static bool InnerScrollerTakesIt(DependencyObject? d, ScrollViewer outer) {
            while (d != null && !ReferenceEquals(d, outer)) {
                if (d is ScrollViewer inner && inner.ScrollableHeight > 0) return true;
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        private static ScrollViewer? FindViewer(DependencyObject root) {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++) {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is ScrollViewer sv) return sv;
                var found = FindViewer(child);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>Moves one scroll viewer towards a target offset a little each frame, slowing as it arrives.</summary>
        private sealed class Glide {
            /// <summary>Seconds for the gap to shrink to about a third; the glide is over in about a quarter of a second.</summary>
            private const double TimeConstant = 0.075;

            private readonly ScrollViewer viewer;
            private double target, lastSet;
            private bool running;
            private TimeSpan lastFrame;

            public Glide(ScrollViewer viewer) {
                this.viewer = viewer;
                viewer.Unloaded += (_, _) => Stop();
            }

            public void By(double delta, bool smooth) {
                // Start from where the list is, unless a glide is under way and nobody else has moved it.
                if (!running || Math.Abs(viewer.VerticalOffset - lastSet) > 1.5) target = viewer.VerticalOffset;
                target = Math.Clamp(target + delta, 0, viewer.ScrollableHeight);
                if (!smooth) {
                    Stop();
                    viewer.ScrollToVerticalOffset(target);
                    return;
                }
                if (running) return;
                running = true;
                lastFrame = TimeSpan.Zero;
                lastSet = viewer.VerticalOffset;
                CompositionTarget.Rendering += OnFrame;
            }

            private void OnFrame(object? sender, EventArgs e) {
                try {
                    var now = e is RenderingEventArgs r ? r.RenderingTime : TimeSpan.Zero;
                    if (lastFrame != TimeSpan.Zero && now == lastFrame) return;   // raised twice for one frame
                    double dt = lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Clamp((now - lastFrame).TotalSeconds, 0.001, 0.1);
                    lastFrame = now;

                    double current = viewer.VerticalOffset;
                    // The scroll bar, keyboard or a list rebuild moved it: let that win.
                    if (Math.Abs(current - lastSet) > 1.5) { Stop(); return; }

                    target = Math.Clamp(target, 0, viewer.ScrollableHeight);
                    double gap = target - current;
                    if (Math.Abs(gap) < 0.5) {
                        viewer.ScrollToVerticalOffset(target);
                        Stop();
                        return;
                    }
                    double next = current + gap * (1 - Math.Exp(-dt / TimeConstant));
                    if (Math.Abs(next - current) < 0.5) next = current + Math.Sign(gap) * Math.Min(0.5, Math.Abs(gap));
                    viewer.ScrollToVerticalOffset(next);
                    lastSet = next;
                } catch (Exception ex) {
                    Stop();
                    NINA.Core.Utility.Logger.Error("Target Board: smooth scrolling stopped", ex);
                }
            }

            private void Stop() {
                if (!running) return;
                running = false;
                CompositionTarget.Rendering -= OnFrame;
            }
        }
    }
}
