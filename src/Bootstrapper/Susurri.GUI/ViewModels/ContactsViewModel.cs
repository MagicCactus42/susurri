using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Susurri.GUI.Services;
using Susurri.Modules.DHT.Core.Contacts;

namespace Susurri.GUI.ViewModels;

public sealed class ContactModel : ViewModelBase
{
    private bool _verified;

    public required Contact Source { get; init; }
    public string Petname => Source.Petname;
    public string Username => Source.Username;
    public string KeyShort
    {
        get
        {
            var hex = Convert.ToHexString(Source.EncryptionPublicKey).ToLowerInvariant();
            return hex.Length >= 12 ? $"{hex[..8]}…{hex[^4..]}" : hex;
        }
    }

    public bool Verified
    {
        get => _verified;
        set
        {
            if (SetField(ref _verified, value))
                OnPropertyChanged(nameof(VerifiedText));
        }
    }

    public string VerifiedText => _verified ? "✓ VERIFIED" : "UNVERIFIED";
}

public class ContactsViewModel : ViewModelBase
{
    private readonly AppSession _session;

    private string _newPetname = string.Empty;
    private string _newUsername = string.Empty;
    private string _status = string.Empty;
    private bool _statusIsError;
    private ContactModel? _selected;
    private string _safetyNumber = string.Empty;
    private string _checkResult = string.Empty;
    private bool _checkFailed;

    public ContactsViewModel(AppSession session)
    {
        _session = session;

        AddCommand = new RelayCommand(() => _ = AddAsync());
        MarkVerifiedCommand = new RelayCommand(MarkVerified, () => Selected is { Verified: false });
        CheckCommand = new RelayCommand(() => _ = CheckAsync(), () => Selected != null);
        RemoveCommand = new RelayCommand(Remove, () => Selected != null);

        Refresh();
    }

    public ObservableCollection<ContactModel> Items { get; } = new();

    public RelayCommand AddCommand { get; }
    public RelayCommand MarkVerifiedCommand { get; }
    public RelayCommand CheckCommand { get; }
    public RelayCommand RemoveCommand { get; }

    public string NewPetname
    {
        get => _newPetname;
        set => SetField(ref _newPetname, value);
    }

    public string NewUsername
    {
        get => _newUsername;
        set => SetField(ref _newUsername, value);
    }

    public string Status
    {
        get => _status;
        private set
        {
            if (SetField(ref _status, value))
                OnPropertyChanged(nameof(HasStatus));
        }
    }

    public bool HasStatus => !string.IsNullOrEmpty(_status);

    public bool StatusIsError
    {
        get => _statusIsError;
        private set => SetField(ref _statusIsError, value);
    }

