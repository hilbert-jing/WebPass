using WebPass.Web.Domain.Enums;

namespace WebPass.Web.Application.Assets;

public sealed record ServerAssetInput(
    string BusinessIp,
    string Location,
    AliveStatus AliveStatus,
    string ComputerName,
    string SystemName,
    string? OperatingSystemVersion,
    string? DatabaseVersion,
    string? Notes);

public sealed class ServerAssetConcurrencyException : InvalidOperationException
{
    public ServerAssetConcurrencyException() : base("The server was changed by another user. Reload and try again.")
    {
    }
}
