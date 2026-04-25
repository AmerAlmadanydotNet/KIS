using System;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using KesFile.Pages;
using KesFile.ViewModels;

namespace KesFile
{
    public sealed partial class MainPage : Page
    {
        private readonly MainViewModel _vm = new();

        public MainPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // If launched via file association, open the archive
            if (e.Parameter is StorageFile file)
                OpenArchiveFile(file);
        }

        public void OpenArchiveFile(StorageFile? file)
        {
            if (file == null) return;
            SelectNavItem("open");
            ContentFrame.Navigate(typeof(OpenPage), file);
        }

        private void NavView_Loaded(object sender, RoutedEventArgs e)
        {
            // Select Home by default
            NavView.SelectedItem = NavView.MenuItems[0];
            ContentFrame.Navigate(typeof(HomePage));
        }

        private void NavView_SelectionChanged(NavigationView sender,
            NavigationViewSelectionChangedEventArgs args)
        {
            if (args.IsSettingsSelected)
            {
                ContentFrame.Navigate(typeof(SettingsPage));
                return;
            }

            if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
            {
                _vm.NavigateTo(tag);
                Type? pageType = tag switch
                {
                    "home"   => typeof(HomePage),
                    "create" => typeof(CreatePage),
                    "open"   => typeof(OpenPage),
                    "about"  => typeof(AboutPage),
                    _        => null
                };

                if (pageType != null && ContentFrame.CurrentSourcePageType != pageType)
                    ContentFrame.Navigate(pageType);
            }
        }

        private void SelectNavItem(string tag)
        {
            foreach (var item in NavView.MenuItems)
                if (item is NavigationViewItem ni && ni.Tag as string == tag)
                {
                    NavView.SelectedItem = ni;
                    break;
                }
        }
    }
}
