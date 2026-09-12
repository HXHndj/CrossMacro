
namespace CrossMacro.Application.Automation;

public interface ITextExpansionStore
{
    public Task<IList<TextExpansionEntry>> LoadAsync();
    public Task ReloadAsync(string profileConfigDirectory) => LoadAsync();
    public Task SaveAsync(IEnumerable<TextExpansionEntry> expansions);
}

/// <summary>
/// Coordinates active-profile text-expansion mutations with a profile switch.
/// Implementations must hold the scope until the complete load/mutate/save
/// operation has finished.
/// </summary>
public interface IProfileTextExpansionOperationScope
{
    public Task<IAsyncDisposable> EnterAsync(CancellationToken cancellationToken = default);
}
