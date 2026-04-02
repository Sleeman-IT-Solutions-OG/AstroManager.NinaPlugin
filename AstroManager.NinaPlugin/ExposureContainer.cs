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
using System;
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
        }
        
        /// <summary>
        /// Add the exposure item to this container
        /// </summary>
        public void AddExposure(NextSlotDto slot)
        {
            var exposureItem = new TakeExposure(
                _profileService,
                _cameraMediator,
                _imagingMediator,
                _imageSaveMediator,
                _imageHistoryVM);
            
            exposureItem.ExposureTime = slot.ExposureTimeSeconds;
            exposureItem.ExposureCount = 1;
            exposureItem.ImageType = "LIGHT";
            exposureItem.Gain = slot.Gain >= 0 ? slot.Gain : -1;
            exposureItem.Offset = slot.Offset >= 0 ? slot.Offset : -1;
            
            // Add to Items collection - base.Execute() will execute it
            Add(exposureItem);
            
            Logger.Info($"ExposureContainer: Added TakeExposure - {slot.Filter} {slot.ExposureTimeSeconds}s");
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
            finally
            {
                // Clean up: detach items and clear
                foreach (var item in Items)
                {
                    item.AttachNewParent(null);
                }
                Items.Clear();
                
                // Don't clear triggers - they belong to parent
                foreach (var trigger in Triggers)
                {
                    trigger.AttachNewParent(_parentContainer);
                }
                Triggers.Clear();
            }
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
    }
}
