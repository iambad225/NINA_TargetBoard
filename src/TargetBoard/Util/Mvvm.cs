using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace TargetBoard.Util {

    public abstract class ObservableBase : INotifyPropertyChanged {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void Raise([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null) {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }
    }

    public sealed class BoardCommand : ICommand {
        private readonly Action<object?> execute;
        private readonly Func<object?, bool>? canExecute;

        public BoardCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) {
            this.execute = execute;
            this.canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object? parameter) {
            try {
                return canExecute?.Invoke(parameter) ?? true;
            } catch (Exception) {
                return false;
            }
        }

        public void Execute(object? parameter) => Safe.Run("command", () => execute(parameter));
    }

    /// <summary>Runs plugin code so that an error is logged and shown, never thrown into NINA.</summary>
    public static class Safe {
        public static void Run(string what, Action action) {
            try {
                action();
            } catch (Exception ex) {
                try {
                    NINA.Core.Utility.Logger.Error($"Target Board: {what} failed", ex);
                    NINA.Core.Utility.Notification.Notification.ShowWarning($"Target Board: {what} failed: {ex.Message}");
                } catch (Exception) {
                    // Logging failed too; keep NINA running.
                }
            }
        }
    }

    public static class Colors2 {
        public static Color Parse(string? hex, Color fallback) {
            if (string.IsNullOrWhiteSpace(hex)) return fallback;
            try {
                return (Color)ColorConverter.ConvertFromString(hex);
            } catch (FormatException) {
                return fallback;
            }
        }

        public static SolidColorBrush Brush(string? hex, byte alpha = 255) {
            var c = Parse(hex, Color.FromRgb(0x9E, 0x9E, 0x9E));
            var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            b.Freeze();
            return b;
        }
    }

    /// <summary>true → Visible, false → Collapsed. Parameter "invert" flips it.</summary>
    public sealed class BoolToVisibility : IValueConverter {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
            bool b = value is bool v && v;
            if (parameter as string == "invert") b = !b;
            return b ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    /// <summary>null or empty → Collapsed.</summary>
    public sealed class NullToVisibility : IValueConverter {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
            bool empty = value == null || (value is string s && s.Length == 0);
            if (parameter as string == "invert") empty = !empty;
            return empty ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    /// <summary>"#RRGGBB" → brush, for the color swatches.</summary>
    public sealed class HexToBrush : IValueConverter {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Colors2.Brush(value as string);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    /// <summary>Compares an enum value with the parameter, for radio-button style mode switches.</summary>
    public sealed class EnumEquals : IValueConverter {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value != null && parameter != null && value.ToString() == parameter.ToString();

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) {
            if (value is bool b && b && parameter != null) return Enum.Parse(targetType, parameter.ToString()!);
            return Binding.DoNothing;
        }
    }
}
