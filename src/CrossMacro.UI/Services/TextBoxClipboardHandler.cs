namespace CrossMacro.UI.Services;

internal static class TextBoxClipboardHandler
{
    internal static async Task<bool> TryCopyAsync(
        TextBox textBox,
        Func<string, Task> setTextAsync)
    {
        ArgumentNullException.ThrowIfNull(textBox);
        ArgumentNullException.ThrowIfNull(setTextAsync);

        try
        {
            var selectedText = textBox.SelectedText;
            if (string.IsNullOrEmpty(selectedText))
            {
                return false;
            }

            await setTextAsync(selectedText).ConfigureAwait(true);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warning(ex, "[TextBoxClipboard] Failed to copy selected text; ignoring clipboard backend failure");
            return false;
        }
    }

    internal static async Task<bool> TryCutAsync(
        TextBox textBox,
        Func<string, Task> setTextAsync)
    {
        ArgumentNullException.ThrowIfNull(textBox);
        ArgumentNullException.ThrowIfNull(setTextAsync);

        try
        {
            var originalText = textBox.Text ?? string.Empty;
            var selectionStart = textBox.SelectionStart;
            var selectionEnd = textBox.SelectionEnd;
            var selectedText = textBox.SelectedText;
            if (string.IsNullOrEmpty(selectedText))
            {
                return false;
            }

            await setTextAsync(selectedText).ConfigureAwait(true);

            // Clipboard writes are asynchronous and can yield to another edit.
            // Only delete the range that produced the clipboard payload while
            // the text and selection are still exactly the same.
            if (!IsSelectionSnapshotCurrent(textBox, originalText, selectionStart, selectionEnd, selectedText))
            {
                Log.Warning("[TextBoxClipboard] Selection or text changed while cutting; keeping the current text");
                return false;
            }

            textBox.SelectedText = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warning(ex, "[TextBoxClipboard] Failed to cut selected text; keeping the text unchanged");
            return false;
        }
    }

    private static bool IsSelectionSnapshotCurrent(
        TextBox textBox,
        string originalText,
        int selectionStart,
        int selectionEnd,
        string selectedText)
    {
        return string.Equals(textBox.Text ?? string.Empty, originalText, StringComparison.Ordinal)
            && textBox.SelectionStart == selectionStart
            && textBox.SelectionEnd == selectionEnd
            && string.Equals(textBox.SelectedText, selectedText, StringComparison.Ordinal);
    }
}
