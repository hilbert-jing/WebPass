namespace WebPass.Web.Application.Exporting;

public enum ExportFormat
{
    Csv,
    Xlsx,
}

public sealed record ExportFile(
    byte[] Content,
    string ContentType,
    string FileName);

public sealed record ExportRow(
    string BusinessIp,
    string Location,
    string AliveStatus,
    string ComputerName,
    string SystemName,
    string? OperatingSystemVersion,
    string? DatabaseVersion,
    string? Notes);

public sealed record PasswordExportRow(
    ExportRow Asset,
    string? Password);
