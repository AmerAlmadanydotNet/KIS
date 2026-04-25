using System;
using Windows.Storage;
using Windows.UI.Xaml;

namespace KesFile.Services
{
    /// <summary>
    /// Persists the user's theme choice and applies it to the app window.
    /// </summary>
    public static class ThemeService
    {
        public const string SettingKey = "AppTheme";

        public enum AppTheme
        {
            System = 0,
            Light  = 1,
            Dark   = 2
        }

        public static AppTheme Current
        {
            get
            {
                try
                {
                    var v = ApplicationData.Current.LocalSettings.Values[SettingKey] as string;
                    if (Enum.TryParse<AppTheme>(v, out var t)) return t;
                }
                catch { }
                return AppTheme.System;
            }
            set
            {
                try
                {
                    ApplicationData.Current.LocalSettings.Values[SettingKey] = value.ToString();
                }
                catch { }
                Apply(value);
            }
        }

        /// <summary>Apply the saved theme to the current window. Safe to call on startup.</summary>
        public static void ApplyCurrent() => Apply(Current);

        public static void Apply(AppTheme theme)
        {
            try
            {
                if (Window.Current?.Content is FrameworkElement root)
                {
                    root.RequestedTheme = theme switch
                    {
                        AppTheme.Light => ElementTheme.Light,
                        AppTheme.Dark  => ElementTheme.Dark,
                        _              => ElementTheme.Default
                    };
                }
            }
            catch { }
        }
    }
}
