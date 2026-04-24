using System;
using System.Collections.Generic;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace KesFile.Pages
{
    public sealed partial class HomePage : Page
    {
        public HomePage()
        {
            InitializeComponent();
        }

        private void CreateCard_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(CreatePage));
        }

        private void OpenCard_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(OpenPage));
        }

        private void DropZone_DragOver(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
                e.DragUIOverride.Caption = "Create archive";
                e.DragUIOverride.IsCaptionVisible = true;
                e.DragUIOverride.IsGlyphVisible = true;
                DropZoneBorder.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 80, 100, 212));
            }
        }

        private async void DropZone_Drop(object sender, DragEventArgs e)
        {
            DropZoneBorder.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 199, 210, 254));

            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

            IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
            if (items.Count == 0) return;

            // Navigate to Create page with the dropped files
            Frame.Navigate(typeof(CreatePage), items);
        }
    }
}
