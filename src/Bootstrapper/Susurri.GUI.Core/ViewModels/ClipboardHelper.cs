using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace Susurri.GUI.ViewModels;

public static class ClipboardHelper
{
    public static async Task SetTextAsync(string text)
    {
        var topLevel = Application.Current?.ApplicationLifetime switch
        {
            IClassicDesktopStyleApplicationLifetime desktop => desktop.MainWindow as TopLevel,
            ISingleViewApplicationLifetime { MainView: { } view } => TopLevel.GetTopLevel(view),
            _ => null
        };

        if (topLevel?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }
}
