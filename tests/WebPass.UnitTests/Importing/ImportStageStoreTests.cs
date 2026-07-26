using WebPass.Web.Application.Importing;
using WebPass.Web.Infrastructure.Importing;
using Xunit;

namespace WebPass.UnitTests.Importing;

public sealed class ImportStageStoreTests
{
    [Fact]
    public void Preview_is_inaccessible_at_its_fifteen_minute_expiration()
    {
        var now = new DateTimeOffset(2026, 7, 26, 13, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        using var store = new InMemoryImportStageStore(clock);
        var preview = new ImportPreview(
            Guid.NewGuid(),
            0,
            0,
            0,
            0,
            [],
            false,
            now.AddMinutes(15));
        store.Store(new StagedImport(preview, ImportFileType.Csv, Guid.NewGuid(), []));

        Assert.Equal(preview.Id, store.Get(preview.Id).Preview.Id);

        clock.UtcNow = now.AddMinutes(15);

        Assert.Throws<KeyNotFoundException>(() => store.Get(preview.Id));
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
