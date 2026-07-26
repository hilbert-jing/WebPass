using Microsoft.EntityFrameworkCore;
using WebPass.Web.Application.Secrets;
using WebPass.Web.Data;
using WebPass.Web.Infrastructure.Secrets;
using Xunit;

namespace WebPass.IntegrationTests.Data;

public sealed class DataEncryptionKeyConcurrencyTests
{
    [Fact]
    public async Task Concurrent_first_requests_share_one_active_key()
    {
        var databaseName = $"WebPass_KeyRace_{Guid.NewGuid():N}";
        var connectionString =
            $"Server=localhost\\SQLEXPRESS;Database={databaseName};Integrated Security=True;TrustServerCertificate=True";
        await using var setup = NewDatabase(connectionString);
        await setup.Database.MigrateAsync();
        try
        {
            using var barrier = new Barrier(2);
            var wrapper = new BarrierDataKeyWrapper(barrier);
            await using var firstDb = NewDatabase(connectionString);
            await using var secondDb = NewDatabase(connectionString);
            var firstProvider = new DatabaseDataEncryptionKeyProvider(firstDb, wrapper);
            var secondProvider = new DatabaseDataEncryptionKeyProvider(secondDb, wrapper);

            var materials = await Task.WhenAll(
                Task.Run(() => firstProvider.GetActiveAsync(default)),
                Task.Run(() => secondProvider.GetActiveAsync(default)));
            using var first = materials[0];
            using var second = materials[1];

            setup.ChangeTracker.Clear();
            Assert.Equal(first.KeyVersion, second.KeyVersion);
            Assert.Equal(first.Key.ToArray(), second.Key.ToArray());
            Assert.Equal(1, await setup.DataEncryptionKeys.CountAsync(key => key.RetiredAt == null));
        }
        finally
        {
            await setup.Database.EnsureDeletedAsync();
        }
    }

    private static WebPassDbContext NewDatabase(string connectionString) => new(
        new DbContextOptionsBuilder<WebPassDbContext>()
            .UseSqlServer(connectionString)
            .Options);

    private sealed class BarrierDataKeyWrapper(Barrier barrier) : IDataKeyWrapper
    {
        private const byte Mask = 0x96;

        public string CurrentCertificateThumbprint { get; } = new('D', 40);

        public byte[] WrapKey(ReadOnlySpan<byte> dataKey)
        {
            if (!barrier.SignalAndWait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Both initialization requests did not reach the wrapping boundary.");
            return Transform(dataKey);
        }

        public byte[] UnwrapKey(ReadOnlySpan<byte> wrappedKey, string certificateThumbprint) =>
            Transform(wrappedKey);

        private static byte[] Transform(ReadOnlySpan<byte> value) =>
            value.ToArray().Select(item => (byte)(item ^ Mask)).ToArray();
    }
}
