using System.Net;
using System.Text.RegularExpressions;
using WebPass.Web.Application.Authorization;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Domain.Enums;
using Xunit;

namespace WebPass.IntegrationTests.Presentation;

public sealed class VisualSystemPageTests
{
    [Theory]
    [InlineData("/login", "登录 WebPass")]
    [InlineData("/secrets/reauthenticate", "验证当前密码")]
    [InlineData("/error", "无法完成此请求")]
    public async Task Focused_pages_use_chinese_copy_without_business_navigation(
        string path,
        string heading)
    {
        using var factory = new PresentationFactory();
        factory.InitializeUser(false, PermissionCode.SecretReveal);
        using var client = factory.CreateAuthenticatedClient();

        var html = await client.GetStringAsync(path);

        Assert.Contains(heading, html, StringComparison.Ordinal);
        Assert.Contains("focused-shell", html, StringComparison.Ordinal);
        Assert.DoesNotContain("资产作业", html, StringComparison.Ordinal);
    }

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
    public async Task Server_inventory_renders_subnet_rail_and_drawer_contracts()
    {
        using var factory = new PresentationFactory();
        factory.InitializeUser(
            false,
            PermissionCode.AssetView,
            PermissionCode.AssetCreate,
            PermissionCode.PingExecute);
        var subnetId = Guid.NewGuid();
        factory.Seed(db =>
        {
            db.Subnets.Add(new Subnet
            {
                Id = subnetId,
                Name = "生产网段",
                Cidr = "10.0.0.0/24",
                NetworkAddress = "10.0.0.0",
                PrefixLength = 24,
                Location = "总部",
                IsEnabled = true,
            });
            db.ServerAssets.Add(new ServerAsset
            {
                SubnetId = subnetId,
                BusinessIp = "10.0.0.9",
                BusinessIpNumber = 167772169,
                Location = "总部",
                AliveStatus = AliveStatus.Alive,
                ComputerName = "web-09",
                SystemName = "WebPass",
            });
        });
        using var client = factory.CreateAuthenticatedClient();

        var html = await client.GetStringAsync(
            $"/servers?Query.SubnetId={subnetId}");

        Assert.Contains("服务器资产", html, StringComparison.Ordinal);
        Assert.DoesNotContain("请检查表单中标记的错误后重试。", html, StringComparison.Ordinal);
        Assert.Contains("data-ip-rail", html, StringComparison.Ordinal);
        Assert.Contains("10.0.0.0/24", html, StringComparison.Ordinal);
        Assert.Contains("1 / 254", html, StringComparison.Ordinal);
        Assert.Contains("<option value=", html, StringComparison.Ordinal);
        Assert.Contains("data-drawer=\"register-server\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-open=\"\"", html, StringComparison.Ordinal);
        Assert.Contains("data-submit-label=\"正在检测\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Server_drawer_uses_progressive_enhancement_for_accessibility_and_preopened_focus()
    {
        using var factory = new PresentationFactory();
        factory.InitializeUser(
            false,
            PermissionCode.AssetView,
            PermissionCode.AssetCreate);
        using var client = factory.CreateAuthenticatedClient();

        var html = await client.GetStringAsync(
            "/servers?Input.BusinessIp=10.0.0.1");
        var script = await client.GetStringAsync("/js/site.js");
        var drawerTag = Regex.Match(
            html,
            "<aside[^>]*data-drawer=\"register-server\"[^>]*>",
            RegexOptions.Singleline).Value;

        Assert.Contains("data-open", drawerTag, StringComparison.Ordinal);
        Assert.DoesNotContain("aria-hidden", drawerTag, StringComparison.Ordinal);
        Assert.Contains("aria-expanded=\"true\"", html, StringComparison.Ordinal);
        Assert.Contains("<noscript>", html, StringComparison.Ordinal);
        Assert.Contains(
            "document.querySelectorAll(\".drawer[data-drawer]\")",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "drawer.setAttribute(\"aria-hidden\", isOpen ? \"false\" : \"true\");",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "drawerOpeners.set(drawer, opener);",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Subnet_management_renders_drawer_preview_and_collapsed_edit_contracts()
    {
        using var factory = new PresentationFactory();
        factory.InitializeUser(false, PermissionCode.SubnetManage);
        factory.Seed(db => db.Subnets.Add(new Subnet
        {
            Name = "生产网段",
            Cidr = "10.0.0.0/24",
            NetworkAddress = "10.0.0.0",
            PrefixLength = 24,
            Location = "总部",
            IsEnabled = true,
            RowVersion = [1],
        }));
        using var client = factory.CreateAuthenticatedClient();

        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/subnets"));

        Assert.Contains("网段管理", html, StringComparison.Ordinal);
        Assert.Contains("添加网段", html, StringComparison.Ordinal);
        Assert.Contains("data-drawer=\"create-subnet\"", html, StringComparison.Ordinal);
        Assert.Contains("data-subnet-preview-form", html, StringComparison.Ordinal);
        Assert.Contains("data-subnet-preview-result", html, StringComparison.Ordinal);
        Assert.Contains("data-subnet-edit", html, StringComparison.Ordinal);
        Assert.Contains("<noscript>", html, StringComparison.Ordinal);
        Assert.Contains("formaction=\"?handler=Preview\"", html, StringComparison.Ordinal);
        Assert.Contains("生产网段", html, StringComparison.Ordinal);
        Assert.Contains("10.0.0.0/24", html, StringComparison.Ordinal);
        Assert.Contains("停用后，新服务器不能再登记到此网段。", html, StringComparison.Ordinal);
        Assert.Contains("删除“生产网段”（10.0.0.0/24）", html, StringComparison.Ordinal);
        Assert.Contains("/js/subnet-preview.js", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Subnet_preview_script_posts_urlencoded_fields_and_renders_server_errors_as_text()
    {
        using var factory = new PresentationFactory();
        using var client = factory.CreateClient();

        var script = await client.GetStringAsync("/js/subnet-preview.js");

        Assert.Contains("new URLSearchParams", script, StringComparison.Ordinal);
        Assert.Contains("?handler=Preview", script, StringComparison.Ordinal);
        Assert.Contains("credentials: \"same-origin\"", script, StringComparison.Ordinal);
        Assert.Contains("result.textContent", script, StringComparison.Ordinal);
        Assert.Contains("result.setAttribute(\"role\", \"alert\")", script, StringComparison.Ordinal);
        Assert.Contains("networkAddress", script, StringComparison.Ordinal);
        Assert.Contains("broadcastAddress", script, StringComparison.Ordinal);
        Assert.Contains("usableAddressCount", script, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Secret_reveal_asset_clears_sensitive_values_on_expiry_and_page_lifecycle()
    {
        using var factory = new PresentationFactory();
        using var client = factory.CreateClient();

        var script = await client.GetStringAsync("/js/secret-reveal.js");

        Assert.Contains("method: \"POST\"", script, StringComparison.Ordinal);
        Assert.Contains("credentials: \"same-origin\"", script, StringComparison.Ordinal);
        Assert.Contains("[data-secret-value]", script, StringComparison.Ordinal);
        Assert.Contains("[data-secret-countdown]", script, StringComparison.Ordinal);
        Assert.Contains("navigator.clipboard.writeText", script, StringComparison.Ordinal);
        Assert.Contains("new AbortController()", script, StringComparison.Ordinal);
        Assert.Contains("signal: controller.signal", script, StringComparison.Ordinal);
        Assert.Contains("visibilitychange", script, StringComparison.Ordinal);
        Assert.Contains("document.visibilityState === \"hidden\"", script, StringComparison.Ordinal);
        Assert.Contains(
            """
            if (document.visibilityState === "hidden") {
                        clearAll();
                    }
            """,
            script,
            StringComparison.Ordinal);
        Assert.Contains("window.addEventListener(\"pagehide\", clearAll)", script, StringComparison.Ordinal);
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
