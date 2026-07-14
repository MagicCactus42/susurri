using System;
using System.Collections.ObjectModel;
using Susurri.GUI.Services;

namespace Susurri.GUI.ViewModels;

public class TransfersViewModel : ViewModelBase
{
    private readonly AppSession _session;
    private readonly ConversationStore _store;

    public TransfersViewModel(AppSession session, ConversationStore store)
    {
        _session = session;
        _store = store;
        _store.TransfersChanged += () => OnPropertyChanged(nameof(HasAny));
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasAny));

        AcceptCommand = new RelayCommand<TransferModel>(t => _ = AcceptAsync(t));
        RejectCommand = new RelayCommand<TransferModel>(t => _ = RejectAsync(t));
    }

    public ObservableCollection<TransferModel> Items => _store.Transfers;

    public bool HasAny => Items.Count > 0;

    public RelayCommand<TransferModel> AcceptCommand { get; }
    public RelayCommand<TransferModel> RejectCommand { get; }

    private async System.Threading.Tasks.Task AcceptAsync(TransferModel? transfer)
    {
        var chat = _session.Chat;
        if (chat == null || transfer == null)
            return;
        try
        {
            await chat.AcceptFileTransferAsync(transfer.TransferId);
            _store.SyncTransfers();
        }
        catch (Exception ex)
        {
            transfer.Detail = ex.Message;
        }
    }

    private async System.Threading.Tasks.Task RejectAsync(TransferModel? transfer)
    {
        var chat = _session.Chat;
        if (chat == null || transfer == null)
            return;
        try
        {
            await chat.RejectFileTransferAsync(transfer.TransferId);
            _store.SyncTransfers();
        }
        catch (Exception ex)
        {
            transfer.Detail = ex.Message;
        }
    }
}
