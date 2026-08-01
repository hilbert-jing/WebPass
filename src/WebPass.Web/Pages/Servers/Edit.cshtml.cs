using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebPass.Web.Application.Assets;
using WebPass.Web.Application.Authorization;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Infrastructure.Authorization;

namespace WebPass.Web.Pages.Servers;

[Authorize(Policy = PermissionCode.AssetView)]
public sealed class EditModel(WebPassDbContext db, ServerAssetService assetService, PermissionAuthorizationHandler permissions) : PageModel
{
    [BindProperty]
    public IndexModel.ServerForm Input { get; set; } = new();

    public Guid? AssetId { get; private set; }
    public string? RowVersion { get; private set; }
    public ServerSnapshot? CurrentSnapshot { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid? id, string? businessIp, CancellationToken ct)
    {
        if (!await permissions.IsAllowedAsync(UserId(), id is null ? PermissionCode.AssetCreate : PermissionCode.AssetEdit, ct)) return Forbid();
        if (id is null)
        {
            if (!string.IsNullOrWhiteSpace(businessIp)) Input.BusinessIp = businessIp;
            return Page();
        }

        var asset = await db.ServerAssets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && !x.IsArchived, ct);
        if (asset is null) return NotFound();
        AssetId = asset.Id;
        RowVersion = Convert.ToBase64String(asset.RowVersion);
        Input = ToForm(asset);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, string? rowVersion, CancellationToken ct)
    {
        if (!await permissions.IsAllowedAsync(UserId(), PermissionCode.AssetEdit, ct)) return Forbid();
        try
        {
            await assetService.UpdateAsync(id, Input.ToInput(), DecodeRowVersion(rowVersion), UserId(), ct);
            return RedirectToPage("/Servers/Index");
        }
        catch (ServerAssetConcurrencyException exception)
        {
            var current = await db.ServerAssets
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == id && !x.IsArchived, ct);
            if (current is null) return NotFound();

            AssetId = current.Id;
            CurrentSnapshot = ServerSnapshot.From(current);
            RowVersion = Convert.ToBase64String(current.RowVersion);
            ModelState.Remove(nameof(rowVersion));
            ModelState.AddModelError(string.Empty, exception.Message);
            Response.StatusCode = StatusCodes.Status409Conflict;
            return Page();
        }
        catch (ArgumentException exception)
        {
            return BadRequest(exception.Message);
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return await OnGetAsync(id, null, ct);
        }
    }

    private Guid UserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static IndexModel.ServerForm ToForm(ServerAsset asset) => new()
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

    public sealed record ServerSnapshot(
        string BusinessIp,
        string Location,
        Domain.Enums.AliveStatus AliveStatus,
        string ComputerName,
        string SystemName,
        string? OperatingSystemVersion,
        string? DatabaseVersion,
        string? Notes)
    {
        public static ServerSnapshot From(ServerAsset asset) => new(
            asset.BusinessIp,
            asset.Location,
            asset.AliveStatus,
            asset.ComputerName,
            asset.SystemName,
            asset.OperatingSystemVersion,
            asset.DatabaseVersion,
            asset.Notes);
    }
}
