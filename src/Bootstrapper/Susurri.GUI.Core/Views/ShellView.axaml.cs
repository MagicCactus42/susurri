using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Susurri.GUI.ViewModels;

namespace Susurri.GUI.Views;

public partial class ShellView : UserControl
{
    private ShellViewModel? _viewModel;

    public ShellView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookViewModel();
    }

    private void HookViewModel()
    {
        if (_viewModel != null)
            _viewModel.ScrollToEndRequested -= OnScrollToEndRequested;
        _viewModel = DataContext as ShellViewModel;
        if (_viewModel != null)
            _viewModel.ScrollToEndRequested += OnScrollToEndRequested;
    }

    private void OnScrollToEndRequested()
    {
        Dispatcher.UIThread.Post(() => MessagesScroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    private async void OnSendFileClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel == null)
            return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
            return;

        try
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Offer a file",
                AllowMultiple = false
            });
            var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
            if (path != null)
                await _viewModel.SendFileAsync(path);
        }
        catch (Exception)
        {
        }
    }

    private async void OnCopyInviteClick(object? sender, RoutedEventArgs e)
    {
        var code = _viewModel?.InviteCode;
        if (string.IsNullOrEmpty(code))
            return;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
                await clipboard.SetTextAsync(code);
        }
        catch (Exception)
        {
        }
    }
}
