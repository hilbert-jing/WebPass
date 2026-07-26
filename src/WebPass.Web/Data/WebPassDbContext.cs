using Microsoft.EntityFrameworkCore;
using WebPass.Web.Domain.Entities;

namespace WebPass.Web.Data;

public sealed class WebPassDbContext(DbContextOptions<WebPassDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<UserPermission> UserPermissions => Set<UserPermission>();
    public DbSet<Subnet> Subnets => Set<Subnet>();
    public DbSet<ServerAsset> ServerAssets => Set<ServerAsset>();
    public DbSet<PingResult> PingResults => Set<PingResult>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<DataEncryptionKey> DataEncryptionKeys => Set<DataEncryptionKey>();
    public DbSet<ServerSecret> ServerSecrets => Set<ServerSecret>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<AppUser>(entity =>
        {
            entity.HasIndex(x => x.Username).IsUnique();
            entity.Property(x => x.Username).HasMaxLength(128);
            entity.Property(x => x.PasswordHash).HasMaxLength(512);
            entity.Property(x => x.RowVersion).IsRowVersion();
        });

        builder.Entity<UserPermission>(entity =>
        {
            entity.HasKey(x => new { x.UserId, x.PermissionCode });
            entity.Property(x => x.PermissionCode).HasMaxLength(64);
            entity.HasOne(x => x.User).WithMany(x => x.Permissions).HasForeignKey(x => x.UserId);
        });

        builder.Entity<Subnet>(entity =>
        {
            entity.HasIndex(x => x.Cidr).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(128);
            entity.Property(x => x.Cidr).HasMaxLength(32);
            entity.Property(x => x.NetworkAddress).HasMaxLength(15);
            entity.Property(x => x.Location).HasMaxLength(256);
            entity.Property(x => x.RowVersion).IsRowVersion();
        });

        builder.Entity<ServerAsset>(entity =>
        {
            entity.HasIndex(x => x.BusinessIp).IsUnique().HasFilter("[IsArchived] = 0");
            entity.Property(x => x.BusinessIp).HasMaxLength(15);
            entity.Property(x => x.Location).HasMaxLength(256);
            entity.Property(x => x.ComputerName).HasMaxLength(256);
            entity.Property(x => x.SystemName).HasMaxLength(256);
            entity.Property(x => x.AliveStatus).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.RowVersion).IsRowVersion();
            entity.HasOne(x => x.Subnet).WithMany(x => x.ServerAssets).HasForeignKey(x => x.SubnetId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<PingResult>(entity =>
        {
            entity.Property(x => x.TargetIp).HasMaxLength(15);
            entity.Property(x => x.Outcome).HasMaxLength(32);
            entity.Property(x => x.ErrorCode).HasMaxLength(128);
            entity.HasOne(x => x.ServerAsset).WithMany(x => x.PingResults).HasForeignKey(x => x.ServerAssetId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<AuditLog>(entity =>
        {
            entity.Property(x => x.Action).HasMaxLength(128);
            entity.Property(x => x.ObjectType).HasMaxLength(128);
            entity.Property(x => x.ObjectId).HasMaxLength(128);
            entity.Property(x => x.Result).HasMaxLength(64);
            entity.Property(x => x.SourceIp).HasMaxLength(45);
            entity.Property(x => x.CorrelationId).HasMaxLength(128);
        });

        builder.Entity<DataEncryptionKey>(entity =>
        {
            entity.HasKey(x => x.KeyVersion);
            entity.Property(x => x.KeyVersion).ValueGeneratedNever();
            entity.Property(x => x.WrappedKey).HasMaxLength(1024);
            entity.Property(x => x.CertificateThumbprint).HasMaxLength(128);
            entity.Property(x => x.RowVersion).IsRowVersion();
            entity.HasIndex(x => x.RetiredAt).IsUnique().HasFilter("[RetiredAt] IS NULL");
        });

        builder.Entity<ServerSecret>(entity =>
        {
            entity.HasKey(x => x.ServerAssetId);
            entity.Property(x => x.Ciphertext).HasMaxLength(4096);
            entity.Property(x => x.Nonce).HasMaxLength(12).IsFixedLength();
            entity.Property(x => x.AuthenticationTag).HasMaxLength(16).IsFixedLength();
            entity.Property(x => x.RowVersion).IsRowVersion();
            entity.HasOne(x => x.ServerAsset).WithOne().HasForeignKey<ServerSecret>(x => x.ServerAssetId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.DataEncryptionKey).WithMany(x => x.ServerSecrets).HasForeignKey(x => x.KeyVersion)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
