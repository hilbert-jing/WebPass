using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebPass.Web.Application.Assets;
using WebPass.Web.Application.Authorization;
using WebPass.Web.Application.Ping;
using WebPass.Web.Domain.Enums;

namespace WebPass.Web.Pages.Servers;

[Authorize(Policy = PermissionCode.AssetView)]
public sealed class IndexModel(ServerAssetService assetService, PingService pingService) : PageModel
{
    public ServerListPage Results { get; private set; } = new([], 0, false, 0, 50);

    [BindProperty(SupportsGet = true)]
    public ServerListQuery Query { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public ServerForm Input { get; set; } = new();

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            await LoadAsync(ct);
            return Page();
        }

        try
        {
            await assetService.CreateAsync(Input.ToInput(), UserId(), ct);
            return RedirectToPage(new { Query.Search, Query.SubnetId, Query.Status, Query.IncludeArchived, Query.PoolMode, Query.Skip, Query.Take });
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await LoadAsync(ct);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostArchiveAsync(Guid id, string? rowVersion, CancellationToken ct) =>
        await ExecuteAsync(() => assetService.ArchiveAsync(id, DecodeRowVersion(rowVersion), UserId(), ct), ct);

    public async Task<IActionResult> OnPostPingAsync(Guid id, CancellationToken ct) =>
        await ExecuteAsync(() => pingService.ExecuteAsync(id, UserId(), ct), ct);

    public async Task<IActionResult> OnPostMarkAliveAsync(Guid id, string? rowVersion, CancellationToken ct) =>
        await ExecuteAsync(() => pingService.MarkAliveAsync(id, UserId(), DecodeRowVersion(rowVersion), ct), ct);

    private async Task<IActionResult> ExecuteAsync(Func<Task> command, CancellationToken ct)
    {
        try
        {
            await command();
            return RedirectToPage(new { Query.Search, Query.SubnetId, Query.Status, Query.IncludeArchived, Query.PoolMode, Query.Skip, Query.Take });
        }
        catch (ArgumentException exception)
        {
            return BadRequest(exception.Message);
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException or KeyNotFoundException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await LoadAsync(ct);
            return Page();
        }
    }

    private async Task LoadAsync(CancellationToken ct) => Results = await assetService.ListAsync(Query, ct);
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

    public sealed class ServerForm
    {
        [Required]
        public string BusinessIp { get; set; } = string.Empty;
        [Required]
        public string Location { get; set; } = string.Empty;
        public AliveStatus AliveStatus { get; set; } = AliveStatus.Unknown;
        [Required]
        public string ComputerName { get; set; } = string.Empty;
        [Required]
        public string SystemName { get; set; } = string.Empty;
        public string? OperatingSystemVersion { get; set; }
        public string? DatabaseVersion { get; set; }
        public string? Notes { get; set; }

        public ServerAssetInput ToInput() => new(BusinessIp, Location, AliveStatus, ComputerName, SystemName,
            OperatingSystemVersion, DatabaseVersion, Notes);
    }
}
