namespace KesFile.ViewModels
{
    /// <summary>Shell-level ViewModel (navigation state).</summary>
    public class MainViewModel : ViewModelBase
    {
        private string _pageTitle = "Home";
        public string PageTitle
        {
            get => _pageTitle;
            set => SetProperty(ref _pageTitle, value);
        }

        public void NavigateTo(string tag)
        {
            PageTitle = tag switch
            {
                "home"    => "Home",
                "create"  => "Create Archive",
                "open"    => "Open Archive",
                "settings"=> "Settings",
                _         => tag
            };
        }
    }
}
