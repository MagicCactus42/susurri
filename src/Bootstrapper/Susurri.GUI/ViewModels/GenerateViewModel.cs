using System;
using System.Security.Cryptography;
using System.Windows.Input;
using dotnetstandard_bip39;
using Susurri.GUI.Services;

namespace Susurri.GUI.ViewModels;

public class GenerateViewModel : ViewModelBase
{
    private readonly AppState _appState;
    private string _generatedPassphrase = string.Empty;
    private int _wordCount = 12;
    private string _statusMessage = string.Empty;
    private bool _copied;

    public GenerateViewModel(AppState appState)
    {
        _appState = appState;
        GenerateCommand = new RelayCommand(Generate);
        CopyCommand = new RelayCommand(Copy);
    }

    public string GeneratedPassphrase
    {
        get => _generatedPassphrase;
        set => SetField(ref _generatedPassphrase, value);
    }

    public int WordCount
    {
        get => _wordCount;
        set => SetField(ref _wordCount, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    public bool Copied
    {
        get => _copied;
        set => SetField(ref _copied, value);
    }

    public int[] WordCountOptions => new[] { 12, 15, 18, 21, 24 };

    public ICommand GenerateCommand { get; }
    public ICommand CopyCommand { get; }

    private void Generate()
    {
        try
        {
            var entropyBytes = WordCount switch
            {
                12 => 16,
                15 => 20,
                18 => 24,
                21 => 28,
                24 => 32,
                _ => 16
            };

            var entropy = new byte[entropyBytes];
            RandomNumberGenerator.Fill(entropy);
            var entropyHex = Convert.ToHexString(entropy).ToLowerInvariant();

            var bip = new BIP39();
            GeneratedPassphrase = bip.EntropyToMnemonic(entropyHex, BIP39Wordlist.English);
            StatusMessage = $"Generated {WordCount}-word passphrase ({entropyBytes * 8} bits of entropy)";
            Copied = false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    private async void Copy()
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
                Copied = true;
                StatusMessage = "Copied to clipboard!";
            }
        }
        catch
        {
            StatusMessage = "Failed to copy to clipboard";
        }
    }
}
