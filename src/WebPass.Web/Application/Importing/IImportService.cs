namespace WebPass.Web.Application.Importing;

public interface IImportService
{
    Task<ImportPreview> PreviewAsync(
        Stream source,
        ImportFileType type,
        Guid actorId,
        CancellationToken ct);

    Task<ImportCommitResult> CommitAsync(
        Guid previewId,
        Guid actorId,
        CancellationToken ct);
}
