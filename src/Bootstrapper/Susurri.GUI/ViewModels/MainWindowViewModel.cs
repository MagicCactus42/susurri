using Susurri.GUI.Services;

namespace Susurri.GUI.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private readonly AppSession _session;
    private ViewModelBase _currentView;

    public MainWindowViewModel(AppSession session)
    {
        _session = session;
        _currentView = CreateLogin();
    }

    public ViewModelBase CurrentView
    {
        get => _currentView;
        set => SetField(ref _currentView, value);
    }

    private LoginViewModel CreateLogin() => new(_session, OnLoggedIn, ShowGenerate);

    private void OnLoggedIn() => CurrentView = new ShellViewModel(_session, ShowLogin);

    private void ShowGenerate() => CurrentView = new GenerateViewModel(_session, ShowLogin);

    private void ShowLogin() => CurrentView = CreateLogin();
}
