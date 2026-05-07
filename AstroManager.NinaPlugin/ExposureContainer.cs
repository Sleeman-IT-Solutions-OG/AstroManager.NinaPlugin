using NINA.Astrometry;
using NINA.Astrometry.Interfaces;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.Container.ExecutionStrategy;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Imaging;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using Shared.Model.DTO.Client;
using System.Threading;
using System.Threading.Tasks;

namespace AstroManager.NinaPlugin
{
    /// <summary>
    /// Container for a single exposure that properly integrates with NINA's trigger system.
    /// Similar to TargetScheduler's PlanContainer pattern.
    /// By calling base.Execute(), NINA handles trigger evaluation and counter increments.
    /// </summary>
    public class ExposureContainer : SequentialContainer, IDeepSkyObjectContainer
    {
        private readonly AstroManagerTargetScheduler _parentContainer;
        private readonly IProfileService _profileService;
        private readonly ICameraMediator _cameraMediator;
        private readonly IImagingMediator _imagingMediator;
        private readonly IImageSaveMediator _imageSaveMediator;
        private readonly IImageHistoryVM _imageHistoryVM;
        private readonly TakeExposure _exposureItem;
        
        // IDeepSkyObjectContainer implementation - delegate to parent
        public InputTarget Target 
        { 
            get => _parentContainer.Target;
            set { } // Setter required by interface but we don't use it - parent owns the target
        }
        public NighttimeData NighttimeData => _parentContainer.NighttimeData;
        
        public ExposureContainer(
            AstroManagerTargetScheduler parentContainer,
            IProfileService profileService,
            ICameraMediator cameraMediator,
            IImagingMediator imagingMediator,
            IImageSaveMediator imageSaveMediator,
            IImageHistoryVM imageHistoryVM)
            : base()
        {
            _parentContainer = parentContainer;
            _profileService = profileService;
            _cameraMediator = cameraMediator;
            _imagingMediator = imagingMediator;
            _imageSaveMediator = imageSaveMediator;
            _imageHistoryVM = imageHistoryVM;
            
            Name = nameof(ExposureContainer);
            Category = "AstroManager";
            
            // Attach to parent container for proper hierarchy (like TargetScheduler's PlanContainer)
            AttachNewParent(parentContainer);
            
            // Copy triggers from parent to this container so they can fire
            foreach (var trigger in parentContainer.GetTriggersSnapshot())
            {
                trigger.AttachNewParent(this);
            }

            _exposureItem = new TakeExposure(
                _profileService,
                _cameraMediator,
                _imagingMediator,
                _imageSaveMediator,
                _imageHistoryVM);
            _exposureItem.ExposureCount = 1;

            Add(_exposureItem);
        }
        
        /// <summary>
        /// Configure the persistent exposure item for the next slot.
        /// NINA keeps the filename exposure counter on the TakeExposure instance,
        /// so recreating that item every shot resets $$EXPOSURENUMBER$$ back to 0001.
        /// We intentionally keep one TakeExposure item alive for the full scheduler run,
        /// which matches Target Scheduler's session-wide frame numbering behavior.
        /// </summary>
        public void ConfigureExposure(NextSlotDto slot)
        {
            if (_exposureItem.ExposureCount < 1)
            {
                _exposureItem.ExposureCount = 1;
            }

            _exposureItem.ExposureTime = slot.ExposureTimeSeconds;
            _exposureItem.ImageType = "LIGHT";
            _exposureItem.Gain = slot.Gain >= 0 ? slot.Gain : -1;
            _exposureItem.Offset = slot.Offset >= 0 ? slot.Offset : -1;

            Logger.Info($"ExposureContainer: Configured TakeExposure - {slot.Filter} {slot.ExposureTimeSeconds}s (next #{_exposureItem.ExposureCount})");
        }
        
        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
        {
            Logger.Info("ExposureContainer: Starting execution via base.Execute()");
            
            try
            {
                // Call base.Execute() to let NINA handle the execution flow
                // This properly evaluates triggers and increments counters
                await base.Execute(progress, token);
                
                Logger.Info("ExposureContainer: Execution completed");
            }
            finally { }
        }
        
        public override object Clone()
        {
            return new ExposureContainer(
                _parentContainer,
                _profileService,
                _cameraMediator,
                _imagingMediator,
                _imageSaveMediator,
                _imageHistoryVM);
        }

        public void ResetExposureCounter()
        {
            _exposureItem.ExposureCount = 1;
            Logger.Info("ExposureContainer: Reset exposure counter for new scheduler session");
        }
    }
}
