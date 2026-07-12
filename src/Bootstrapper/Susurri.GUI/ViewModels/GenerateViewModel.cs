using System;
using System.Collections.ObjectModel;
using System.Linq;
using Susurri.GUI.Services;

namespace Susurri.GUI.ViewModels;

public sealed record WordItem(int Index, string Word);

public class GenerateViewModel : ViewModelBase
{
    private readonly AppSession _session;
    private readonly Action _onBack;

    private int _selectedWordCount = 12;
    private string _passphrase = string.Empty;
    private string _entropyText = string.Empty;
    private string _error = string.Empty;
    private bool _copied;

    public GenerateViewModel(AppSession session, Action onBack)
    {
        _session = session;
        _onBack = onBack;

        GenerateCommand = new RelayCommand(Generate);
        CopyCommand = new RelayCommand(() => _ = CopyAsync(), () => HasWords);
        BackCommand = new RelayCommand(() => _onBack());
    }

    public int[] WordCounts { get; } = { 12, 15, 18, 21, 24 };

    public int SelectedWordCount
    {
        get => _selectedWordCount;
        set => SetField(ref _selectedWordCount, value);
    }

    public ObservableCollection<WordItem> Words { get; } = new();

    public bool HasWords => Words.Count > 0;

    public string EntropyText
    {
        get => _entropyText;
        private set => SetField(ref _entropyText, value);
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

    public bool Copied
    {
        get => _copied;
        private set => SetField(ref _copied, value);
    }

    public RelayCommand GenerateCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand BackCommand { get; }

    private void Generate()
    {
        Error = string.Empty;
        Copied = false;
        try
        {
            _passphrase = _session.GeneratePassphrase(SelectedWordCount);
            var words = _passphrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            Words.Clear();
            foreach (var (word, index) in words.Select((w, i) => (w, i)))
                Words.Add(new WordItem(index + 1, word));

            EntropyText = $"{words.Length} words · {words.Length * 11 - words.Length / 3} bits of entropy";
            OnPropertyChanged(nameof(HasWords));
            CopyCommand.RaiseCanExecuteChanged();
        }
        catch (ArgumentException ex)
        {
            Error = $"{ex.Message} Valid word counts: 12, 15, 18, 21, 24.";
        }
    }

    private async System.Threading.Tasks.Task CopyAsync()
    {
        if (string.IsNullOrEmpty(_passphrase))
            return;
        await ClipboardHelper.SetTextAsync(_passphrase);
        Copied = true;
    }
}
