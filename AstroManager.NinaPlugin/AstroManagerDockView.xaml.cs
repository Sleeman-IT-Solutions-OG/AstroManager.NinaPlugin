using System.ComponentModel.Composition;
using System.Windows;

namespace AstroManager.NinaPlugin
{
    /// <summary>
    /// Code-behind for AstroManagerDockView ResourceDictionary
    /// Must export ResourceDictionary for NINA to discover the DataTemplate
    /// </summary>
    [Export(typeof(ResourceDictionary))]
    public partial class AstroManagerDockView : ResourceDictionary
    {
        public AstroManagerDockView()
        {
            InitializeComponent();
        }
    }
}
