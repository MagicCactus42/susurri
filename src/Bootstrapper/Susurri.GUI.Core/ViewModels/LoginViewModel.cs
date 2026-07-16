using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Susurri.GUI.Services;
using Susurri.Shared.Abstractions.Security;

namespace Susurri.GUI.ViewModels;

public class LoginViewModel : ViewModelBase
{
    private readonly AppSession _session;
    private readonly Action _onLoggedIn;
    private readonly Action _onGenerate;

    private string _username = string.Empty;
    private string _passphrase = string.Empty;
    private string _portText = string.Empty;
    private string _bootstrapText = string.Empty;
    private string _cachePin = string.Empty;
    private string _newCachePin = string.Empty;
    private bool _saveCredentials;
    private bool _isBusy;
    private string _busyText = string.Empty;
    private string _error = string.Empty;

    public LoginViewModel(AppSession session, Action onLoggedIn, Action onGenerate)
    {
        _session = session;
        _onLoggedIn = onLoggedIn;
        _onGenerate = onGenerate;
        CacheAvailable = session.CacheExists;

        var saved = GuiSettings.LoadBootstrapNodes();
        _bootstrapText = string.Join(" ", saved.Length > 0 ? saved : session.Seeds());

        LoginCommand = new RelayCommand(() => _ = LoginAsync(), () => !IsBusy);
        UnlockCacheCommand = new RelayCommand(() => _ = UnlockCacheAsync(), () => !IsBusy);
        GenerateCommand = new RelayCommand(() => _onGenerate());
    }

    public bool CacheAvailable { get; }

    public RelayCommand LoginCommand { get; }
    public RelayCommand UnlockCacheCommand { get; }
    public RelayCommand GenerateCommand { get; }

    public string Username
    {
        get => _username;
        set => SetField(ref _username, value);
    }

    public string Passphrase
    {
        get => _passphrase;
        set
        {
            if (SetField(ref _passphrase, value))
                OnPropertyChanged(nameof(WordCountText));
        }
    }

    public string WordCountText
    {
        get
        {
            var count = _passphrase.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            return $"BIP39 · {count} / {SecurityLimits.MinPassphraseWords}+ words";
        }
    }

    public string PortText
    {
        get => _portText;
        set => SetField(ref _portText, value);
    }

    public string BootstrapText
    {
        get => _bootstrapText;
        set => SetField(ref _bootstrapText, value);
    }

    public string CachePin
    {
        get => _cachePin;
        set => SetField(ref _cachePin, value);
    }

    public string NewCachePin
    {
        get => _newCachePin;
        set => SetField(ref _newCachePin, value);
    }

    public bool SaveCredentials
    {
        get => _saveCredentials;
        set => SetField(ref _saveCredentials, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetField(ref _isBusy, value))
                return;
            LoginCommand.RaiseCanExecuteChanged();
            UnlockCacheCommand.RaiseCanExecuteChanged();
        }
    }

    public string BusyText
    {
        get => _busyText;
        private set => SetField(ref _busyText, value);
    }

    public string Error
    {
        get => _error;
        private set
        {
            if (SetField(ref _error, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    private async Task UnlockCacheAsync()
    {
        Error = string.Empty;
        if (string.IsNullOrEmpty(CachePin))
        {
            Error = "Enter the cache password.";
            return;
        }

        var cached = _session.TryLoadCached(CachePin);
        if (cached == null)
        {
            Error = "Could not unlock the cached credentials — wrong password or corrupt cache.";
            return;
        }

        Username = cached.Value.Username;
        Passphrase = cached.Value.Passphrase;
        await LoginAsync(skipCacheSave: true);
    }

    private async Task LoginAsync(bool skipCacheSave = false)
    {
        Error = string.Empty;

        var username = Username.Trim();
        if (username.Length < SecurityLimits.MinUsernameLength || username.Length > SecurityLimits.MaxUsernameLength)
        {
            Error = $"Username must be {SecurityLimits.MinUsernameLength}-{SecurityLimits.MaxUsernameLength} characters.";
            return;
        }
        if (username.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-'))
        {
            Error = "Username may only contain letters, digits, underscores, and hyphens.";
            return;
        }

        var words = Passphrase.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (words < SecurityLimits.MinPassphraseWords)
        {
            Error = $"Passphrase must be at least {SecurityLimits.MinPassphraseWords} words — generate one if you don't have an identity yet.";
            return;
        }
        if (words > SecurityLimits.MaxPassphraseWords)
        {
            Error = $"Passphrase cannot exceed {SecurityLimits.MaxPassphraseWords} words.";
            return;
        }

        var port = 0;
        if (!string.IsNullOrWhiteSpace(PortText) &&
            (!int.TryParse(PortText.Trim(), out port) || port < 0 || port > 65535))
        {
            Error = "Port must be a number between 1 and 65535, or empty for automatic.";
            return;
        }

        var seeds = new List<string>();
        foreach (var token in BootstrapText.Split(
                     new[] { ' ', ',', ';', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = AppSession.NormalizeSeed(token);
            if (normalized == null)
            {
                Error = $"Bootstrap node '{token}' must be an ip:port pair.";
                return;
            }
            if (!seeds.Contains(normalized))
                seeds.Add(normalized);
        }

        IsBusy = true;
        try
        {
            var progress = new Progress<string>(s => BusyText = s);
            await _session.LoginAsync(username, Passphrase, port, seeds, progress);
            GuiSettings.SaveBootstrapNodes(seeds.ToArray());

            if (!skipCacheSave && SaveCredentials)
            {
                if (NewCachePin.Length >= 8)
                    await _session.SaveCacheAsync(username, Passphrase, NewCachePin);
                else
                    Error = "Cache password too short (8+ chars) — credentials were not cached.";
            }

            _onLoggedIn();
        }
        catch (Exception ex)
        {
            Error = $"Login failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
    }
}
