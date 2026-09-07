
using System.Runtime.InteropServices;
using Avalonia.Layout;

namespace CrossMacro.UI.Views;

public partial class MainWindow : Window
{
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(TextBox.CopyingToClipboardEvent, OnTextBoxCopyingToClipboard, RoutingStrategies.Bubble);
        AddHandler(TextBox.CuttingToClipboardEvent, OnTextBoxCuttingToClipboard, RoutingStrategies.Bubble);
        PropertyChanged += OnWindowPropertyChanged;
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        ApplyRoundedWindowCornersIfSupported();
        UpdateResizeHotZonesHitTestability();
        Dispatcher.UIThread.Post(
            static state =>
            {
                var window = (MainWindow)state!;
                RefreshContentLayout(window.MainContentControl);
            },
            this,
            DispatcherPriority.Render);
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
        {
            UpdateResizeHotZonesHitTestability();
        }
    }

    private void UpdateResizeHotZonesHitTestability()
    {
        ResizeHotZones.IsHitTestVisible = WindowState == WindowState.Normal;
    }

    private void OnResizeGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (WindowState != WindowState.Normal
            || sender is not Border { Tag: string edgeName }
            || !Enum.TryParse<WindowEdge>(edgeName, out var edge)
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        BeginResizeDrag(edge, e);
    }

    private void ApplyRoundedWindowCornersIfSupported()
    {
        if (!OperatingSystem.IsWindows() || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        try
        {
            var hwnd = TryGetPlatformHandle()?.Handle ?? nint.Zero;
            if (hwnd == nint.Zero)
            {
                return;
            }

            var preference = DWMWCP_ROUND;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[MainWindow] Rounded window corners are unavailable");
        }
    }

    internal static void RefreshContentLayout(Layoutable content)
    {
        ArgumentNullException.ThrowIfNull(content);
        content.InvalidateMeasure();
        content.InvalidateArrange();
        content.InvalidateVisual();
    }

    private void OnTextBoxCopyingToClipboard(object? sender, RoutedEventArgs e)
    {
        var textBox = e.Source as TextBox ?? sender as TextBox;
        if (textBox is null)
        {
            return;
        }

        e.Handled = true;
        _ = CopyTextToClipboardAsync(textBox);
    }

    private void OnTextBoxCuttingToClipboard(object? sender, RoutedEventArgs e)
    {
        var textBox = e.Source as TextBox ?? sender as TextBox;
        if (textBox is null)
        {
            return;
        }

        e.Handled = true;
        _ = CutTextToClipboardAsync(textBox);
    }

    private static async Task CopyTextToClipboardAsync(TextBox textBox)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(textBox)?.Clipboard;
            if (clipboard is null)
            {
                Log.Warning("[TextBoxClipboard] Clipboard is unavailable; copy skipped");
                return;
            }

            _ = await TextBoxClipboardHandler.TryCopyAsync(
                textBox,
                text => clipboard.SetTextAsync(text)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warning(ex, "[TextBoxClipboard] Unexpected copy failure; keeping the application alive");
        }
    }

    private static async Task CutTextToClipboardAsync(TextBox textBox)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(textBox)?.Clipboard;
            if (clipboard is null)
            {
                Log.Warning("[TextBoxClipboard] Clipboard is unavailable; cut skipped");
                return;
            }

            _ = await TextBoxClipboardHandler.TryCutAsync(
                textBox,
                text => clipboard.SetTextAsync(text)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warning(ex, "[TextBoxClipboard] Unexpected cut failure; keeping the application alive");
        }
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnMinimizeWindow(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnCloseApp(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnDismissAppNotification(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainWindowViewModel vm)
        {
            vm.DismissAppNotification();
        }
    }
}
