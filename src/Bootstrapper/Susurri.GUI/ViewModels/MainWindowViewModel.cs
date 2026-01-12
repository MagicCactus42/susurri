using System;
using System.Windows.Input;
using Susurri.GUI.Services;

namespace Susurri.GUI.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private readonly AppState _appState;
    private ViewModelBase _currentView;
    private string _currentViewName = "Login";

    public MainWindowViewModel(AppState appState)
    {
        _appState = appState;
        _currentView = new LoginViewModel(appState, NavigateToDashboard);

        NavigateCommand = new RelayCommand<string>(Navigate);
    }

    public AppState AppState => _appState;

    public ViewModelBase CurrentView
    {
        get => _currentView;
        set => SetField(ref _currentView, value);
    }

    public string CurrentViewName
    {
        get => _currentViewName;
        set => SetField(ref _currentViewName, value);
    }

    public ICommand NavigateCommand { get; }

    private void Navigate(string? viewName)
    {
        if (string.IsNullOrEmpty(viewName)) return;

        CurrentViewName = viewName;
        CurrentView = viewName switch
        {
            "Login" => new LoginViewModel(_appState, NavigateToDashboard),
            "Dashboard" => new DashboardViewModel(_appState),
            "Generate" => new GenerateViewModel(_appState),
            "Settings" => new SettingsViewModel(_appState),
            _ => CurrentView
        };
    }

    private void NavigateToDashboard()
    {
        Navigate("Dashboard");
    }
}

public class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Func<T?, bool>? _canExecute;

    public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke((T?)parameter) ?? true;
    public void Execute(object? parameter) => _execute((T?)parameter);
    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => _execute();
    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
