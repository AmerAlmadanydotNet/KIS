using System;
using KesFile.Services;
using Windows.UI.Xaml.Controls;

namespace KesFile.Pages
{
    public sealed partial class SettingsPage : Page
    {
        private bool _initializing = true;

        public SettingsPage()
        {
            InitializeComponent();
            int idx = ThemeService.Current switch
            {
                ThemeService.AppTheme.Light => 1,
                ThemeService.AppTheme.Dark  => 2,
                _                           => 0
            };
            ThemeComboBox.SelectedIndex = idx;
            _initializing = false;
        }

        private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initializing) return;
            if (ThemeComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag &&
                Enum.TryParse<ThemeService.AppTheme>(tag, out var theme))
            {
                ThemeService.Current = theme;
            }
        }
    }
}
