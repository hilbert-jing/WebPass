using WebPass.Web.Application.Assets;
using WebPass.Web.Application.Secrets;

namespace WebPass.Web.Application.Importing;

public enum ImportFileType
{
    Csv,
    Xlsx,
}

public enum ImportOperation
{
    Create,
    Update,
    Skip,
}

public sealed record ImportRowError(int RowNumber, string Field, string Message);

public sealed record ImportPreview(
    Guid Id,
    int TotalRows,
    int CreateCount,
    int UpdateCount,
    int SkipCount,
    IReadOnlyList<ImportRowError> Errors,
    bool HasBlockingErrors,
    DateTimeOffset ExpiresAt);

public sealed record ImportCommitResult(
    Guid JobId,
    int CreatedCount,
    int UpdatedCount,
    int SkippedCount);

public sealed record StagedImportRow(
    int RowNumber,
    ImportOperation Operation,
    Guid AssetId,
    byte[]? RowVersion,
    ServerAssetInput Input,
    SecretEnvelope? PasswordEnvelope);

public sealed record StagedImport(
    ImportPreview Preview,
    ImportFileType FileType,
    Guid ActorUserId,
    IReadOnlyList<StagedImportRow> Rows);
