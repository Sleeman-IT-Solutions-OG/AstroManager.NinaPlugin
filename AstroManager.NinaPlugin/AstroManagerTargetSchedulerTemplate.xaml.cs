using System.ComponentModel.Composition;
using System.Windows;

namespace AstroManager.NinaPlugin
{
    [Export(typeof(ResourceDictionary))]
    public partial class AstroManagerTargetSchedulerTemplate : ResourceDictionary
    {
        public AstroManagerTargetSchedulerTemplate()
        {
            InitializeComponent();
        }
    }
}
