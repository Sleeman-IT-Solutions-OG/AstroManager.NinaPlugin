using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Sequencer.Container;
using NINA.Sequencer.Container.ExecutionStrategy;
using NINA.Sequencer.SequenceItem;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AstroManager.NinaPlugin
{
    /// <summary>
    /// Type of event container for custom instructions
    /// </summary>
    public enum EventContainerType
    {
        BeforeNewTarget,
        AfterEachExposure,
        AfterTarget
    }

    /// <summary>
    /// Custom execution strategy for event instruction containers.
    /// Wraps SequentialStrategy to ensure proper identification of TS-originated calls.
    /// </summary>
    internal class EventContainerStrategy : IExecutionStrategy
    {
        private readonly SequentialStrategy _sequentialStrategy;

        public EventContainerStrategy()
        {
            _sequentialStrategy = new SequentialStrategy();
        }

        public object Clone()
        {
            return _sequentialStrategy.Clone();
        }

        public Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token)
        {
            return _sequentialStrategy.Execute(context, progress, token);
        }
    }

    /// <summary>
    /// Container for custom event instructions that users can drag/drop into.
    /// Similar to Target Scheduler's InstructionContainer pattern.
    /// </summary>
    [ExportMetadata("Name", "AstroManagerEventContainer")]
    [ExportMetadata("Description", "Container for custom event instructions")]
    [ExportMetadata("Icon", "Pen_NoFill_SVG")]
    [Export(typeof(ISequenceContainer))]
    [JsonObject(MemberSerialization.OptIn)]
    public class EventInstructionContainer : SequenceContainer, ISequenceContainer
    {
        private readonly object _lockObj = new object();

        [JsonProperty]
        public EventContainerType EventContainerType { get; set; }
        
        /// <summary>
        /// Override Items to ensure it's serialized (base class doesn't have [JsonProperty] when using OptIn)
        /// </summary>
        [JsonProperty]
        public new IList<ISequenceItem> Items
        {
            get => base.Items;
            set
            {
                base.Items.Clear();
                if (value != null)
                {
                    foreach (var item in value)
                    {
                        base.Items.Add(item);
                        item.AttachNewParent(this);
                    }
                }
            }
        }

        [ImportingConstructor]
        public EventInstructionContainer() : base(new EventContainerStrategy()) 
        {
            // Ensure Name is never null - required for NINA's DropIntoBehavior
            Name = nameof(EventInstructionContainer);
            Category = "AstroManager";
        }

        public EventInstructionContainer(EventContainerType containerType, ISequenceContainer parent) 
            : base(new EventContainerStrategy())
        {
            EventContainerType = containerType;
            Name = containerType.ToString();
            Category = "AstroManager";
            AttachNewParent(parent);
        }

        [OnDeserialized]
        public void OnDeserializedMethod(StreamingContext context)
        {
            // Restore EventContainerType from Name after deserialization
            if (Enum.TryParse<EventContainerType>(Name, out var containerType))
            {
                EventContainerType = containerType;
            }
            
            // Ensure Name is never null - required for NINA's DropIntoBehavior
            if (string.IsNullOrEmpty(Name))
            {
                Name = EventContainerType.ToString();
            }
            
            // Ensure Category is set
            if (string.IsNullOrEmpty(Category))
            {
                Category = "AstroManager";
            }
        }

        public override void Initialize()
        {
            foreach (ISequenceItem item in Items)
            {
                item.Initialize();
            }
            base.Initialize();
        }

        /// <summary>
        /// Reset the parent of the container. Needed when executing event containers
        /// to ensure items can find the DSO Target via the parent hierarchy.
        /// </summary>
        public void ResetParent(ISequenceContainer parent)
        {
            AttachNewParent(parent);
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
        {
            if (Items.Count > 0)
            {
                Logger.Info($"Event container '{Name}': starting execution with {Items.Count} items");
                await base.Execute(progress, token);
                Logger.Info($"Event container '{Name}': finished execution");
            }
        }

        /// <summary>
        /// Reset all items in the container for re-execution
        /// </summary>
        public void ResetAll()
        {
            foreach (var item in Items)
            {
                item.ResetProgress();
            }
            ResetProgress();
        }

        public override object Clone()
        {
            var clone = new EventInstructionContainer(EventContainerType, Parent);
            clone.Items = new ObservableCollection<ISequenceItem>(Items.Select(i => i.Clone() as ISequenceItem));
            foreach (var item in clone.Items)
            {
                item.AttachNewParent(clone);
            }
            return clone;
        }

        public new void MoveUp(ISequenceItem item)
        {
            lock (_lockObj)
            {
                var index = Items.IndexOf(item);
                if (index > 0)
                {
                    base.MoveUp(item);
                }
            }
        }
    }
}
