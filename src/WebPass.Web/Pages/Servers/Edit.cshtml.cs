using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebPass.Web.Application.Assets;
using WebPass.Web.Application.Authorization;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;

namespace WebPass.Web.Pages.Servers;

[Authorize(Policy = PermissionCode.AssetView)]
public sealed class EditModel(WebPassDbContext db, ServerAssetService assetService) : PageModel
{
    [BindProperty]
    public IndexModel.ServerForm Input { get; set; } = new();

    public Guid? AssetId { get; private set; }
    public string? RowVersion { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid? id, string? businessIp, CancellationToken ct)
    {
        if (id is null)
        {
            if (!string.IsNullOrWhiteSpace(businessIp)) Input.BusinessIp = businessIp;
            return Page();
        }

        var asset = await db.ServerAssets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && !x.IsArchived, ct);
        if (asset is null) return NotFound();
        AssetId = asset.Id;
        RowVersion = Convert.ToBase64String(asset.RowVersion);
        Input = new IndexModel.ServerForm
        {
            BusinessIp = asset.BusinessIp,
            Location = asset.Location,
            AliveStatus = asset.AliveStatus,
            ComputerName = asset.ComputerName,
            SystemName = asset.SystemName,
            OperatingSystemVersion = asset.OperatingSystemVersion,
            DatabaseVersion = asset.DatabaseVersion,
            Notes = asset.Notes,
        };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, string? rowVersion, CancellationToken ct)
    {
        try
        {
            await assetService.UpdateAsync(id, Input.ToInput(), DecodeRowVersion(rowVersion), UserId(), ct);
            return RedirectToPage("/Servers/Index");
        }
        catch (ServerAssetConcurrencyException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return await OnGetAsync(id, null, ct);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(exception.Message);
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return await OnGetAsync(id, null, ct);
        }
    }

    private Guid UserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static byte[] DecodeRowVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A row version is required.", nameof(value));
        try
        {
            var rowVersion = Convert.FromBase64String(value);
            return rowVersion.Length == 0 ? throw new ArgumentException("A row version is required.", nameof(value)) : rowVersion;
        }
        catch (FormatException)
        {
            throw new ArgumentException("The row version is invalid.", nameof(value));
        }
    }
}
