using CrossMacro.UI.Controls;
using Avalonia.Controls;
using Xunit;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;

namespace CrossMacro.UI.Controls.Tests;

public sealed class HotkeyCaptureTests
{
    private static readonly Task PlatformReady = Task.Run(StartHeadlessUiThread);

    // Dispatcher.UIThread is per-thread: test methods hop threads across awaits,
    // so capture the dedicated UI dispatcher instance once and always use it.
    private static Dispatcher? uiDispatcher;

    private static void StartHeadlessUiThread()
    {
        // Controls bind to the dispatcher of the thread that constructs them and
        // that first touches the shared XAML cache. A dedicated headless UI
        // thread with a running dispatcher loop gives every test in this class
        // one stable owner thread for the control.
        var started = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            _ = AppBuilder.Configure<Avalonia.Application>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                .SetupWithoutStarting();
            uiDispatcher = Dispatcher.UIThread;
            started.Set();
            Dispatcher.UIThread.MainLoop(CancellationToken.None);
        })
        {
            IsBackground = true,
            Name = "HotkeyCaptureTests-UI",
        };
        thread.Start();
        started.Wait();
    }

    private static async Task<T> OnUiAsync<T>(Func<T> action)
    {
        await PlatformReady.ConfigureAwait(false);
        return await uiDispatcher!.InvokeAsync(action);
    }

    private static Task OnUiAsync(Action action) => OnUiAsync<object?>(() =>
    {
        action();
        return null;
    });

    /// <summary>
    /// Waits until everything the capture pipeline posted at normal priority has
    /// run: the pipeline's continuation runs on a pool thread and posts back to
    /// the UI dispatcher, so the test must give the dispatcher a turn before
    /// asserting.
    /// </summary>
    private static Task FlushUiAsync() => OnUiAsync<object?>(() => null);

    private static HotkeyCapture CreateControl(TaskCompletionSource<string> completion)
    {
        return new HotkeyCapture
        {
            Hotkey = "F8",
            CaptureNextKeyAsyncOverride = _ => completion.Task,
            // Mirror PostToUi's real contract: hand the action to the UI
            // dispatcher instead of running it inline on the (pool) caller.
            UiPostOverride = action => { _ = uiDispatcher!.InvokeAsync(action); },
        };
    }

    [Fact]
    public void ImplementsDisposableOwnershipContract()
    {
        Assert.Contains(typeof(IDisposable), typeof(HotkeyCapture).GetInterfaces());
    }

    [Theory]
    [InlineData(Key.Enter)]
    [InlineData(Key.Space)]
    public async Task KeyboardActivation_StartsCaptureAndAppliesResult(Key key)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var control = await OnUiAsync(() => CreateControl(completion));
        try
        {
            var captureTask = await OnUiAsync(() => control.InvokeKeyDownForTestAsync(key));
            Assert.True(await OnUiAsync(() => control.IsCapturing));

            completion.SetResult("F12");
            // captureTask completes only after the pipeline posted its UI update,
            // so awaiting it first removes the race with the flush barrier.
            await captureTask;
            await FlushUiAsync();

            Assert.False(await OnUiAsync(() => control.IsCapturing));
            Assert.Equal("F12", await OnUiAsync(() => control.Hotkey));
        }
        finally
        {
            await OnUiAsync(() => control.Dispose());
        }
    }

    [Fact]
    public async Task KeyboardActivation_FromCancelChildDoesNotStartCapture()
    {
        var completion = new TaskCompletionSource<string>();
        var control = await OnUiAsync(() => CreateControl(completion));
        try
        {
            _ = await OnUiAsync(() => control.InvokeKeyDownForTestAsync(Key.Enter, new Button()));

            Assert.False(await OnUiAsync(() => control.IsCapturing));
            Assert.Equal("F8", await OnUiAsync(() => control.Hotkey));
        }
        finally
        {
            await OnUiAsync(() => control.Dispose());
        }
    }

    [Fact]
    public async Task CancelledCapture_DoesNotApplyLateResult()
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var control = await OnUiAsync(() => CreateControl(completion));
        try
        {
            var captureTask = await OnUiAsync(() => control.InvokeKeyDownForTestAsync(Key.Enter));
            Assert.True(await OnUiAsync(() => control.IsCapturing));

            await OnUiAsync(() => control.CancelCaptureForTest());
            Assert.False(await OnUiAsync(() => control.IsCapturing));

            completion.SetResult("Escape");
            await captureTask;
            await FlushUiAsync();

            Assert.Equal("F8", await OnUiAsync(() => control.Hotkey));
        }
        finally
        {
            await OnUiAsync(() => control.Dispose());
        }
    }
}
