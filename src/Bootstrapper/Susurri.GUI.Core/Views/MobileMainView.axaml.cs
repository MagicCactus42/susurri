using Avalonia;
using Avalonia.Controls;

namespace Susurri.GUI.Views;

public partial class MobileMainView : UserControl
{
    public MobileMainView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this)?.InsetsManager is { } insets)
            insets.DisplayEdgeToEdge = true;
    }
}
