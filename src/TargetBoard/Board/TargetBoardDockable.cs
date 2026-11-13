using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.ViewModel;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Media;

namespace TargetBoard.Board {

    /// <summary>
    /// The "Target Board" panel in NINA's Imaging tab. Float it or dock it wide; the gear in its header opens the settings.
    /// Its look is the DataTemplate keyed "TargetBoard.Board.TargetBoardDockable_Dockable" in Templates.xaml.
    /// </summary>
    [Export(typeof(IDockableVM))]
    public sealed class TargetBoardDockable : DockableVM {

        [ImportingConstructor]
        public TargetBoardDockable(IProfileService profileService, IImageSaveMediator imageSaveMediator, ISequenceMediator sequenceMediator) : base(profileService) {
            Title = "Target Board";
            HasSettings = true;
            ImageGeometry = CalendarIcon();
            Board = new BoardViewModel(BoardContext.Get(profileService, imageSaveMediator, sequenceMediator));
        }

        public BoardViewModel Board { get; }

        /// <summary>A small calendar drawn on a 24×24 grid; even-odd fill turns the inner rectangles into holes.</summary>
        private static GeometryGroup CalendarIcon() {
            var g = new GeometryGroup { FillRule = FillRule.EvenOdd };
            g.Children.Add(new RectangleGeometry(new Rect(2, 4, 20, 18), 2, 2));
            g.Children.Add(new RectangleGeometry(new Rect(4, 9, 16, 11)));
            g.Children.Add(new RectangleGeometry(new Rect(6, 1, 2, 3)));
            g.Children.Add(new RectangleGeometry(new Rect(16, 1, 2, 3)));
            foreach (var (x, y) in new[] { (6.0, 11.0), (10.5, 11.0), (15.0, 11.0), (6.0, 15.5), (10.5, 15.5) }) {
                g.Children.Add(new RectangleGeometry(new Rect(x, y, 3, 3)));
            }
            g.Freeze();
            return g;
        }
    }
}
