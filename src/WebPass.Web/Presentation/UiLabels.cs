using WebPass.Web.Application.Authorization;
using WebPass.Web.Domain.Enums;

namespace WebPass.Web.Presentation;

public static class UiLabels
{
    public static string ForAliveStatus(AliveStatus? status) => status switch
    {
        AliveStatus.Alive => "存活",
        AliveStatus.Fault => "异常",
        AliveStatus.Decommissioned => "停用",
        _ => "未知",
    };

    public static string ForPermission(string permissionCode) => permissionCode switch
    {
        PermissionCode.AssetView => "查看服务器资产",
        PermissionCode.AssetCreate => "登记服务器",
        PermissionCode.AssetEdit => "编辑服务器",
        PermissionCode.AssetArchive => "归档服务器",
        PermissionCode.PingExecute => "运行 Ping",
        PermissionCode.StatusMarkAlive => "标记为存活",
        PermissionCode.ImportData => "导入服务器数据",
        PermissionCode.ExportData => "导出服务器数据",
        PermissionCode.SecretReveal => "查看服务器密码",
        PermissionCode.SubnetManage => "管理网段",
        PermissionCode.AuditView => "查看审计日志",
        _ => "未知权限",
    };

    public static string ForPingOutcome(string outcome) => outcome switch
    {
        "Success" => "可达",
        "Timeout" => "超时",
        "Unreachable" => "不可达",
        "PermissionDenied" => "权限不足",
        _ => "检测失败",
    };
}
