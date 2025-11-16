using System;
using System.Windows.Input;
using Susurri.GUI.Services;

namespace Susurri.GUI.ViewModels;

public class DashboardViewModel : ViewModelBase
{
    private readonly AppState _appState;
    private int _dhtPort = 7070;
    private string _statusMessage = string.Empty;

    public DashboardViewModel(AppState appState)
    {
        _appState = appState;

        StartDhtCommand = new RelayCommand(StartDht, CanStartDht);
        StopDhtCommand = new RelayCommand(StopDht, CanStopDht);
        LogoutCommand = new RelayCommand(Logout);
    }

    public AppState AppState => _appState;

    public int DhtPort
    {
        get => _dhtPort;
        set => SetField(ref _dhtPort, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    public string PublicKeyDisplay
    {
        get
        {
            if (_appState.PublicKey == null) return "Not available";
            var hex = Convert.ToHexString(_appState.PublicKey);
            return hex.Length > 16 ? $"{hex[..8]}...{hex[^8..]}" : hex;
        }
    }

    public ICommand StartDhtCommand { get; }
    public ICommand StopDhtCommand { get; }
    public ICommand LogoutCommand { get; }

    private bool CanStartDht() => !_appState.IsDhtRunning;
    private bool CanStopDht() => _appState.IsDhtRunning;

    private void StartDht()
    {
        try
        {
            // Simulate DHT start
            _appState.IsDhtRunning = true;
            _appState.DhtNodeId = Guid.NewGuid().ToString("N")[..16];
            StatusMessage = $"DHT started on port {DhtPort}";

            ((RelayCommand)StartDhtCommand).RaiseCanExecuteChanged();
            ((RelayCommand)StopDhtCommand).RaiseCanExecuteChanged();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to start DHT: {ex.Message}";
        }
    }

    private void StopDht()
    {
        try
        {
            _appState.IsDhtRunning = false;
            _appState.DhtNodeId = null;
            StatusMessage = "DHT stopped";

            ((RelayCommand)StartDhtCommand).RaiseCanExecuteChanged();
            ((RelayCommand)StopDhtCommand).RaiseCanExecuteChanged();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to stop DHT: {ex.Message}";
        }
    }

    private void Logout()
    {
        if (_appState.IsDhtRunning)
        {
            StopDht();
        }
        _appState.Logout();
    }
}
