namespace WebPass.Web.Application.Authorization;

public static class PermissionCode
{
    public const string AssetView = nameof(AssetView);
    public const string AssetCreate = nameof(AssetCreate);
    public const string AssetEdit = nameof(AssetEdit);
    public const string AssetArchive = nameof(AssetArchive);
    public const string PingExecute = nameof(PingExecute);
    public const string StatusMarkAlive = nameof(StatusMarkAlive);
    public const string ImportData = nameof(ImportData);
    public const string ExportData = nameof(ExportData);
    public const string SecretReveal = nameof(SecretReveal);
    public const string SubnetManage = nameof(SubnetManage);
    public const string AuditView = nameof(AuditView);
    public const string AdministratorPolicy = "Administrator";

    public static IReadOnlySet<string> OrdinaryUserCodes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        AssetView, AssetCreate, AssetEdit, AssetArchive, PingExecute, StatusMarkAlive,
        ImportData, ExportData, SecretReveal, SubnetManage, AuditView,
    };
}
