using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebPass.Web.Application.Assets;
using WebPass.Web.Application.Authorization;
using WebPass.Web.Application.Exporting;
using WebPass.Web.Presentation;

namespace WebPass.Web.Pages.Exports;

[Authorize(Policy = PermissionCode.ExportData)]
public sealed class IndexModel(AssetExportService exports) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public ExportFormat Format { get; set; } = ExportFormat.Xlsx;

    [BindProperty(SupportsGet = true)]
    public ServerListQuery Query { get; set; } = new();

    public string DownloadLabel =>
        Format == ExportFormat.Csv ? "下载 CSV" : "下载 XLSX";

    public string ScopeSummary
    {
        get
        {
            var filters = new List<string>();
            if (!string.IsNullOrWhiteSpace(Query.Search))
            {
                filters.Add($"搜索“{Query.Search.Trim()}”");
            }

            if (Query.SubnetId is { } subnetId)
            {
                filters.Add($"网段“{subnetId}”");
            }

            if (Query.Status is { } status)
            {
                filters.Add($"状态“{UiLabels.ForAliveStatus(status)}”");
            }

            return filters.Count == 0
                ? "当前导出范围：全部服务器。"
                : $"当前导出范围：{string.Join("；", filters)}。";
        }
    }

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
