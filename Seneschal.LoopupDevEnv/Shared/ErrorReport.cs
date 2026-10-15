using MudBlazor;

namespace DevEnvironment.Shared;

public sealed record ErrorEntry(string Title, string Message);

/// <summary>One way to report a failure: a short toast that opens a details dialog, never a wall of text in the page.</summary>
public static class ErrorReport
{
    /// <summary>Splits "Could not start Identity: the file is locked" into a title and the detail behind it.</summary>
    public static ErrorEntry Split(string message)
    {
        var text = message.Trim();
        var split = text.IndexOf(": ", StringComparison.Ordinal);
        return split is > 0 and < 80
            ? new ErrorEntry(text[..split], text[(split + 2)..].Trim())
            : new ErrorEntry(text.Length > 80 ? text[..77] + "..." : text, text.Length > 80 ? text : "");
    }

    public static Task ShowAsync(IDialogService dialogs, IReadOnlyList<ErrorEntry> errors, string? title = null)
    {
        var parameters = new DialogParameters<ErrorsDialog>
        {
            { x => x.Errors, errors },
            { x => x.Heading, title },
        };
        return dialogs.ShowAsync<ErrorsDialog>("Errors", parameters, new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true, CloseOnEscapeKey = true });
    }

    /// <summary>A toast with the short title. It stays until closed, and "Details" opens the full text.</summary>
    public static void Toast(ISnackbar snackbar, IDialogService dialogs, string message)
    {
        var error = Split(message);
        snackbar.Add(error.Title, Severity.Error, options =>
        {
            options.RequireInteraction = true;
            if (error.Message.Length == 0) return;
            options.Action = "Details";
            options.OnClick = toast =>
            {
                _ = ShowAsync(dialogs, [error]);
                return Task.FromResult(toast);
            };
        });
    }
}
