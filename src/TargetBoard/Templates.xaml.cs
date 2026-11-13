using System.ComponentModel.Composition;
using System.Windows;

namespace TargetBoard {

    /// <summary>Exported so NINA merges these templates into its application resources.</summary>
    [Export(typeof(ResourceDictionary))]
    public partial class Templates : ResourceDictionary {
        public Templates() {
            InitializeComponent();
        }
    }
}
