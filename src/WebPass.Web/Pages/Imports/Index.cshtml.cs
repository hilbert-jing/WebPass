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
            ModelState.AddModelError(string.Empty, "请选择 CSV 或 XLSX 文件。");
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
            ModelState.AddModelError(
                string.Empty,
                "仅支持 CSV 和 XLSX 服务器清单文件。");
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
            ModelState.AddModelError(
                string.Empty,
                PreviewFailureMessage(exception));
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
                $"已新增 {result.CreatedCount} 项，更新 {result.UpdatedCount} 项，跳过 {result.SkippedCount} 项";
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
            ModelState.AddModelError(
                string.Empty,
                "导入预览已失效，请重新上传文件。");
            return Page();
        }
    }

    private Guid UserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public static string ImportErrorField(string field) => field switch
    {
        "File" => "文件",
        "BusinessIp" => "业务 IP",
        "AliveStatus" => "状态",
        "Row" => "该行",
        "Location" => "位置",
        "ComputerName" => "计算机名",
        "SystemName" => "系统名",
        "OperatingSystemVersion" => "操作系统版本",
        "DatabaseVersion" => "数据库版本",
        "Notes" => "备注",
        _ => "未识别字段",
    };

    public static string ImportErrorMessage(string message) => message switch
    {
        "The import exceeds 5,000 rows." =>
            "导入文件最多包含 5,000 行。",
        "A canonical IPv4 address is required." =>
            "必须是规范的 IPv4 地址。",
        "The address is outside enabled subnets." =>
            "地址不在已启用网段的可用范围内。",
        "The alive status is invalid." =>
            "服务器状态无效。",
        "A required value is missing or exceeds its limit." =>
            "必填内容缺失或字段长度超出限制。",
        "The business IP is duplicated in the import." =>
            "业务 IP 在导入文件中重复。",
        _ => "无法导入此行，请检查文件内容。",
    };

    private static string PreviewFailureMessage(Exception exception) =>
        exception is InvalidOperationException
        {
            Message: "The import file exceeds 10 MB.",
        }
            ? "导入文件不能超过 10 MB。"
            : "无法读取导入文件，请检查格式、大小和内容。";

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
