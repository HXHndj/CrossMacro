namespace CrossMacro.UI.Tests.Services;

public sealed class TextBoxClipboardHandlerTests
{
    [Fact]
    public async Task TryCopyAsync_WhenSelectionExists_CopiesSelectedText()
    {
        var textBox = new TextBox { Text = "250" };
        textBox.SelectAll();
        string? copiedText = null;

        var copied = await TextBoxClipboardHandler.TryCopyAsync(
            textBox,
            text =>
            {
                copiedText = text;
                return Task.CompletedTask;
            });

        Assert.True(copied);
        Assert.Equal("250", copiedText);
    }

    [Fact]
    public async Task TryCopyAsync_WhenClipboardThrows_DoesNotPropagate()
    {
        var textBox = new TextBox { Text = "250" };
        textBox.SelectAll();

        var copied = await TextBoxClipboardHandler.TryCopyAsync(
            textBox,
            _ => Task.FromException(new TimeoutException("clipboard is locked")));

        Assert.False(copied);
    }

    [Fact]
    public async Task TryCopyAsync_WhenNothingIsSelected_DoesNotCallClipboard()
    {
        var textBox = new TextBox { Text = "250" };
        var clipboardCallCount = 0;

        var copied = await TextBoxClipboardHandler.TryCopyAsync(
            textBox,
            _ =>
            {
                clipboardCallCount++;
                return Task.CompletedTask;
            });

        Assert.False(copied);
        Assert.Equal(0, clipboardCallCount);
    }

    [Fact]
    public async Task TryCutAsync_WhenClipboardSucceeds_RemovesSelectedText()
    {
        var textBox = new TextBox { Text = "250" };
        textBox.SelectAll();
        string? copiedText = null;

        var cut = await TextBoxClipboardHandler.TryCutAsync(
            textBox,
            text =>
            {
                copiedText = text;
                return Task.CompletedTask;
            });

        Assert.True(cut);
        Assert.Equal("250", copiedText);
        Assert.Empty(textBox.Text);
    }

    [Fact]
    public async Task TryCutAsync_WhenClipboardThrows_KeepsSelectedText()
    {
        var textBox = new TextBox { Text = "250" };
        textBox.SelectAll();

        var cut = await TextBoxClipboardHandler.TryCutAsync(
            textBox,
            _ => Task.FromException(new TimeoutException("clipboard is locked")));

        Assert.False(cut);
        Assert.Equal("250", textBox.Text);
    }

    [Fact]
    public async Task TryCutAsync_WhenSelectionChangesWhileClipboardIsPending_KeepsCurrentTextAndSelection()
    {
        var textBox = new TextBox { Text = "first second" };
        textBox.SelectionStart = 0;
        textBox.SelectionEnd = 5;
        // No RunContinuationsAsynchronously: continuations must stay on the test
        // thread that owns the TextBox (AvaloniaObject verifies thread access).
        var clipboardStarted = new TaskCompletionSource<object?>();
        var releaseClipboard = new TaskCompletionSource<object?>();

        var cutTask = TextBoxClipboardHandler.TryCutAsync(
            textBox,
            async text =>
            {
                _ = clipboardStarted.TrySetResult(null);
                await releaseClipboard.Task;
            });

        await clipboardStarted.Task;
        textBox.SelectionStart = 6;
        textBox.SelectionEnd = 12;
        _ = releaseClipboard.TrySetResult(null);

        Assert.False(await cutTask);
        Assert.Equal("first second", textBox.Text);
        Assert.Equal("second", textBox.SelectedText);
    }

    [Fact]
    public async Task TryCutAsync_WhenTextChangesWhileClipboardIsPending_KeepsChangedText()
    {
        var textBox = new TextBox { Text = "original" };
        textBox.SelectAll();
        // No RunContinuationsAsynchronously: continuations must stay on the test
        // thread that owns the TextBox (AvaloniaObject verifies thread access).
        var clipboardStarted = new TaskCompletionSource<object?>();
        var releaseClipboard = new TaskCompletionSource<object?>();

        var cutTask = TextBoxClipboardHandler.TryCutAsync(
            textBox,
            async text =>
            {
                _ = clipboardStarted.TrySetResult(null);
                await releaseClipboard.Task;
            });

        await clipboardStarted.Task;
        textBox.Text = "user replacement";
        _ = releaseClipboard.TrySetResult(null);

        Assert.False(await cutTask);
        Assert.Equal("user replacement", textBox.Text);
    }
}
