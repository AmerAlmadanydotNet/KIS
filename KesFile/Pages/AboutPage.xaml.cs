using System;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace KesFile.Pages
{
    public sealed partial class AboutPage : Page
    {
        public AboutPage()
        {
            InitializeComponent();
        }

        private async void Email_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await Launcher.LaunchUriAsync(new Uri(
                    "mailto:kisfile.feedback@protonmail.com?subject=KIS%20feedback"));
            }
            catch
            {
                // Best-effort; ignore if no mail handler is registered.
            }
        }
    }
}
