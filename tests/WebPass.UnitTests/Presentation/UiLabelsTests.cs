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
}
