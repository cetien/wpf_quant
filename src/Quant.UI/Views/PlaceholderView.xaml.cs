using System.Windows.Controls;

namespace Quant.UI.Views;

public partial class PlaceholderView : UserControl
{
    public PlaceholderView(string icon, string label)
    {
        InitializeComponent();
        TxtIcon.Text = icon;
        TxtLabel.Text = label;
    }
}
