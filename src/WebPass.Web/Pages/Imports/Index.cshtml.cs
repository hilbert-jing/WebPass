using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebPass.Web.Application.Authorization;
using WebPass.Web.Application.Importing;

namespace WebPass.Web.Pages.Imports;

[Authorize(Policy = PermissionCode.ImportData)]
[RequestFormLimits(MultipartBodyLengthLimit = 11 * 1024 * 1024)]
public sealed class IndexModel(IImportService imports) : PageModel
{
    [BindProperty]
    public IFormFile? Upload { get; set; }

    public ImportPreview? Preview { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostPreviewAsync(CancellationToken ct)
    {
        if (Upload is null || Upload.Length == 0)
        {
            ModelState.AddModelError(string.Empty, "Select a CSV or XLSX file.");
            return Page();
        }

        var type = Path.GetExtension(Upload.FileName).ToLowerInvariant() switch
        {
            ".csv" => ImportFileType.Csv,
            ".xlsx" => ImportFileType.Xlsx,
            _ => (ImportFileType?)null,
        };
        if (type is null || !ContentTypeAllowed(type.Value, Upload.ContentType))
        {
            ModelState.AddModelError(string.Empty, "Only CSV and XLSX inventory files are accepted.");
            return Page();
        }

        try
        {
            await using var source = Upload.OpenReadStream();
            Preview = await imports.PreviewAsync(source, type.Value, UserId(), ct);
            return Page();
        }
        catch (Exception exception) when (
            exception is FormatException
            or InvalidOperationException
            or ArgumentException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostCommitAsync(
        Guid previewId,
        CancellationToken ct)
    {
        try
        {
            var result = await imports.CommitAsync(previewId, UserId(), ct);
            TempData["ImportResult"] =
                $"Created {result.CreatedCount}, updated {result.UpdatedCount}, skipped {result.SkippedCount}.";
            return RedirectToPage();
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
            or KeyNotFoundException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return Page();
        }
    }

    private Guid UserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static bool ContentTypeAllowed(
        ImportFileType type,
        string? contentType)
    {
        var mediaType = contentType?.Split(';', 2)[0].Trim();
        return type switch
        {
            ImportFileType.Csv => mediaType is
                "text/csv"
                or "text/plain"
                or "application/vnd.ms-excel"
                or "application/octet-stream",
            ImportFileType.Xlsx => mediaType is
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
                or "application/octet-stream",
            _ => false,
        };
    }
}
