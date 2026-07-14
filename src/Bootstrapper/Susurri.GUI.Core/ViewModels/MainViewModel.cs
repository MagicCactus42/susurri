using Susurri.GUI.Services;

namespace Susurri.GUI.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly AppSession _session;
    private readonly bool _autoSelectConversation;
    private ViewModelBase _currentView;

    public MainViewModel(AppSession session, bool autoSelectConversation = true)
    {
        _session = session;
        _autoSelectConversation = autoSelectConversation;
        _currentView = CreateLogin();
    }

    public ViewModelBase CurrentView
    {
        get => _currentView;
        set => SetField(ref _currentView, value);
    }

    private LoginViewModel CreateLogin() => new(_session, OnLoggedIn, ShowGenerate);

    private void OnLoggedIn() => CurrentView = new ShellViewModel(_session, ShowLogin, _autoSelectConversation);

    private void ShowGenerate() => CurrentView = new GenerateViewModel(_session, ShowLogin);

    private void ShowLogin() => CurrentView = CreateLogin();
}
