using System.ComponentModel.Composition;
using NINA.Core.Utility.WindowService;
using NINA.Sequencer.Container;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;

namespace AstroManager.NinaPlugin
{
    [Export(typeof(ISequenceItem))]
    [Export(typeof(ISequenceContainer))]
    [ExportMetadata("Name", "AstroManager Scheduler Container")]
    [ExportMetadata("Description", "Recommended AstroManager scheduler container with Deep Sky Object-compatible target context and event containers.")]
    [ExportMetadata("Icon", "SpaceShuttle")]
    [ExportMetadata("Category", "AstroManager")]
    public class AstroManagerSchedulerContainer : AstroManagerTargetScheduler
    {
        [ImportingConstructor]
        public AstroManagerSchedulerContainer(
            AstroManagerApiClient apiClient,
            HeartbeatService heartbeatService,
            IProfileService profileService,
            ITelescopeMediator telescopeMediator,
            IGuiderMediator guiderMediator,
            IDomeMediator domeMediator,
            IDomeFollower domeFollower,
            ISafetyMonitorMediator safetyMonitorMediator,
            IWeatherDataMediator weatherDataMediator,
            IFilterWheelMediator filterWheelMediator,
            ICameraMediator cameraMediator,
            IFocuserMediator focuserMediator,
            IRotatorMediator rotatorMediator,
            IImagingMediator imagingMediator,
            IImageSaveMediator imageSaveMediator,
            IImageHistoryVM imageHistoryVM,
            ISequenceMediator sequenceMediator,
            IPlateSolverFactory plateSolverFactory,
            IWindowServiceFactory windowServiceFactory,
            IAutoFocusVMFactory autoFocusVMFactory,
            ScheduledTargetStore targetStore)
            : base(
                apiClient,
                heartbeatService,
                profileService,
                telescopeMediator,
                guiderMediator,
                domeMediator,
                domeFollower,
                safetyMonitorMediator,
                weatherDataMediator,
                filterWheelMediator,
                cameraMediator,
                focuserMediator,
                rotatorMediator,
                imagingMediator,
                imageSaveMediator,
                imageHistoryVM,
                sequenceMediator,
                plateSolverFactory,
                windowServiceFactory,
                autoFocusVMFactory,
                targetStore)
        {
        }

        private AstroManagerSchedulerContainer(AstroManagerTargetScheduler source)
            : base(source)
        {
        }

        protected override AstroManagerTargetScheduler CreateCloneInstance()
        {
            return new AstroManagerSchedulerContainer(this);
        }
    }
}
