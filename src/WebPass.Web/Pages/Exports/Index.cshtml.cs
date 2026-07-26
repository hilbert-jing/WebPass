using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebPass.Web.Application.Assets;
using WebPass.Web.Application.Authorization;
using WebPass.Web.Application.Exporting;

namespace WebPass.Web.Pages.Exports;

[Authorize(Policy = PermissionCode.ExportData)]
public sealed class IndexModel(AssetExportService exports) : PageModel
{
    [BindProperty]
    public ExportFormat Format { get; set; } = ExportFormat.Xlsx;

    [BindProperty]
    public ServerListQuery Query { get; set; } = new();

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostDownloadAsync(
        CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            var file = await exports.ExportAsync(
                Format,
                Query,
                UserId(),
                ct);
            Response.Headers.CacheControl = "no-store";
            Response.Headers.Pragma = "no-cache";
            return File(file.Content, file.ContentType, file.FileName);
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (ArgumentException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return Page();
        }
    }

    private Guid UserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
