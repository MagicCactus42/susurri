using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;

namespace Susurri.GUI.Views;

public partial class MobileMainView : UserControl
{
    private IInsetsManager? _insets;
    private IInputPane? _inputPane;
    private double _imeHeight;

    public MobileMainView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var topLevel = TopLevel.GetTopLevel(this);
        _insets = topLevel?.InsetsManager;
        _inputPane = topLevel?.InputPane;
        if (_insets == null)
            return;
        _insets.DisplayEdgeToEdge = true;
        _insets.SafeAreaChanged += OnSafeAreaChanged;
        if (_inputPane != null)
            _inputPane.StateChanged += OnInputPaneStateChanged;
        UpdatePadding();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_insets != null)
            _insets.SafeAreaChanged -= OnSafeAreaChanged;
        if (_inputPane != null)
            _inputPane.StateChanged -= OnInputPaneStateChanged;
        _insets = null;
        _inputPane = null;
    }

    private void OnSafeAreaChanged(object? sender, SafeAreaChangedArgs e)
        => UpdatePadding();

    private void OnInputPaneStateChanged(object? sender, InputPaneStateEventArgs e)
    {
        _imeHeight = e.NewState == InputPaneState.Open ? Math.Max(0, e.EndRect.Height) : 0;
        UpdatePadding();
    }

    private void UpdatePadding()
    {
        if (_insets == null)
            return;
        var safe = _insets.SafeAreaPadding;
        Padding = new Thickness(safe.Left, safe.Top, safe.Right, safe.Bottom + _imeHeight);
    }
}
