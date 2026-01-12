using System.Windows.Input;
using Susurri.GUI.Services;

namespace Susurri.GUI.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    private readonly AppState _appState;
    private string _statusMessage = string.Empty;

    public SettingsViewModel(AppState appState)
    {
        _appState = appState;
        ClearCacheCommand = new RelayCommand(ClearCache);
    }

    public AppState AppState => _appState;

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    public ICommand ClearCacheCommand { get; }

    private void ClearCache()
    {
        _appState.CredentialsCached = false;
        StatusMessage = "Credentials cache cleared";
    }
}
