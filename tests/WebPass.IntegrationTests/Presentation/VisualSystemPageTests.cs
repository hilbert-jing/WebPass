using WebPass.Web.Application.Authorization;
using Xunit;

namespace WebPass.IntegrationTests.Presentation;

public sealed class VisualSystemPageTests
{
    [Fact]
    public async Task Authenticated_shell_is_chinese_and_permission_scoped()
    {
        using var factory = new PresentationFactory();
        factory.InitializeUser(false, PermissionCode.AssetView, PermissionCode.ExportData);
        using var client = factory.CreateAuthenticatedClient();

        var html = await client.GetStringAsync("/servers");

        Assert.Contains("<html lang=\"zh-CN\"", html, StringComparison.Ordinal);
        Assert.Contains("资产作业", html, StringComparison.Ordinal);
        Assert.Contains("服务器资产", html, StringComparison.Ordinal);
        Assert.Contains("数据导出", html, StringComparison.Ordinal);
        Assert.DoesNotContain("用户与权限", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/css/site.css\"", html, StringComparison.Ordinal);
        Assert.Contains("src=\"/js/site.js\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stylesheet_contains_confirmed_tokens_and_reduced_motion()
    {
        using var factory = new PresentationFactory();
        using var client = factory.CreateClient();

        var css = await client.GetStringAsync("/css/site.css");

        Assert.Contains("--color-nav: #1c3147", css, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--color-accent: #2e75a8", css, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("prefers-reduced-motion: reduce", css, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Mobile_navigation_declares_and_uses_an_initial_focus_target()
    {
        using var factory = new PresentationFactory();
        factory.InitializeUser(false, PermissionCode.AssetView);
        using var client = factory.CreateAuthenticatedClient();

        var html = await client.GetStringAsync("/servers");
        var script = await client.GetStringAsync("/js/site.js");

        Assert.Contains("data-drawer-initial-focus", html, StringComparison.Ordinal);
        Assert.Contains(
            "drawer.querySelector(\"[data-drawer-initial-focus]\")",
            script,
            StringComparison.Ordinal);
        Assert.Contains("openDrawer(sidebar.id, button);", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tablet_shell_keeps_post_logout_control_visible()
    {
        using var factory = new PresentationFactory();
        factory.InitializeUser(false, PermissionCode.AssetView);
        using var client = factory.CreateAuthenticatedClient();

        var html = await client.GetStringAsync("/servers");
        var css = await client.GetStringAsync("/css/site.css");

        Assert.Contains("class=\"sidebar-logout\"", html, StringComparison.Ordinal);
        Assert.Contains("method=\"post\"", html, StringComparison.Ordinal);
        Assert.Contains("action=\"/Logout\"", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".sidebar-logout {", css, StringComparison.Ordinal);
        Assert.Contains("clip-path: none", css, StringComparison.Ordinal);
    }
}
