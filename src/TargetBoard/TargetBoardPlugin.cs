using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.Mediator;
using System.ComponentModel.Composition;
using System.Threading.Tasks;
using TargetBoard.Settings;

namespace TargetBoard {

    /// <summary>
    /// Plugin entry point. NINA creates it at startup, which starts the shared board (file scanning and frame logging).
    /// It is also the data context of the plugin's page under Options › Plugins (template "Target Board_Options").
    /// </summary>
    [Export(typeof(IPluginManifest))]
    public sealed class TargetBoardPlugin : PluginBase {
        private readonly BoardContext context;

        [ImportingConstructor]
        public TargetBoardPlugin(IProfileService profileService, IImageSaveMediator imageSaveMediator, ISequenceMediator sequenceMediator) {
            context = BoardContext.Get(profileService, imageSaveMediator, sequenceMediator);
            Settings = new SettingsViewModel(context);
        }

        public SettingsViewModel Settings { get; }

        public override Task Teardown() {
            context.Dispose();
            return base.Teardown();
        }
    }
}