    public ContactModel? Selected
    {
        get => _selected;
        set
        {
            if (!SetField(ref _selected, value))
                return;
            OnPropertyChanged(nameof(HasSelection));
            CheckResult = string.Empty;
            SafetyNumber = ComputeSafetyNumber(value);
            MarkVerifiedCommand.RaiseCanExecuteChanged();
            CheckCommand.RaiseCanExecuteChanged();
            RemoveCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasSelection => _selected != null;

    public string SafetyNumber
    {
        get => _safetyNumber;
        private set => SetField(ref _safetyNumber, value);
    }

    public string CheckResult
    {
        get => _checkResult;
        private set
        {
            if (SetField(ref _checkResult, value))
                OnPropertyChanged(nameof(HasCheckResult));
        }
    }

    public bool HasCheckResult => !string.IsNullOrEmpty(_checkResult);

    public bool CheckFailed
    {
        get => _checkFailed;
        private set => SetField(ref _checkFailed, value);
    }

    public void Refresh()
    {
        var book = _session.Chat?.Contacts;
        Items.Clear();
        if (book == null)
            return;
        foreach (var contact in book.All().OrderBy(c => c.Petname, StringComparer.OrdinalIgnoreCase))
            Items.Add(new ContactModel { Source = contact, Verified = contact.Verified });
    }

    private string ComputeSafetyNumber(ContactModel? contact)
    {
        var chat = _session.Chat;
        if (contact == null || chat == null)
            return string.Empty;
        return Susurri.Modules.DHT.Core.Contacts.SafetyNumber.Compute(
            chat.LocalPublicKey, chat.LocalSigningPublicKey,
            contact.Source.EncryptionPublicKey, contact.Source.SigningPublicKey);
    }

    private async Task AddAsync()
    {
        var chat = _session.Chat;
        var book = chat?.Contacts;
        if (chat == null || book == null)
            return;

        StatusIsError = false;
        var petname = NewPetname.Trim();
        var username = NewUsername.Trim();
        if (petname.Length == 0 || username.Length == 0)
        {
            StatusIsError = true;
            Status = "Petname and username are both required.";
            return;
        }
        if (book.FindByPetname(petname) != null)
        {
            StatusIsError = true;
            Status = $"Petname '{petname}' is already taken.";
            return;
        }

        Status = $"Looking up @{username} in the DHT…";
        Susurri.Modules.DHT.Core.Kademlia.UserPublicKeyRecord? record;
        try
        {
            record = await chat.GetPublicKeyAsync(username);
        }
        catch (Exception ex)
        {
            StatusIsError = true;
            Status = $"Lookup failed: {ex.Message}";
            return;
        }

        if (record == null)
        {
            StatusIsError = true;
            Status = $"No DHT record found for @{username}.";
            return;
        }

        var added = book.Add(new Contact
        {
            Petname = petname,
            Username = username,
            EncryptionPublicKey = record.EncryptionPublicKey,
            SigningPublicKey = record.SigningPublicKey,
            AddedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        });

        if (!added)
        {
            StatusIsError = true;
            Status = "Could not pin the contact — it may already exist under another petname.";
            return;
        }

        NewPetname = string.Empty;
        NewUsername = string.Empty;
        Status = $"Pinned '{petname}' — compare the safety number out of band, then mark verified.";
        Refresh();
        Selected = Items.FirstOrDefault(c => c.Petname == petname);
    }

    private void MarkVerified()
    {
        var book = _session.Chat?.Contacts;
        if (book == null || Selected == null)
            return;
        if (book.SetVerified(Selected.Petname, true))
        {
            Selected.Verified = true;
            MarkVerifiedCommand.RaiseCanExecuteChanged();
            Status = $"'{Selected.Petname}' marked as verified.";
            StatusIsError = false;
        }
    }

    private async Task CheckAsync()
    {
        var chat = _session.Chat;
        if (chat == null || Selected == null)
            return;

        CheckFailed = false;
        CheckResult = "Fetching the live DHT record…";
        try
        {
            var live = await chat.LookupPublicKeyFreshAsync(Selected.Username);
            if (live == null)
            {
                CheckFailed = true;
                CheckResult = "No live DHT record found — cannot compare right now.";
                return;
            }

            var matches = live.EncryptionPublicKey.SequenceEqual(Selected.Source.EncryptionPublicKey) &&
                          live.SigningPublicKey.SequenceEqual(Selected.Source.SigningPublicKey);
            if (matches)
            {
                CheckResult = "Live DHT record matches the pinned key ✓";
            }
            else
            {
                CheckFailed = true;
                CheckResult = "LIVE RECORD DIFFERS FROM THE PINNED KEY — possible impersonation attempt. Your messages still use the pinned key.";
            }
        }
        catch (Exception ex)
        {
            CheckFailed = true;
            CheckResult = $"Check failed: {ex.Message}";
        }
    }

    private void Remove()
    {
        var book = _session.Chat?.Contacts;
        if (book == null || Selected == null)
            return;
        var name = Selected.Petname;
        if (book.Remove(name))
        {
            Status = $"Removed '{name}' — the key is no longer pinned.";
            StatusIsError = false;
            Selected = null;
            Refresh();
        }
    }
}
