using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebPass.Web.Application.Assets;
using WebPass.Web.Application.Authorization;
using WebPass.Web.Application.Networking;
using WebPass.Web.Application.Ping;
using WebPass.Web.Data;
using WebPass.Web.Domain.Enums;
using WebPass.Web.Infrastructure.Authorization;
using WebPass.Web.Presentation;

namespace WebPass.Web.Pages.Servers;

[Authorize(Policy = PermissionCode.AssetView)]
public sealed class IndexModel(
    ServerAssetService assetService,
    PingService pingService,
    PermissionAuthorizationHandler permissions,
    WebPassDbContext db) : PageModel
{
    public ServerListPage Results { get; private set; } = new([], 0, false, 0, 50);
    public IReadOnlyList<SubnetFilterOption> SubnetOptions { get; private set; } = [];
    public SelectedSubnetSummary? SelectedSubnet { get; private set; }
    public bool CanCreate { get; private set; }
    public bool CanEdit { get; private set; }
    public bool CanArchive { get; private set; }
    public bool CanPing { get; private set; }
    public bool CanMarkAlive { get; private set; }
    public bool CanReveal { get; private set; }
    public ServerPingFeedback? PingFeedback { get; private set; }

    [BindProperty(SupportsGet = true)]
    public ServerListQuery Query { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public ServerForm Input { get; set; } = new();

    public async Task OnGetAsync(CancellationToken ct)
    {
        ClearRegistrationValidationState();
        PingFeedback = PingCommandWorkflow.TakeFeedback(TempData);
        await LoadAsync(ct);
    }

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
            ModelState.AddModelError(string.Empty, "无法登记服务器：请检查 IP、网段和必填信息。");
            await LoadAsync(ct);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostArchiveAsync(Guid id, string? rowVersion, CancellationToken ct)
    {
        ClearRegistrationValidationState();
        return await ExecuteAsync(
            PermissionCode.AssetArchive,
            () => assetService.ArchiveAsync(id, DecodeRowVersion(rowVersion), UserId(), ct),
            "服务器已归档。",
            ct);
    }

    public async Task<IActionResult> OnPostPingAsync(Guid id, CancellationToken ct)
    {
        ClearRegistrationValidationState();
        if (!await AllowedAsync(PermissionCode.PingExecute, ct)) return Forbid();
        return await PingCommandWorkflow.ExecuteAsync(
            pingService,
            id,
            UserId(),
            TempData,
            feedback => RedirectToPage(
                "/Servers/Index",
                PingCommandWorkflow.TargetRouteValues(feedback)),
            ct);
    }

    public async Task<IActionResult> OnPostMarkAliveAsync(Guid id, string? rowVersion, CancellationToken ct)
    {
        ClearRegistrationValidationState();
        return await ExecuteAsync(
            PermissionCode.StatusMarkAlive,
            () => pingService.MarkAliveAsync(id, UserId(), DecodeRowVersion(rowVersion), ct),
            "服务器已标记为存活。",
            ct);
    }

    private async Task<IActionResult> ExecuteAsync(
        string permission,
        Func<Task> command,
        string successMessage,
        CancellationToken ct)
    {
        if (!await AllowedAsync(permission, ct)) return Forbid();
        try
        {
            await command();
            TempData["StatusMessage"] = successMessage;
            return RedirectToPage(new { Query.Search, Query.SubnetId, Query.Status, Query.IncludeArchived, Query.PoolMode, Query.Skip, Query.Take });
        }
        catch (ArgumentException)
        {
            return BadRequest("请求无效，请刷新页面后重试。");
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (ServerAssetConcurrencyException)
        {
            return new ObjectResult(
                "该服务器已被其他用户修改，请刷新后重试。")
            {
                StatusCode = StatusCodes.Status409Conflict,
            };
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or KeyNotFoundException)
        {
            ModelState.AddModelError(
                string.Empty,
                "无法完成服务器操作，请刷新后重试。");
            await LoadAsync(ct);
            return Page();
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Results = await assetService.ListAsync(Query, ct);
        var subnets = await db.Subnets.AsNoTracking()
            .OrderBy(x => x.NetworkAddress)
            .Select(x => new SubnetFilterOption(x.Id, x.Name, x.Cidr))
            .ToListAsync(ct);
        SubnetOptions = subnets;
        SelectedSubnet = null;
        if (Query.SubnetId is { } subnetId &&
            subnets.SingleOrDefault(x => x.Id == subnetId) is { } selected)
        {
            var registered = await db.ServerAssets.LongCountAsync(
                x => x.SubnetId == subnetId && !x.IsArchived,
                ct);
            var usable = Ipv4Cidr.Parse(selected.Cidr).GetUsableAddressCount();
            SelectedSubnet = new(
                selected.Id,
                selected.Name,
                selected.Cidr,
                registered,
                usable);
        }

        CanCreate = await AllowedAsync(PermissionCode.AssetCreate, ct);
        CanEdit = await AllowedAsync(PermissionCode.AssetEdit, ct);
        CanArchive = await AllowedAsync(PermissionCode.AssetArchive, ct);
        CanPing = await AllowedAsync(PermissionCode.PingExecute, ct);
        CanMarkAlive = await AllowedAsync(PermissionCode.StatusMarkAlive, ct);
        CanReveal = await AllowedAsync(PermissionCode.SecretReveal, ct);
    }

    private Task<bool> AllowedAsync(string permission, CancellationToken ct) => permissions.IsAllowedAsync(UserId(), permission, ct);
    private Guid UserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private void ClearRegistrationValidationState()
    {
        ModelState.Remove(nameof(ServerForm.BusinessIp));
        ModelState.Remove(nameof(ServerForm.Location));
        ModelState.Remove(nameof(ServerForm.ComputerName));
        ModelState.Remove(nameof(ServerForm.SystemName));
    }

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
        [Required(ErrorMessage = "请输入业务 IP。")]
        public string BusinessIp { get; set; } = string.Empty;
        [Required(ErrorMessage = "请输入位置。")]
        public string Location { get; set; } = string.Empty;
        public AliveStatus AliveStatus { get; set; } = AliveStatus.Unknown;
        [Required(ErrorMessage = "请输入计算机名。")]
        public string ComputerName { get; set; } = string.Empty;
        [Required(ErrorMessage = "请输入系统名称。")]
        public string SystemName { get; set; } = string.Empty;
        public string? OperatingSystemVersion { get; set; }
        public string? DatabaseVersion { get; set; }
        public string? Notes { get; set; }
        [DataType(DataType.Password)]
        public string? Password { get; set; }

        public ServerAssetInput ToInput() => new(BusinessIp, Location, AliveStatus, ComputerName, SystemName,
            OperatingSystemVersion, DatabaseVersion, Notes, Password);
    }

    public sealed record SubnetFilterOption(Guid Id, string Name, string Cidr);

    public sealed record SelectedSubnetSummary(
        Guid Id,
        string Name,
        string Cidr,
        long RegisteredCount,
        long UsableAddressCount);
}
