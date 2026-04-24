using System.Collections.Generic;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using KesFile.ViewModels;

namespace KesFile.Pages
{
    public sealed partial class OpenPage : Page
    {
        public OpenArchiveViewModel ViewModel { get; } = new();

        public OpenPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // Support opening a .kes file passed via file association or drag-to-open
            if (e.Parameter is StorageFile file)
                await ViewModel.OpenArchiveAsync(file);
        }

        private async void Browse_Click(object sender, RoutedEventArgs e)
            => await ViewModel.BrowseAndOpenArchiveAsync();

        private async void Unlock_Click(object sender, RoutedEventArgs e)
            => await ViewModel.UnlockArchiveAsync();

        private async void ExtractAll_Click(object sender, RoutedEventArgs e)
            => await ViewModel.ExtractAllAsync();

        private async void ExtractSelected_Click(object sender, RoutedEventArgs e)
        {
            var selected = new List<ArchiveEntryViewModel>();
            foreach (var item in EntryListView.SelectedItems)
                if (item is ArchiveEntryViewModel vm)
                    selected.Add(vm);

            if (selected.Count == 0)
            {
                await ViewModel.ExtractAllAsync();
                return;
            }

            await ViewModel.ExtractSelectedAsync(selected);
        }
    }
}
