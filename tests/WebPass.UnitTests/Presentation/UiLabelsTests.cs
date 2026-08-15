using WebPass.Web.Application.Authorization;
using WebPass.Web.Domain.Enums;
using WebPass.Web.Presentation;
using Xunit;

namespace WebPass.UnitTests.Presentation;

public sealed class UiLabelsTests
{
    [Theory]
    [InlineData(AliveStatus.Unknown, "未知")]
    [InlineData(AliveStatus.Alive, "存活")]
    [InlineData(AliveStatus.Fault, "异常")]
    [InlineData(AliveStatus.Decommissioned, "停用")]
    public void Alive_status_has_a_stable_chinese_label(
        AliveStatus status,
        string expected) =>
        Assert.Equal(expected, UiLabels.ForAliveStatus(status));

    [Fact]
    public void Null_alive_status_has_the_unknown_label() =>
        Assert.Equal("未知", UiLabels.ForAliveStatus(null));

    [Theory]
    [InlineData(PermissionCode.AssetView, "查看服务器资产")]
    [InlineData(PermissionCode.AssetCreate, "登记服务器")]
    [InlineData(PermissionCode.AssetEdit, "编辑服务器")]
    [InlineData(PermissionCode.AssetArchive, "归档服务器")]
    [InlineData(PermissionCode.PingExecute, "运行 Ping")]
    [InlineData(PermissionCode.StatusMarkAlive, "标记为存活")]
    [InlineData(PermissionCode.ImportData, "导入服务器数据")]
    [InlineData(PermissionCode.ExportData, "导出服务器数据")]
    [InlineData(PermissionCode.SecretReveal, "查看服务器密码")]
    [InlineData(PermissionCode.SubnetManage, "管理网段")]
    [InlineData(PermissionCode.AuditView, "查看审计日志")]
    public void Permission_codes_are_not_exposed_as_primary_copy(
        string permissionCode,
        string expected) =>
        Assert.Equal(expected, UiLabels.ForPermission(permissionCode));

    [Fact]
    public void Unknown_permission_has_a_safe_fallback_label() =>
        Assert.Equal("未知权限", UiLabels.ForPermission("UnknownPermission"));

    [Theory]
    [InlineData("Success", "可达")]
    [InlineData("Timeout", "超时")]
    [InlineData("Unreachable", "不可达")]
    [InlineData("PermissionDenied", "权限不足")]
    public void Ping_outcome_has_a_stable_chinese_label(
        string outcome,
        string expected) =>
        Assert.Equal(expected, UiLabels.ForPingOutcome(outcome));

    [Fact]
    public void Unknown_ping_outcome_has_a_safe_fallback_label() =>
        Assert.Equal("检测失败", UiLabels.ForPingOutcome("UnknownOutcome"));

    [Theory]
    [InlineData("AssetCreate", "登记服务器")]
    [InlineData("AssetEdit", "编辑服务器")]
    [InlineData("PingUnregisteredAddress", "探测空闲 IP")]
    [InlineData("AssetArchive", "归档服务器")]
    [InlineData("AdministratorPasswordExport", "导出服务器密码")]
    [InlineData("AssetExport", "导出服务器资产")]
    [InlineData("ImportCommit", "提交数据导入")]
    [InlineData("PingExecute", "执行 Ping 检测")]
    [InlineData("StatusMarkAlive", "标记服务器为存活")]
    [InlineData("DataKeyRotate", "轮换数据加密密钥")]
    [InlineData("SecretReauthentication", "验证当前密码")]
    [InlineData("SecretReveal", "查看服务器密码")]
    [InlineData("SubnetCreate", "创建网段")]
    [InlineData("SubnetEdit", "编辑网段")]
    [InlineData("SubnetEnable", "启用网段")]
    [InlineData("SubnetDisable", "停用网段")]
    [InlineData("SubnetDelete", "删除网段")]
    [InlineData("Login", "登录")]
    [InlineData("Logout", "退出登录")]
    [InlineData("UserCreate", "创建用户")]
    [InlineData("UserPasswordReset", "重置用户密码")]
    [InlineData("UserEnablement", "更改用户状态")]
    [InlineData("UserPermissionsReplace", "更新用户权限")]
    public void Emitted_audit_actions_have_stable_chinese_labels(
        string action,
        string expected) =>
        Assert.Equal(expected, UiLabels.ForAuditAction(action));

    [Theory]
    [InlineData("ServerAsset", "服务器资产")]
    [InlineData("ImportJob", "导入任务")]
    [InlineData("DataEncryptionKey", "数据加密密钥")]
    [InlineData("User", "用户")]
    [InlineData("Subnet", "网段")]
    [InlineData("SubnetAddress", "子网地址")]
    public void Emitted_audit_object_types_have_stable_chinese_labels(
        string objectType,
        string expected) =>
        Assert.Equal(expected, UiLabels.ForAuditObjectType(objectType));

    [Theory]
    [InlineData("Success", "成功")]
    [InlineData("Denied", "已拒绝")]
    [InlineData("Failure", "失败")]
    [InlineData("Timeout", "超时")]
    [InlineData("Unreachable", "不可达")]
    [InlineData("InternalError", "系统处理失败")]
    [InlineData("NotFound", "未找到")]
    [InlineData("InvalidCredentials", "用户名或密码不正确")]
    [InlineData("Locked", "账号已锁定")]
    [InlineData("Disabled", "账号已停用")]
    public void Emitted_audit_results_have_stable_chinese_labels(
        string result,
        string expected) =>
        Assert.Equal(expected, UiLabels.ForAuditResult(result));

    [Theory]
    [InlineData(null)]
    [InlineData("AttackerControlledAction")]
    public void Unknown_audit_action_has_a_safe_fixed_fallback(string? action) =>
        Assert.Equal("未知操作", UiLabels.ForAuditAction(action));

    [Theory]
    [InlineData(null)]
    [InlineData("AttackerControlledObject")]
    public void Unknown_audit_object_type_has_a_safe_fixed_fallback(string? objectType) =>
        Assert.Equal("未知对象", UiLabels.ForAuditObjectType(objectType));

    [Theory]
    [InlineData(null)]
    [InlineData("AttackerControlledResult")]
    public void Unknown_audit_result_has_a_safe_fixed_fallback(string? result) =>
        Assert.Equal("未知结果", UiLabels.ForAuditResult(result));
}
