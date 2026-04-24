using System;
using System.Collections.Generic;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using KesFile.ViewModels;

namespace KesFile.Pages
{
    public sealed partial class CreatePage : Page
    {
        public CreateArchiveViewModel ViewModel { get; } = new();

        public CreatePage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // Handle files dropped on the Home page and forwarded here
            if (e.Parameter is IReadOnlyList<IStorageItem> items)
                ViewModel.AddDroppedFiles(items);
        }

        // ─── File list interactions ──────────────────────────────────────────────

        private async void AddFiles_Click(object sender, RoutedEventArgs e)
            => await ViewModel.AddFilesAsync();

        private async void AddFolder_Click(object sender, RoutedEventArgs e)
            => await ViewModel.AddFolderAsync();

        private void ClearFiles_Click(object sender, RoutedEventArgs e)
            => ViewModel.ClearFiles();

        private void RemoveItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is IStorageItem item)
                ViewModel.RemoveItem(item);
        }

        // ─── Drag & Drop ─────────────────────────────────────────────────────

        private void DropZone_DragOver(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
                e.DragUIOverride.Caption = "Add to archive";
                e.DragUIOverride.IsCaptionVisible = true;
            }
        }

        private async void DropZone_Drop(object sender, DragEventArgs e)
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
            ViewModel.AddDroppedFiles(items);
        }

        // ─── Create action ────────────────────────────────────────────────────

        private async void CreateArchive_Click(object sender, RoutedEventArgs e)
            => await ViewModel.CreateArchiveAsync();

        // ─── Helpers (x:Bind functions) ───────────────────────────────────────

        public string GetLevelText(int index) => index switch
        {
            0 => "Fastest",
            1 => "Fast",
            2 => "Normal",
            3 => "Maximum",
            _ => "Normal"
        };
    }
}
