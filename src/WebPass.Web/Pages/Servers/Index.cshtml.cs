using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebPass.Web.Application.Assets;
using WebPass.Web.Application.Authorization;
using WebPass.Web.Application.Ping;
using WebPass.Web.Domain.Enums;
using WebPass.Web.Infrastructure.Authorization;

namespace WebPass.Web.Pages.Servers;

[Authorize(Policy = PermissionCode.AssetView)]
public sealed class IndexModel(ServerAssetService assetService, PingService pingService, PermissionAuthorizationHandler permissions) : PageModel
{
    public ServerListPage Results { get; private set; } = new([], 0, false, 0, 50);
    public bool CanCreate { get; private set; }
    public bool CanEdit { get; private set; }
    public bool CanArchive { get; private set; }
    public bool CanPing { get; private set; }
    public bool CanMarkAlive { get; private set; }
    public bool CanReveal { get; private set; }

    [BindProperty(SupportsGet = true)]
    public ServerListQuery Query { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public ServerForm Input { get; set; } = new();

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken ct)
    {
        if (!await AllowedAsync(PermissionCode.AssetCreate, ct)) return Forbid();
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
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await LoadAsync(ct);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostArchiveAsync(Guid id, string? rowVersion, CancellationToken ct) =>
        await ExecuteAsync(PermissionCode.AssetArchive, () => assetService.ArchiveAsync(id, DecodeRowVersion(rowVersion), UserId(), ct), ct);

    public async Task<IActionResult> OnPostPingAsync(Guid id, CancellationToken ct)
    {
        if (!await AllowedAsync(PermissionCode.PingExecute, ct)) return Forbid();
        try
        {
            var result = await pingService.ExecuteAsync(id, UserId(), ct);
            TempData["PingResult"] = $"{result.Outcome}; latency: {(result.LatencyMilliseconds is null ? "n/a" : result.LatencyMilliseconds + " ms")}; executed: {result.ExecutedAt:O}";
            return RedirectToPage(new { Query.Search, Query.SubnetId, Query.Status, Query.IncludeArchived, Query.PoolMode, Query.Skip, Query.Take });
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (ArgumentException exception) { return BadRequest(exception.Message); }
        catch (InvalidOperationException exception)
        {
            if (exception.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
                return new ObjectResult("Ping rate limit exceeded.") { StatusCode = StatusCodes.Status429TooManyRequests };
            return BadRequest(exception.Message);
        }
        catch (KeyNotFoundException exception) { return NotFound(exception.Message); }
    }

    public async Task<IActionResult> OnPostMarkAliveAsync(Guid id, string? rowVersion, CancellationToken ct) =>
        await ExecuteAsync(PermissionCode.StatusMarkAlive, () => pingService.MarkAliveAsync(id, UserId(), DecodeRowVersion(rowVersion), ct), ct);

    private async Task<IActionResult> ExecuteAsync(string permission, Func<Task> command, CancellationToken ct)
    {
        if (!await AllowedAsync(permission, ct)) return Forbid();
        try
        {
            await command();
            return RedirectToPage(new { Query.Search, Query.SubnetId, Query.Status, Query.IncludeArchived, Query.PoolMode, Query.Skip, Query.Take });
        }
        catch (ArgumentException exception)
        {
            return BadRequest(exception.Message);
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await LoadAsync(ct);
            return Page();
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Results = await assetService.ListAsync(Query, ct);
        CanCreate = await AllowedAsync(PermissionCode.AssetCreate, ct);
        CanEdit = await AllowedAsync(PermissionCode.AssetEdit, ct);
        CanArchive = await AllowedAsync(PermissionCode.AssetArchive, ct);
        CanPing = await AllowedAsync(PermissionCode.PingExecute, ct);
        CanMarkAlive = await AllowedAsync(PermissionCode.StatusMarkAlive, ct);
        CanReveal = await AllowedAsync(PermissionCode.SecretReveal, ct);
    }

    private Task<bool> AllowedAsync(string permission, CancellationToken ct) => permissions.IsAllowedAsync(UserId(), permission, ct);
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
        [DataType(DataType.Password)]
        public string? Password { get; set; }

        public ServerAssetInput ToInput() => new(BusinessIp, Location, AliveStatus, ComputerName, SystemName,
            OperatingSystemVersion, DatabaseVersion, Notes, Password);
    }
}
