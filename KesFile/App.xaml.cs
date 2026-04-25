using System;
using System.IO;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace KesFile
{
    sealed partial class App : Application
    {
        private static readonly string ErrorLogPath =
            Path.Combine(Path.GetTempPath(), "KesFileError.txt");

        public App()
        {
            this.UnhandledException += App_UnhandledException;
            try
            {
                InitializeComponent();
            }
            catch (Exception ex)
            {
                WriteError("App() InitializeComponent: " + ex.ToString());
                throw;
            }
            Suspending += OnSuspending;
        }

        private static void App_UnhandledException(object sender, Windows.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            WriteError("UnhandledException: " + e.Message + "\n" + e.Exception?.ToString());
            e.Handled = true;
        }

        private static void WriteError(string msg)
        {
            try { File.WriteAllText(ErrorLogPath, msg); } catch { }
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            try
            {
                Frame rootFrame = Window.Current.Content as Frame ?? CreateRootFrame();
                if (e.PrelaunchActivated) return;
                if (rootFrame.Content == null)
                    rootFrame.Navigate(typeof(MainPage), e.Arguments);
                Services.ThemeService.ApplyCurrent();
                Window.Current.Activate();
            }
            catch (Exception ex)
            {
                WriteError("OnLaunched: " + ex.ToString());
                throw;
            }
        }

        // Handle activation via .kes file association
        protected override void OnFileActivated(FileActivatedEventArgs args)
        {
            Frame rootFrame = Window.Current.Content as Frame ?? CreateRootFrame();

            if (rootFrame.Content == null)
                rootFrame.Navigate(typeof(MainPage), args.Files[0]);
            else if (rootFrame.Content is MainPage mainPage)
                mainPage.OpenArchiveFile(args.Files[0] as Windows.Storage.StorageFile);

            Services.ThemeService.ApplyCurrent();
            Window.Current.Activate();
        }

        private static Frame CreateRootFrame()
        {
            var rootFrame = new Frame();
            rootFrame.NavigationFailed += OnNavigationFailed;
            Window.Current.Content = rootFrame;
            return rootFrame;
        }

        private static void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
            => throw new Exception($"Failed to load page '{e.SourcePageType.FullName}'");

        private static void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            deferral.Complete();
        }
    }
}
