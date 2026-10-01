using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sox.App.Services;

namespace Sox.App.Settings;

public sealed partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();
    }

    private void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        Log.Info("Check update requested");
    }
}