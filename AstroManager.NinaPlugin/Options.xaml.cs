using System.ComponentModel.Composition;
using System.Windows;

namespace AstroManager.NinaPlugin
{
    /// <summary>
    /// Code-behind for Options.xaml - Required for NINA to discover the ResourceDictionary
    /// </summary>
    [Export(typeof(ResourceDictionary))]
    public partial class Options : ResourceDictionary
    {
        public Options()
        {
            InitializeComponent();
        }
    }
}
