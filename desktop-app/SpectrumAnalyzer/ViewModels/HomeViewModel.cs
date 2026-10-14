using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SpectrumAnalyzer.ViewModels;

public partial class HomeViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _mainViewModel;

    private string _selectedFilePath = string.Empty;

    [ObservableProperty]
    private string _fileName = "No file selected";

    [ObservableProperty]
    private string _selectButtonText = "Select File";

    [ObservableProperty]
    private bool _isFileSelected = false; 

    [ObservableProperty]
    private string _errorMessage = "";

    public HomeViewModel(MainWindowViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
    }

    [RelayCommand]
    private async Task OpenFile()
    {
        ErrorMessage = "";

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var topLevel = TopLevel.GetTopLevel(desktop.MainWindow);
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select an audio file",
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("Audio") { Patterns = new[] { "*.mp3", "*.wav" } } }
            });

            if (files.Any())
            {
                var file = files[0];
                var path = file.Path.LocalPath;
                var extension = Path.GetExtension(path).ToLower();

                if (extension == ".mp3" || extension == ".wav")
                {
                    _selectedFilePath = path;     
                    FileName = file.Name;        
                    IsFileSelected = true;       

                    SelectButtonText = "Select Another File";

                    ErrorMessage = "";
                }
                else
                {
                    ErrorMessage = "Unsupported format. Please select a .mp3 or .wav file.";
                    IsFileSelected = false;
                    FileName = "No file selected";
                }
            }
        }
    }

    [RelayCommand]
    private void Analyze()
    {
        if (IsFileSelected && !string.IsNullOrEmpty(_selectedFilePath))
        {

            _mainViewModel.GoToAnalysis(_selectedFilePath);
        }
    }

    [RelayCommand]
    public void StartLive()
    {
        _mainViewModel.GoToLiveAnalysis();
    }
    public void Reset()
    {
        FileName = "No file selected";
        IsFileSelected = false;
        ErrorMessage = "";
        _selectedFilePath = string.Empty;

        SelectButtonText = "Select File";
    }

}