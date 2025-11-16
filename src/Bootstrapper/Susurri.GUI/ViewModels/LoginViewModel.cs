using System;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Input;
using dotnetstandard_bip39;
using Susurri.GUI.Services;

namespace Susurri.GUI.ViewModels;

public class LoginViewModel : ViewModelBase
{
    private readonly AppState _appState;
    private readonly Action _onLoginSuccess;
    private string _username = string.Empty;
    private string _passphrase = string.Empty;
    private string _errorMessage = string.Empty;
    private string _generatedPassphrase = string.Empty;
    private bool _isLoading;
    private bool _showPassphrase;
    private bool _cacheCredentials;

    public LoginViewModel(AppState appState, Action onLoginSuccess)
    {
        _appState = appState;
        _onLoginSuccess = onLoginSuccess;

        LoginCommand = new RelayCommand(Login, CanLogin);
        SignupCommand = new RelayCommand(Signup, CanLogin);
        GeneratePassphraseCommand = new RelayCommand(GeneratePassphrase);
        CopyAndUseCommand = new RelayCommand(CopyAndUse);
        TogglePassphraseVisibilityCommand = new RelayCommand(TogglePassphraseVisibility);
    }

    public string Username
    {
        get => _username;
        set
        {
            if (SetField(ref _username, value))
                ((RelayCommand)LoginCommand).RaiseCanExecuteChanged();
        }
    }

    public string Passphrase
    {
        get => _passphrase;
        set
        {
            if (SetField(ref _passphrase, value))
                ((RelayCommand)LoginCommand).RaiseCanExecuteChanged();
        }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (SetField(ref _errorMessage, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    public string GeneratedPassphrase
    {
        get => _generatedPassphrase;
        set
        {
            if (SetField(ref _generatedPassphrase, value))
                OnPropertyChanged(nameof(HasGeneratedPassphrase));
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => SetField(ref _isLoading, value);
    }

    public bool ShowPassphrase
    {
        get => _showPassphrase;
        set => SetField(ref _showPassphrase, value);
    }

    public bool CacheCredentials
    {
        get => _cacheCredentials;
        set => SetField(ref _cacheCredentials, value);
    }

    public bool HasGeneratedPassphrase => !string.IsNullOrEmpty(GeneratedPassphrase);
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public string StatusMessage => ErrorMessage;

    public ICommand LoginCommand { get; }
    public ICommand SignupCommand { get; }
    public ICommand GeneratePassphraseCommand { get; }
    public ICommand CopyAndUseCommand { get; }
    public ICommand TogglePassphraseVisibilityCommand { get; }

    private bool CanLogin()
    {
        return !string.IsNullOrWhiteSpace(Username) &&
               !string.IsNullOrWhiteSpace(Passphrase) &&
               !IsLoading;
    }

    private void Login()
    {
        ErrorMessage = string.Empty;

        if (Username.Length < 3 || Username.Length > 32)
        {
            ErrorMessage = "Username must be 3-32 characters";
            return;
        }

        var words = Passphrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 6)
        {
            ErrorMessage = "Passphrase must contain at least 6 words";
            return;
        }

        try
        {
            IsLoading = true;

            // Generate deterministic public key from passphrase
            var publicKey = DerivePublicKey(Passphrase);

            _appState.Login(Username, publicKey);
            _appState.CredentialsCached = CacheCredentials;

            _onLoginSuccess();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Login failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void GeneratePassphrase()
    {
        try
        {
            var entropy = new byte[16]; // 128 bits for 12 words
            RandomNumberGenerator.Fill(entropy);
            var entropyHex = Convert.ToHexString(entropy).ToLowerInvariant();

            var bip = new BIP39();
            GeneratedPassphrase = bip.EntropyToMnemonic(entropyHex, BIP39Wordlist.English);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to generate passphrase: {ex.Message}";
        }
    }

    private void Signup()
    {
        // For now, signup works the same as login - it creates a new identity
        Login();
    }

    private async void CopyAndUse()
    {
        if (string.IsNullOrEmpty(GeneratedPassphrase)) return;

        try
        {
            var topLevel = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;

            if (topLevel?.Clipboard != null)
            {
                await topLevel.Clipboard.SetTextAsync(GeneratedPassphrase);
            }
            Passphrase = GeneratedPassphrase;
        }
        catch
        {
            // Clipboard access may fail on some systems
            Passphrase = GeneratedPassphrase;
        }
    }

    private void TogglePassphraseVisibility()
    {
        ShowPassphrase = !ShowPassphrase;
    }

    private static byte[] DerivePublicKey(string passphrase)
    {
        var passphraseBytes = Encoding.UTF8.GetBytes(passphrase);
        var hash = SHA256.HashData(passphraseBytes);
        return hash;
    }
}
