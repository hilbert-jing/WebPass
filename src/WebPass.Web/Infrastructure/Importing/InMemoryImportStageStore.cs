using System.Collections.Concurrent;
using WebPass.Web.Application.Importing;

namespace WebPass.Web.Infrastructure.Importing;

public sealed class InMemoryImportStageStore(
    TimeProvider? clock = null) : IDisposable
{
    private readonly ConcurrentDictionary<Guid, StagedImport> _stages = new();

    public void Store(StagedImport stage)
    {
        RemoveExpired();
        _stages[stage.Preview.Id] = stage;
    }

    public StagedImport Get(Guid id)
    {
        RemoveExpired();
        if (!_stages.TryGetValue(id, out var stage))
            throw new KeyNotFoundException("The import preview was not found or has expired.");
        return stage;
    }

    public void Remove(Guid id) => _stages.TryRemove(id, out _);

    public void Dispose() => _stages.Clear();

    private void RemoveExpired()
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        foreach (var stage in _stages)
        {
            if (stage.Value.Preview.ExpiresAt <= now)
                _stages.TryRemove(stage.Key, out _);
        }
    }
}
