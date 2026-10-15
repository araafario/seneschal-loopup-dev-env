using Microsoft.JSInterop;

namespace DevEnvironment.Shared;

/// <summary>
/// Lets the rows of a section be dragged into a new order. The page script (sortable.js) reports where a row was dropped and
/// <paramref name="onReorder"/> is called with the id of the dragged row, the id of the row it was dropped next to and whether it
/// went after that row. The component saves the new order, which is then drawn like any other change.
/// </summary>
internal sealed class SortableList : IAsyncDisposable
{
    private readonly string _sectionId;
    private readonly Action<string, string, bool> _onReorder;
    private IJSObjectReference? _module;
    private DotNetObjectReference<SortableList>? _self;

    private SortableList(string sectionId, Action<string, string, bool> onReorder)
    {
        _sectionId = sectionId;
        _onReorder = onReorder;
    }

    /// <summary>Starts listening on the section element. Without the script (or a browser that is gone) the rows simply cannot be dragged.</summary>
    public static async Task<SortableList> CreateAsync(IJSRuntime js, string sectionId, Action<string, string, bool> onReorder)
    {
        var list = new SortableList(sectionId, onReorder);
        try
        {
            list._module = await js.InvokeAsync<IJSObjectReference>("import", "/_ext/loopup-dev-env/sortable.js");
            list._self = DotNetObjectReference.Create(list);
            await list._module.InvokeVoidAsync("init", sectionId, list._self);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or TaskCanceledException)
        {
            // The page is closing or the script is not there: ordering by hand is not available, everything else is
        }
        return list;
    }

    [JSInvokable]
    public void Reorder(string dragId, string targetId, bool after) => _onReorder(dragId, targetId, after);

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_module is not null)
            {
                await _module.InvokeVoidAsync("dispose", _sectionId);
                await _module.DisposeAsync();
            }
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or TaskCanceledException)
        {
            // The page is already gone
        }
        _self?.Dispose();
    }
}
