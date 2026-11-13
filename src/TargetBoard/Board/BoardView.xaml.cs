using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TargetBoard.Board {

    /// <summary>
    /// The board panel. How the mouse and keyboard work, in one place:
    ///
    /// * The target list is a plain list of rows (no ListBox), so nothing in it selects, scrolls or holds the mouse on its
    ///   own. The selection lives in the view model; the rows only draw it.
    /// * Every press on a row goes through one small state machine below: press → (release = click) | (move = drag) |
    ///   (second press = double-click). A right-click selects the row and opens its menu.
    /// * Nothing here takes hold of the mouse. Only a drag does, and the system's drag and drop lets go when it ends.
    /// * The mouse wheel goes to whatever is under the mouse: the list, the details or the calendar, each on its own.
    /// * The details panel and the calendar have their own handlers; none of the list's handlers reach them.
    /// </summary>
    public partial class BoardView : UserControl {

        public BoardView() {
            InitializeComponent();
            deselectTimer = new DispatcherTimer { Interval = DoubleClickTime };
            deselectTimer.Tick += (_, _) => Run("clearing the selection", FinishDeselect);
            DataContextChanged += (_, _) => Watch(DataContext as BoardViewModel);
            PreviewKeyDown += (_, e) => Run("a key", () => OnKey(e));
        }

        private BoardViewModel? watched;
        private BoardViewModel? Vm => watched;

        private static void Run(string what, Action action) => Util.Safe.Run(what, action);

        // ================================================================== the view model

        private void Watch(BoardViewModel? vm) {
            if (watched != null) watched.PropertyChanged -= OnViewModelChanged;
            watched = vm;
            if (vm != null) vm.PropertyChanged += OnViewModelChanged;
            RestoreLayout();
        }

        private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e) {
            switch (e.PropertyName) {
                case nameof(BoardViewModel.DetailsOpen):
                    Run("showing the details", LayoutDetails);
                    break;
                case nameof(BoardViewModel.ListPanelWidth):
                    Run("restoring the panel sizes", RestoreLayout);
                    break;
                case nameof(BoardViewModel.SelectedListItem):
                    // A target chosen on the calendar scrolls into view in the list.
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => Run("showing the selected row", BringSelectionIntoView)));
                    break;
            }
        }

        private void BringSelectionIntoView() {
            var item = Vm?.SelectedListItem;
            if (item == null) return;
            if (TargetRows.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement row) row.BringIntoView();
        }

        // ================================================================== details under the list

        /// <summary>The list/details split the user last chose, restored each time the details open, and saved with the board.</summary>
        private double listShare = 1, detailsShare = 1;

        /// <summary>Puts back the list width and the list/details split saved with the board.</summary>
        private void RestoreLayout() {
            var vm = Vm;
            if (vm != null) {
                (listShare, detailsShare) = vm.DetailsSplit;
                double width = vm.ListPanelWidth;
                // Leave the calendar its minimum width if the window is narrower than when the width was saved.
                if (LeftPanel.Parent is Grid outer && outer.ActualWidth > 0) width = Math.Max(ListColumn.MinWidth, Math.Min(width, outer.ActualWidth - 306));
                ListColumn.Width = new GridLength(width);
            }
            LayoutDetails();
        }

        private void ListSplitter_DragCompleted(object sender, DragCompletedEventArgs e) => Run("saving the list width", () => {
            if (ListColumn.ActualWidth > 0) Vm?.SaveLayout(ListColumn.ActualWidth, null, null);
        });

        private void DetailsSplitter_DragCompleted(object sender, DragCompletedEventArgs e) => Run("saving the details size", () => {
            if (ListRow.ActualHeight <= 1 || DetailsRow.ActualHeight <= 1) return;
            listShare = ListRow.ActualHeight;
            detailsShare = DetailsRow.ActualHeight;
            Vm?.SaveLayout(null, listShare, detailsShare);
        });

        /// <summary>Open: the list and details share the left side (half each at first). Closed: the list takes it all.</summary>
        private void LayoutDetails() {
            if (Vm?.DetailsOpen == true) {
                ListRow.Height = new GridLength(listShare, GridUnitType.Star);
                SplitRow.Height = new GridLength(6);
                DetailsRow.Height = new GridLength(detailsShare, GridUnitType.Star);
                DetailsRow.MinHeight = 120;
            } else {
                if (DetailsRow.ActualHeight > 1 && ListRow.ActualHeight > 1) {
                    listShare = ListRow.ActualHeight;
                    detailsShare = DetailsRow.ActualHeight;
                }
                DetailsRow.MinHeight = 0;
                ListRow.Height = new GridLength(1, GridUnitType.Star);
                SplitRow.Height = new GridLength(0);
                DetailsRow.Height = new GridLength(0);
            }
        }

        // ================================================================== keys

        private void OnKey(KeyEventArgs e) {
            var vm = Vm;
            if (vm == null) return;
            var focus = Keyboard.FocusedElement as DependencyObject;
            // The calendar handles its own keys (Esc cancels a drag or closes the filter view, Ctrl+Z undoes...).
            if (focus != null && IsInside<CalendarCanvas>(focus)) return;
            if (e.Key == Key.Escape && vm.DetailsOpen) {
                vm.DetailsOpen = false;
                e.Handled = true;
            } else if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && !IsInside<TextBoxBase>(focus)) {
                // Ctrl+Z undoes the last change to the plan from anywhere on the board, except while typing.
                vm.Undo();
                e.Handled = true;
            }
        }

        // ================================================================== the target list

        /// <summary>A press on a row that has not yet become a click, a double-click or a drag.</summary>
        private sealed record Press(object Row, Point At, bool WasSelected);

        private Press? press;
        private readonly DispatcherTimer deselectTimer;
        private object? pendingDeselect;

        [DllImport("user32.dll")]
        private static extern uint GetDoubleClickTime();

        private static TimeSpan DoubleClickTime {
            get {
                try { return TimeSpan.FromMilliseconds(Math.Clamp(GetDoubleClickTime(), 200u, 900u)); } catch (Exception) { return TimeSpan.FromMilliseconds(500); }
            }
        }

        /// <summary>
        /// The row under a press: the item of the list's row container. Null outside a row, and null when the press is
        /// on a button inside the row (a target set's arrow), which handles its own click.
        /// </summary>
        private object? RowAt(object? source) {
            for (var d = source as DependencyObject; d != null && !ReferenceEquals(d, TargetRows); d = StepUp(d)) {
                if (d is ButtonBase) return null;
                if (d is ContentPresenter cp && cp.Content is TargetItemVM or GroupItemVM
                    && ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(cp), TargetRows)) {
                    return cp.Content;
                }
            }
            return null;
        }

        private void Rows_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Run("a click in the list", () => {
            press = null;
            var row = RowAt(e.OriginalSource);
            var vm = Vm;
            if (row == null || vm == null) return;
            LeftPanel.Focus();   // so Esc reaches the board; rows themselves never take the keyboard
            e.Handled = true;

            if (e.ClickCount >= 2) {
                // Double-click: open the details. A deselect waiting from the first click is called off.
                CancelDeselect();
                vm.OpenDetails(row);
                return;
            }
            bool wasSelected = ReferenceEquals(vm.SelectedListItem, row);
            if (!wasSelected) vm.SelectedListItem = row;   // select on press, so a drag shows the target's times at once
            press = new Press(row, e.GetPosition(this), wasSelected);
        });

        private void Rows_PreviewMouseMove(object sender, MouseEventArgs e) => Run("dragging a target", () => {
            var p = press;
            if (p == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) { press = null; return; }
            var delta = e.GetPosition(this) - p.At;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            press = null;
            CancelDeselect();
            StartDrag(p.Row);
        });

        private void Rows_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => Run("a click in the list", () => {
            var p = press;
            press = null;
            if (p == null) return;
            e.Handled = true;
            // A click on the row that was already selected deselects it, once the double-click time has passed
            // without a second click (a double-click opens the details instead).
            if (p.WasSelected) {
                pendingDeselect = p.Row;
                deselectTimer.Stop();
                deselectTimer.Start();
            }
        });

        private void FinishDeselect() {
            deselectTimer.Stop();
            var row = pendingDeselect;
            pendingDeselect = null;
            var vm = Vm;
            if (row != null && vm != null && ReferenceEquals(vm.SelectedListItem, row)) vm.SelectedListItem = null;
        }

        private void CancelDeselect() {
            deselectTimer.Stop();
            pendingDeselect = null;
        }

        private void StartDrag(object row) {
            DataObject? data = row switch {
                TargetItemVM t => new DataObject(CalendarCanvas.DragFormat, t.Id),
                GroupItemVM g => new DataObject(CalendarCanvas.GroupDragFormat, g.GroupId),
                _ => null
            };
            if (data == null) return;
            DragDrop.DoDragDrop(TargetRows, data, DragDropEffects.Copy);
        }

        private void Rows_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e) => Run("opening the target menu", () => {
            var row = RowAt(e.OriginalSource);
            var vm = Vm;
            if (row == null || vm == null) return;
            CancelDeselect();
            vm.SelectedListItem = row;
            var menu = row switch {
                TargetItemVM t => TargetMenu(vm, t),
                GroupItemVM g => GroupMenu(vm, g),
                _ => null
            };
            if (menu == null) return;
            menu.PlacementTarget = TargetRows;
            menu.Placement = PlacementMode.MousePoint;
            menu.IsOpen = true;
            e.Handled = true;
        });

        private static ContextMenu TargetMenu(BoardViewModel vm, TargetItemVM t) {
            var ids = new[] { t.Id };
            var menu = new ContextMenu();
            menu.Items.Add(Item("View details", () => vm.OpenDetails(t)));
            menu.Items.Add(new Separator());
            if (t.IsMarkedDone) menu.Items.Add(Item("Mark as not done", () => vm.MarkDone(ids, false, t.Name)));
            else if (t.IsComplete) menu.Items.Add(new MenuItem { Header = "Done: every frame is taken", IsEnabled = false });
            else menu.Items.Add(Item("Mark as done", () => vm.MarkDone(ids, true, t.Name)));
            menu.Items.Add(t.IsHidden
                ? Item("Show in the list", () => vm.SetHidden(ids, false, t.Name))
                : Item("Hide", () => vm.SetHidden(ids, true, t.Name)));
            return menu;
        }

        private static ContextMenu? GroupMenu(BoardViewModel vm, GroupItemVM g) {
            List<string> ids = vm.GroupMemberIds(g.GroupId);
            if (ids.Count == 0) return null;
            var menu = new ContextMenu();
            menu.Items.Add(Item("View details", () => vm.OpenDetails(g)));
            menu.Items.Add(new Separator());
            menu.Items.Add(vm.AllMarkedDone(ids)
                ? Item("Mark set as not done", () => vm.MarkDone(ids, false, g.Name))
                : Item("Mark set as done", () => vm.MarkDone(ids, true, g.Name)));
            menu.Items.Add(vm.AllHidden(ids)
                ? Item("Show set in the list", () => vm.SetHidden(ids, false, g.Name))
                : Item("Hide set", () => vm.SetHidden(ids, true, g.Name)));
            return menu;
        }

        private static MenuItem Item(string header, Action action) {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => Run(header.ToLowerInvariant(), action);
            return item;
        }

        // ================================================================== switches

        /// <summary>Clicking a switch's label toggles the switch, like clicking the switch itself.</summary>
        private void SwitchRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => Run("a switch", () => {
            if (sender is not Panel row || IsInside<CheckBox>(e.OriginalSource as DependencyObject, row)) return;
            var box = row.Children.OfType<CheckBox>().FirstOrDefault();
            if (box == null || !box.IsEnabled) return;
            box.IsChecked = !(box.IsChecked ?? false);
            e.Handled = true;
        });

        // ================================================================== tree helpers

        private static bool IsInside<T>(DependencyObject? d, DependencyObject? stopAt = null) where T : DependencyObject {
            for (; d != null && !ReferenceEquals(d, stopAt); d = StepUp(d)) {
                if (d is T) return true;
            }
            return false;
        }

        // Text runs are not visuals; step out through the logical tree.
        private static DependencyObject? StepUp(DependencyObject d)
            => d is Visual || d is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
    }
}
