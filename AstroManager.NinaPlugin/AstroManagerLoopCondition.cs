using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;

namespace AstroManager.NinaPlugin
{
    /// <summary>
    /// Condition types for the AstroManager Scheduler
    /// </summary>
    public enum AstroManagerConditionType
    {
        WhileActive
    }

    /// <summary>
    /// Loop condition for the AstroManager Scheduler.
    /// Add this condition to the AstroManager Scheduler container to enable looping.
    /// </summary>
    [Export(typeof(ISequenceCondition))]
    [ExportMetadata("Name", "AstroManager: Until no more targets available")]
    [ExportMetadata("Description", "Loops until AstroManager has no more targets to image.")]
    [ExportMetadata("Icon", "SpaceShuttle")]
    [ExportMetadata("Category", "AstroManager")]
    public class AstroManagerLoopCondition : SequenceCondition
    {
        private AstroManagerConditionType _selectedCondition = AstroManagerConditionType.WhileActive;
        
        [ImportingConstructor]
        public AstroManagerLoopCondition()
        {
        }

        /// <summary>
        /// Available condition types for the dropdown
        /// </summary>
        public IList<AstroManagerConditionType> ConditionTypes => new List<AstroManagerConditionType>
        {
            AstroManagerConditionType.WhileActive
        };
        
        /// <summary>
        /// Selected condition type
        /// </summary>
        [JsonProperty]
        public AstroManagerConditionType SelectedCondition
        {
            get => _selectedCondition;
            set
            {
                _selectedCondition = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(ConditionDescription));
            }
        }
        
        /// <summary>
        /// Human-readable description of the selected condition
        /// </summary>
        public string ConditionDescription
        {
            get
            {
                return _selectedCondition switch
                {
                    AstroManagerConditionType.WhileActive => "AstroManager: Until no more targets available",
                    _ => "Unknown"
                };
            }
        }

        /// <summary>
        /// Check if the loop should continue.
        /// Returns true to continue looping, false to stop.
        /// IMPORTANT: Returns true if scheduler hasn't started yet (to allow first run)
        /// or if the scheduler was cancelled but sequence is restarting.
        /// </summary>
        public override bool Check(ISequenceItem previousItem, ISequenceItem nextItem)
        {
            // Find the AstroManagerTargetScheduler (search parents, siblings, and children)
            var scheduler = FindScheduler();
            if (scheduler == null)
            {
                // No scheduler found - return true to allow the container to run
                // (user may have condition on wrong container)
                Logger.Warning("AstroManager Condition: No scheduler found in hierarchy - allowing execution");
                return true;
            }

            // If ShouldContinue is false, stop the loop.
            // The scheduler sets this to false when:
            // - API returns Stop (no more targets, night over, all complete)
            // - API returns Park
            // - An error occurs
            // The scheduler will reset _shouldContinue=true in SequenceBlockInitialize() when 
            // the user manually restarts the sequence, so we don't need special handling here.
            if (!scheduler.ShouldContinue)
            {
                Logger.Debug("AstroManager Condition: ShouldContinue is false - stopping loop");
                return false;
            }

            return _selectedCondition switch
            {
                // ShouldContinue is true by default, becomes false when API signals stop
                AstroManagerConditionType.WhileActive => scheduler.ShouldContinue,
                _ => true
            };
        }

        /// <summary>
        /// Find the AstroManagerTargetScheduler in the hierarchy
        /// Searches: parent hierarchy, sibling items, and child items
        /// </summary>
        private AstroManagerTargetScheduler? FindScheduler()
        {
            // First check if we ARE the scheduler (condition directly on scheduler)
            if (Parent is AstroManagerTargetScheduler directParent)
            {
                return directParent;
            }
            
            // Search parent hierarchy
            ISequenceContainer? current = Parent;
            while (current != null)
            {
                if (current is AstroManagerTargetScheduler scheduler)
                {
                    return scheduler;
                }
                
                // Also search siblings (items in the same container)
                if (current is ISequenceContainer container)
                {
                    foreach (var item in container.Items)
                    {
                        if (item is AstroManagerTargetScheduler siblingScheduler)
                        {
                            return siblingScheduler;
                        }
                        // Search children of siblings
                        if (item is ISequenceContainer childContainer)
                        {
                            var found = SearchChildren(childContainer);
                            if (found != null) return found;
                        }
                    }
                }
                
                current = (current as ISequenceItem)?.Parent;
            }
            return null;
        }
        
        /// <summary>
        /// Recursively search children for the scheduler
        /// </summary>
        private AstroManagerTargetScheduler? SearchChildren(ISequenceContainer container)
        {
            foreach (var item in container.Items)
            {
                if (item is AstroManagerTargetScheduler scheduler)
                {
                    return scheduler;
                }
                if (item is ISequenceContainer childContainer)
                {
                    var found = SearchChildren(childContainer);
                    if (found != null) return found;
                }
            }
            return null;
        }

        public override object Clone()
        {
            return new AstroManagerLoopCondition
            {
                SelectedCondition = SelectedCondition
            };
        }

        public override string ToString()
        {
            return $"AstroManager: {ConditionDescription}";
        }
    }
}
