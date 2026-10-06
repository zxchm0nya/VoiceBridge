using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace VoiceBridge.Views;

public partial class HelpView : UserControl
{
    public HelpView()
    {
        InitializeComponent();
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            /* ignore */
        }
        e.Handled = true;
    }
}
