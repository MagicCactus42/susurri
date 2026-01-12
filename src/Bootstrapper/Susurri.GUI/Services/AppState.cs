using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Susurri.GUI.Services;

public class AppState : INotifyPropertyChanged
{
    private bool _isLoggedIn;
    private string? _username;
    private byte[]? _publicKey;
    private bool _isDhtRunning;
    private string? _dhtNodeId;
    private string? _statusMessage;
    private bool _credentialsCached;

    public bool IsLoggedIn
    {
        get => _isLoggedIn;
        set => SetField(ref _isLoggedIn, value);
    }

    public string? Username
    {
        get => _username;
        set => SetField(ref _username, value);
    }

    public byte[]? PublicKey
    {
        get => _publicKey;
        set => SetField(ref _publicKey, value);
    }

    public bool IsDhtRunning
    {
        get => _isDhtRunning;
        set => SetField(ref _isDhtRunning, value);
    }

    public string? DhtNodeId
    {
        get => _dhtNodeId;
        set => SetField(ref _dhtNodeId, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    public bool CredentialsCached
    {
        get => _credentialsCached;
        set => SetField(ref _credentialsCached, value);
    }

    public void Login(string username, byte[] publicKey)
    {
        Username = username;
        PublicKey = publicKey;
        IsLoggedIn = true;
        StatusMessage = $"Logged in as {username}";
    }

    public void Logout()
    {
        Username = null;
        PublicKey = null;
        IsLoggedIn = false;
        StatusMessage = "Logged out";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
