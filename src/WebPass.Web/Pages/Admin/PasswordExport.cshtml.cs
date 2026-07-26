using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebPass.Web.Application.Assets;
using WebPass.Web.Application.Authorization;
using WebPass.Web.Application.Exporting;

namespace WebPass.Web.Pages.Admin;

[Authorize(Policy = PermissionCode.AdministratorPolicy)]
public sealed class PasswordExportModel(
    AdministratorPasswordExportService exports) : PageModel
{
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
                Query,
                UserId(),
                ct);
            Response.Headers.CacheControl = "no-store";
            Response.Headers.Pragma = "no-cache";
            return File(file.Content, file.ContentType, file.FileName);
        }
        catch (UnauthorizedAccessException)
        {
            return RedirectToPage(
                "/Secrets/Reauthenticate",
                new
                {
                    ReturnUrl = Url.Page("/Admin/PasswordExport"),
                });
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
