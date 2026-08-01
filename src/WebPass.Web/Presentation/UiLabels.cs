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

    public static string ForAuditAction(string? action) => action switch
    {
        "AssetCreate" => "登记服务器",
        "AssetEdit" => "编辑服务器",
        "AssetArchive" => "归档服务器",
        "AdministratorPasswordExport" => "导出服务器密码",
        "AssetExport" => "导出服务器资产",
        "ImportCommit" => "提交数据导入",
        "PingExecute" => "执行 Ping 检测",
        "StatusMarkAlive" => "标记服务器为存活",
        "DataKeyRotate" => "轮换数据加密密钥",
        "SecretReauthentication" => "验证当前密码",
        "SecretReveal" => "查看服务器密码",
        "SubnetCreate" => "创建网段",
        "SubnetEdit" => "编辑网段",
        "SubnetEnable" => "启用网段",
        "SubnetDisable" => "停用网段",
        "SubnetDelete" => "删除网段",
        "Login" => "登录",
        "Logout" => "退出登录",
        "UserCreate" => "创建用户",
        "UserPasswordReset" => "重置用户密码",
        "UserEnablement" => "更改用户状态",
        "UserPermissionsReplace" => "更新用户权限",
        _ => "未知操作",
    };

    public static string ForAuditObjectType(string? objectType) => objectType switch
    {
        "ServerAsset" => "服务器资产",
        "ImportJob" => "导入任务",
        "DataEncryptionKey" => "数据加密密钥",
        "User" => "用户",
        "Subnet" => "网段",
        _ => "未知对象",
    };

    public static string ForAuditResult(string? result) => result switch
    {
        "Success" => "成功",
        "Denied" => "已拒绝",
        "Failure" => "失败",
        "Timeout" => "超时",
        "Unreachable" => "不可达",
        "InternalError" => "系统处理失败",
        "NotFound" => "未找到",
        "InvalidCredentials" => "用户名或密码不正确",
        "Locked" => "账号已锁定",
        "Disabled" => "账号已停用",
        _ => "未知结果",
    };
}
