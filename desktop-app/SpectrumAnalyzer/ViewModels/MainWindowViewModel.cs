using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SpectrumAnalyzer.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private ViewModelBase _currentPage;

    [ObservableProperty]
    private bool _isMenuOpen;

    public HomeViewModel HomePage { get; }
    public AnalysisViewModel AnalysisPage { get; }

    public MainWindowViewModel()
    {
        HomePage = new HomeViewModel(this);
        AnalysisPage = new AnalysisViewModel();

        AnalysisPage.RequestNavigateToHome += () => GoToHome();

        CurrentPage = HomePage;
    }

    [RelayCommand]
    public void GoToHome()
    {
        HomePage.Reset();

        CurrentPage = HomePage;
        IsMenuOpen = false; 
    }

    [RelayCommand]
    public void GoToAnalysis()
    {
        CurrentPage = AnalysisPage;
        IsMenuOpen = false;
    }

    [RelayCommand]
    private void ToggleMenu() => IsMenuOpen = !IsMenuOpen;

    public void GoToAnalysis(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return;
        }

        AnalysisPage.StartAnalysis(filePath);

        CurrentPage = AnalysisPage;
        IsMenuOpen = false;
    }

    public void GoToLiveAnalysis()
    {
        AnalysisPage.StartLiveAnalysis();

        CurrentPage = AnalysisPage;
        IsMenuOpen = false;
    }
}

