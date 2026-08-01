using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using WebPass.Web.Application.Authorization;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Domain.Enums;
using Xunit;

namespace WebPass.IntegrationTests.Presentation;

public sealed class VisualSystemPageTests
{
    [Fact]
    public async Task Login_submit_control_exposes_idle_and_busy_labels()
    {
        using var factory = new PresentationFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/login");
        var submitButton = Regex.Match(
            html,
            "<button[^>]*type=\"submit\"[^>]*>.*?</button>",
            RegexOptions.Singleline).Value;

        Assert.Contains(
            "data-submit-label=\"正在登录\"",
            submitButton,
            StringComparison.Ordinal);
        Assert.Equal(
            "登录",
            Regex.Replace(
                Regex.Match(
                    submitButton,
                    ">(?<label>.*?)</button>",
                    RegexOptions.Singleline).Groups["label"].Value,
                "\\s+",
                " ").Trim());
    }

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
    public async Task Error_page_copy_control_targets_only_the_correlation_id_and_uses_shared_live_feedback()
    {
        using var factory = new PresentationFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/error");
        var correlationId = Regex.Match(
            html,
            "<code[^>]*id=\"error-correlation-id\"[^>]*>(?<value>[^<]+)</code>")
            .Groups["value"]
            .Value;

        Assert.NotEmpty(correlationId);
        Assert.Contains("data-copy", html, StringComparison.Ordinal);
        Assert.Contains(
            "data-copy-target=\"#error-correlation-id\"",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "data-copy-status-target=\"#error-correlation-status\"",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "aria-describedby=\"error-correlation-status\"",
            html,
            StringComparison.Ordinal);
        Assert.Contains("aria-label=\"复制关联编号\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"error-correlation-status\"", html, StringComparison.Ordinal);
        Assert.Contains("role=\"status\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-live=\"polite\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "data-copy-target=\".auth-panel\"",
            html,
            StringComparison.Ordinal);
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
    public async Task Enhancement_boot_is_a_blocking_local_head_script_before_stylesheet()
    {
        using var factory = new PresentationFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/login");
        using var bootResponse = await client.GetAsync("/js/enhance-boot.js");
        var bootTag = Regex.Match(
            html,
            "<script[^>]*src=\"/js/enhance-boot\\.js\"[^>]*></script>",
            RegexOptions.IgnoreCase).Value;
        var stylesheetPosition = html.IndexOf(
            "<link rel=\"stylesheet\" href=\"/css/site.css\"",
            StringComparison.Ordinal);

        Assert.True(bootResponse.IsSuccessStatusCode);
        Assert.NotEmpty(bootTag);
        Assert.DoesNotContain(" defer", bootTag, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" async", bootTag, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(
            html.IndexOf(bootTag, StringComparison.Ordinal),
            0,
            stylesheetPosition - 1);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("throws-after-hiding")]
    public async Task Enhancement_boot_restores_the_static_baseline_when_site_initialization_does_not_finish(
        string scenario)
    {
        using var factory = new PresentationFactory();
        using var client = factory.CreateClient();
        var boot = await client.GetStringAsync("/js/enhance-boot.js");
        var site = await client.GetStringAsync("/js/site.js");
        var harness =
            """
            import assert from "node:assert/strict";
            import vm from "node:vm";

            const bootSource = Buffer
                .from(process.env.WEBPASS_BOOT_SCRIPT_BASE64, "base64")
                .toString("utf8");
            const siteSource = Buffer
                .from(process.env.WEBPASS_SITE_SCRIPT_BASE64, "base64")
                .toString("utf8");
            const scenario = process.env.WEBPASS_ENHANCEMENT_FAILURE;
            const windowListeners = new Map();

            class Element {
                constructor(id = "") {
                    this.id = id;
                    this.dataset = {};
                    this.attributes = new Map();
                }
                addEventListener(type) {
                    if (scenario === "throws-after-hiding" &&
                        this === toggle &&
                        type === "click") {
                        throw new Error("simulated site initialization failure");
                    }
                }
                closest() {
                    return null;
                }
                contains() {
                    return false;
                }
                focus() {
                    document.activeElement = this;
                }
                getAttribute(name) {
                    return this.attributes.get(name) ?? null;
                }
                hasAttribute(name) {
                    return this.attributes.has(name);
                }
                querySelector() {
                    return null;
                }
                removeAttribute(name) {
                    this.attributes.delete(name);
                }
                setAttribute(name, value) {
                    this.attributes.set(name, String(value));
                }
            }

            const html = new Element("html");
            const sidebar = new Element("app-sidebar");
            sidebar.setAttribute("data-drawer", "");
            const createDrawer = new Element("register-server");
            createDrawer.setAttribute("data-drawer", "");
            const toggle = new Element();
            toggle.setAttribute("data-nav-toggle", "");
            toggle.setAttribute("aria-controls", sidebar.id);
            toggle.setAttribute("aria-expanded", "false");
            const createOpener = new Element();
            createOpener.dataset.drawerOpen = createDrawer.id;
            const byId = new Map([
                [sidebar.id, sidebar],
                [createDrawer.id, createDrawer],
            ]);
            const document = {
                activeElement: null,
                documentElement: html,
                addEventListener() {},
                getElementById(id) {
                    return byId.get(id) ?? null;
                },
                querySelector() {
                    return null;
                },
                querySelectorAll(selector) {
                    if (selector === "[data-drawer]") {
                        return [sidebar, createDrawer];
                    }
                    if (selector === "[data-drawer-open], [data-nav-toggle]") {
                        return [toggle, createOpener];
                    }
                    if (selector === "[data-nav-toggle]") return [toggle];
                    return [];
                },
            };
            const window = {
                addEventListener(type, listener) {
                    windowListeners.set(type, listener);
                },
                matchMedia() {
                    return {
                        matches: true,
                        addEventListener() {},
                    };
                },
                setTimeout() {},
            };
            const sandbox = {
                console,
                DataTransfer: class {},
                document,
                Element,
                HTMLButtonElement: class extends Element {},
                HTMLInputElement: class extends Element {},
                HTMLSelectElement: class extends Element {},
                navigator: {},
                window,
            };
            vm.createContext(sandbox);
            vm.runInContext(bootSource, sandbox);

            assert.equal(html.hasAttribute("data-js-loading"), true);
            assert.equal(html.hasAttribute("data-js-enabled"), false);
            assert.equal(windowListeners.has("load"), true);

            if (scenario === "throws-after-hiding") {
                assert.throws(
                    () => vm.runInContext(siteSource, sandbox),
                    /simulated site initialization failure/);
                assert.equal(sidebar.getAttribute("aria-hidden"), "true");
                assert.equal(createDrawer.getAttribute("aria-hidden"), "true");
            }

            windowListeners.get("load")();

            assert.equal(html.hasAttribute("data-js-loading"), false);
            assert.equal(html.hasAttribute("data-js-enabled"), false);
            assert.equal(sidebar.hasAttribute("aria-hidden"), false);
            assert.equal(createDrawer.hasAttribute("aria-hidden"), false);
            assert.equal(toggle.getAttribute("aria-expanded"), "false");
            process.stdout.write(scenario + "-static-baseline");
            """;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("node")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            },
        };
        process.StartInfo.ArgumentList.Add("--input-type=module");
        process.StartInfo.ArgumentList.Add("--eval");
        process.StartInfo.ArgumentList.Add(harness);
        process.StartInfo.Environment["WEBPASS_BOOT_SCRIPT_BASE64"] =
            Convert.ToBase64String(Encoding.UTF8.GetBytes(boot));
        process.StartInfo.Environment["WEBPASS_SITE_SCRIPT_BASE64"] =
            Convert.ToBase64String(Encoding.UTF8.GetBytes(site));
        process.StartInfo.Environment["WEBPASS_ENHANCEMENT_FAILURE"] = scenario;

        Assert.True(process.Start());
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await standardOutput;
        var error = await standardError;

        Assert.True(
            process.ExitCode == 0,
            $"Node enhancement failure test failed:{Environment.NewLine}{error}");
        Assert.Equal($"{scenario}-static-baseline", output);
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
    public async Task Server_drawer_response_preserves_preopened_accessibility_state()
    {
        using var factory = new PresentationFactory();
        factory.InitializeUser(
            false,
            PermissionCode.AssetView,
            PermissionCode.AssetCreate);
        using var client = factory.CreateAuthenticatedClient();

        var html = await client.GetStringAsync(
            "/servers?Input.BusinessIp=10.0.0.1");
        var drawerTag = Regex.Match(
            html,
            "<aside[^>]*data-drawer=\"register-server\"[^>]*>",
            RegexOptions.Singleline).Value;

        Assert.Contains("data-open", drawerTag, StringComparison.Ordinal);
        Assert.DoesNotContain("aria-hidden", drawerTag, StringComparison.Ordinal);
        Assert.Contains("aria-expanded=\"true\"", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/servers", "register-server", "Input.BusinessIp")]
    [InlineData("/subnets", "create-subnet", "Input.Name")]
    [InlineData("/admin/users", "create-user", "username")]
    public async Task Create_forms_render_a_no_javascript_submission_baseline(
        string path,
        string drawerId,
        string fieldName)
    {
        using var factory = new PresentationFactory();
        factory.InitializeUser(true);
        using var client = factory.CreateAuthenticatedClient();

        var html = await client.GetStringAsync(path);
        var openerTag = Regex.Match(
            html,
            $"<button[^>]*data-drawer-open=\"{Regex.Escape(drawerId)}\"[^>]*>",
            RegexOptions.Singleline).Value;
        var drawer = Regex.Match(
            html,
            $"<aside[^>]*id=\"{Regex.Escape(drawerId)}\"[^>]*>.*?</aside>",
            RegexOptions.Singleline).Value;
        var drawerTag = Regex.Match(
            drawer,
            "<aside[^>]*>",
            RegexOptions.Singleline).Value;
        var closeTag = Regex.Match(
            drawer,
            "<button[^>]*data-drawer-close[^>]*>",
            RegexOptions.Singleline).Value;

        Assert.DoesNotMatch("<noscript>\\s*<style", html);
        Assert.DoesNotContain("hidden", drawerTag, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("data-js-only", openerTag, StringComparison.Ordinal);
        Assert.Contains("data-js-only", closeTag, StringComparison.Ordinal);
        Assert.Contains($"name=\"{fieldName}\"", drawer, StringComparison.Ordinal);
        Assert.Matches(
            "<form[^>]*method=\"post\"[^>]*action=\"[^\"]*handler=Create[^\"]*\"",
            drawer);
        Assert.Contains(
            "name=\"__RequestVerificationToken\"",
            drawer,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stylesheet_keeps_drawers_visible_until_external_script_enhances_page()
    {
        using var factory = new PresentationFactory();
        using var client = factory.CreateClient();

        var css = await client.GetStringAsync("/css/site.css");
        var baseline = Regex.Match(
            css,
            "(?ms)^\\.drawer\\s*\\{(?<rules>.*?)^\\}").Groups["rules"].Value;
        var enhanced = Regex.Match(
            css,
            "(?ms)^html\\[data-js-enabled\\] \\.drawer\\s*\\{(?<rules>.*?)^\\}")
            .Groups["rules"].Value;

        Assert.Contains("position: static", baseline, StringComparison.Ordinal);
        Assert.DoesNotContain("visibility: hidden", baseline, StringComparison.Ordinal);
        Assert.DoesNotContain("translateX", baseline, StringComparison.Ordinal);
        Assert.Contains("position: fixed", enhanced, StringComparison.Ordinal);
        Assert.Contains("visibility: hidden", enhanced, StringComparison.Ordinal);
        Assert.Contains("[data-js-only]", css, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Loading_styles_hide_only_closed_surfaces_without_exposing_dead_controls()
    {
        using var factory = new PresentationFactory();
        using var client = factory.CreateClient();

        var css = await client.GetStringAsync("/css/site.css");
        var loadingDrawer = Regex.Match(
            css,
            "(?ms)^html\\[data-js-loading\\] \\.drawer:not\\(\\[data-open\\]\\)\\s*\\{(?<rules>.*?)^\\}")
            .Groups["rules"].Value;
        var mobileStart = css.IndexOf(
            "@media (max-width: 767px)",
            StringComparison.Ordinal);
        var reducedMotionStart = css.IndexOf(
            "@media (prefers-reduced-motion: reduce)",
            mobileStart,
            StringComparison.Ordinal);
        Assert.InRange(mobileStart, 0, css.Length - 1);
        Assert.InRange(reducedMotionStart, mobileStart + 1, css.Length - 1);
        var mobileCss = css[mobileStart..reducedMotionStart];
        var loadingSidebar = Regex.Match(
            mobileCss,
            "(?ms)^\\s*html\\[data-js-loading\\] \\.app-sidebar:not\\(\\[data-open\\]\\)\\s*\\{(?<rules>.*?)^\\s*\\}")
            .Groups["rules"].Value;

        Assert.Contains("visibility: hidden", loadingDrawer, StringComparison.Ordinal);
        Assert.DoesNotContain("position:", loadingDrawer, StringComparison.Ordinal);
        Assert.DoesNotContain("transform:", loadingDrawer, StringComparison.Ordinal);
        Assert.Contains("visibility: hidden", loadingSidebar, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "html[data-js-loading] .nav-toggle",
            css,
            StringComparison.Ordinal);
        Assert.Contains(
            "html[data-js-enabled] .nav-toggle",
            mobileCss,
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
    public async Task Governance_pages_render_chinese_read_only_and_permission_management_contracts()
    {
        using var factory = new PresentationFactory();
        factory.InitializeUser(true, PermissionCode.AuditView);
        factory.Seed(db =>
        {
            db.AuditLogs.Add(new AuditLog
            {
                ActorUserId = factory.UserId,
                Action = "UserPermissionsReplace",
                ObjectType = "User",
                ObjectId = "operator",
                Result = "Success",
                CorrelationId = "governance-42",
            });
            db.AuditLogs.Add(new AuditLog
            {
                ActorUserId = factory.UserId,
                Action = "UnexpectedAction42",
                ObjectType = "UnexpectedObject42",
                Result = "UnexpectedResult42",
            });
            var userId = Guid.NewGuid();
            db.Users.Add(new AppUser
            {
                Id = userId,
                Username = "operator",
                PasswordHash = "opaque-password-hash",
            });
            db.UserPermissions.Add(new UserPermission
            {
                UserId = userId,
                PermissionCode = PermissionCode.SecretReveal,
            });
        });
        using var client = factory.CreateAuthenticatedClient();

        var auditHtml = WebUtility.HtmlDecode(
            await client.GetStringAsync("/audit"));
        var usersHtml = WebUtility.HtmlDecode(
            await client.GetStringAsync("/admin/users"));

        Assert.Contains("审计日志", auditHtml, StringComparison.Ordinal);
        Assert.Contains("只读记录", auditHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("筛选审计", auditHtml, StringComparison.Ordinal);
        Assert.Contains("data-copy", auditHtml, StringComparison.Ordinal);
        Assert.Contains("data-copy-target", auditHtml, StringComparison.Ordinal);
        Assert.Contains("data-copy-status-target", auditHtml, StringComparison.Ordinal);
        Assert.Contains("aria-describedby=", auditHtml, StringComparison.Ordinal);
        Assert.Contains("role=\"status\"", auditHtml, StringComparison.Ordinal);
        Assert.Contains("aria-live=\"polite\"", auditHtml, StringComparison.Ordinal);
        Assert.Contains(
            "aria-label=\"复制关联编号\"",
            auditHtml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "aria-label=\"复制关联编号 governance-42\"",
            auditHtml,
            StringComparison.Ordinal);
        Assert.Contains("更新用户权限", auditHtml, StringComparison.Ordinal);
        Assert.Contains("用户", auditHtml, StringComparison.Ordinal);
        Assert.Contains("成功", auditHtml, StringComparison.Ordinal);
        Assert.Contains("未知操作", auditHtml, StringComparison.Ordinal);
        Assert.Contains("未知对象", auditHtml, StringComparison.Ordinal);
        Assert.Contains("未知结果", auditHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("UserPermissionsReplace", auditHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("UnexpectedAction42", auditHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("UnexpectedObject42", auditHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("UnexpectedResult42", auditHtml, StringComparison.Ordinal);

        Assert.Contains("用户与权限", usersHtml, StringComparison.Ordinal);
        Assert.Contains("创建普通用户", usersHtml, StringComparison.Ordinal);
        Assert.Contains("查看服务器密码", usersHtml, StringComparison.Ordinal);
        Assert.Contains(
            $"value=\"{PermissionCode.SecretReveal}\"",
            usersHtml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            PermissionCode.SecretReveal + "</label>",
            usersHtml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "opaque-password-hash",
            usersHtml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", usersHtml, StringComparison.Ordinal);

        var administratorRow = Regex.Match(
            usersHtml,
            "<tr>(?:(?!</tr>).)*presentation-user(?:(?!</tr>).)*</tr>",
            RegexOptions.Singleline).Value;
        Assert.Contains(
            "管理员拥有全部权限",
            administratorRow,
            StringComparison.Ordinal);
        Assert.DoesNotContain("<form", administratorRow, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "type=\"checkbox\"",
            administratorRow,
            StringComparison.Ordinal);
        Assert.DoesNotContain("重置密码", administratorRow, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Administrator_user_validation_is_csp_safe_and_non_sensitive()
    {
        using var factory = new PresentationFactory();
        factory.InitializeUser(true);
        using var client = factory.CreateAuthenticatedClient();

        using var getResponse = await client.GetAsync("/admin/users");
        var getHtml = await getResponse.Content.ReadAsStringAsync();
        var antiforgeryToken = WebUtility.HtmlDecode(Regex.Match(
            getHtml,
            "<input[^>]*name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"",
            RegexOptions.IgnoreCase).Groups["token"].Value);

        getResponse.EnsureSuccessStatusCode();
        Assert.False(string.IsNullOrWhiteSpace(antiforgeryToken));
        Assert.Contains(
            "default-src 'self'",
            getResponse.Headers.GetValues("Content-Security-Policy").Single(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(" style=", getHtml, StringComparison.OrdinalIgnoreCase);

        var invalidUsername = $"sensitive-{new string('x', 129)}";
        using var postResponse = await client.PostAsync(
            "/admin/users?handler=Create",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = invalidUsername,
                ["__RequestVerificationToken"] = antiforgeryToken,
            }));
        var invalidHtml = await postResponse.Content.ReadAsStringAsync();

        postResponse.EnsureSuccessStatusCode();
        Assert.Contains("role=\"alert\"", invalidHtml, StringComparison.Ordinal);
        Assert.Contains(
            "请检查表单中标记的错误后重试。",
            invalidHtml,
            StringComparison.Ordinal);
        var usernameField = WebUtility.HtmlDecode(Regex.Match(
            invalidHtml,
            "<div>\\s*<label for=\"username\".*?</div>",
            RegexOptions.Singleline).Value);
        Assert.Contains("aria-invalid=\"true\"", usernameField, StringComparison.Ordinal);
        Assert.Contains(
            "aria-describedby=\"username-error\"",
            usernameField,
            StringComparison.Ordinal);
        Assert.Contains(
            "id=\"username-error\"",
            usernameField,
            StringComparison.Ordinal);
        Assert.Contains(
            "用户名不能超过 128 个字符。",
            usernameField,
            StringComparison.Ordinal);
        Assert.DoesNotContain("aria-live", usernameField, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("role=\"status\"", usernameField, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" style=", invalidHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(invalidUsername, invalidHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", invalidHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Username must contain",
            invalidHtml,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Shared_copy_control_executes_accessible_feedback_without_exfiltration()
    {
        using var factory = new PresentationFactory();
        using var client = factory.CreateClient();

        var script = await client.GetStringAsync("/js/site.js");

        var harness =
            """
            import assert from "node:assert/strict";
            import vm from "node:vm";

            const source = Buffer
                .from(process.env.WEBPASS_SITE_SCRIPT_BASE64, "base64")
                .toString("utf8");
            const copiedText = "governance-copy-42";

            async function exercise({ targetPresent, clipboardFails }) {
                let clickHandler;
                const scheduled = [];
                const clipboardWrites = [];
                const externalCalls = [];

                class Element {
                    closest(selector) {
                        return selector === "[data-copy]" ? this : null;
                    }
                }

                const button = new Element();
                button.dataset = {
                    copyTarget: "#copy-value",
                    copyStatusTarget: "#copy-status",
                };
                button.textContent = "复制";
                const target = targetPresent
                    ? { textContent: `  ${copiedText}  ` }
                    : null;
                const status = { textContent: "" };
                const storage = {
                    getItem(key) {
                        externalCalls.push(["storage.getItem", key]);
                        return null;
                    },
                    setItem(key, value) {
                        externalCalls.push(["storage.setItem", key, value]);
                    },
                };
                const trackedConsole = Object.fromEntries(
                    ["debug", "error", "info", "log", "warn"].map(method => [
                        method,
                        (...args) => externalCalls.push([`console.${method}`, ...args]),
                    ]));
                const rootAttributes = new Set(["data-js-loading"]);
                const document = {
                    documentElement: {
                        hasAttribute(name) {
                            return rootAttributes.has(name);
                        },
                        removeAttribute(name) {
                            rootAttributes.delete(name);
                        },
                        setAttribute(name) {
                            rootAttributes.add(name);
                        },
                    },
                    addEventListener(type, handler) {
                        if (type === "click") clickHandler = handler;
                    },
                    getElementById() {
                        return null;
                    },
                    querySelector(selector) {
                        if (selector === "#copy-value") return target;
                        if (selector === "#copy-status") return status;
                        return null;
                    },
                    querySelectorAll() {
                        return [];
                    },
                };
                const sandbox = {
                    console: trackedConsole,
                    DataTransfer: class {},
                    document,
                    Element,
                    fetch: (...args) => externalCalls.push(["fetch", ...args]),
                    HTMLButtonElement: class extends Element {},
                    HTMLInputElement: class extends Element {},
                    HTMLSelectElement: class extends Element {},
                    localStorage: storage,
                    navigator: {
                        clipboard: {
                            writeText(value) {
                                clipboardWrites.push(value);
                                return clipboardFails
                                    ? Promise.reject(new Error("denied"))
                                    : Promise.resolve();
                            },
                        },
                        sendBeacon: (...args) => externalCalls.push(["sendBeacon", ...args]),
                    },
                    sessionStorage: storage,
                    WebSocket: class {
                        constructor(...args) {
                            externalCalls.push(["WebSocket", ...args]);
                        }
                    },
                    window: {
                        setTimeout(callback, delay) {
                            scheduled.push({ callback, delay });
                        },
                    },
                    XMLHttpRequest: class {
                        constructor() {
                            externalCalls.push(["XMLHttpRequest"]);
                        }
                    },
                };

                vm.createContext(sandbox);
                vm.runInContext(source, sandbox);
                assert.equal(typeof clickHandler, "function");
                assert.equal(rootAttributes.has("data-js-loading"), false);
                assert.equal(rootAttributes.has("data-js-enabled"), true);

                clickHandler({ target: button });
                await Promise.resolve();
                await Promise.resolve();

                return {
                    button,
                    clipboardWrites,
                    externalCalls,
                    scheduled,
                    status,
                };
            }

            const success = await exercise({
                targetPresent: true,
                clipboardFails: false,
            });
            assert.deepEqual(success.clipboardWrites, [copiedText]);
            assert.equal(success.status.textContent, "已复制");
            assert.equal(success.button.textContent, "复制");
            assert.equal(success.scheduled.length, 1);
            assert.equal(success.scheduled[0].delay, 1800);
            assert.ok(!success.status.textContent.includes(copiedText));
            success.scheduled[0].callback();
            assert.equal(success.status.textContent, "");
            assert.deepEqual(success.externalCalls, []);

            const failure = await exercise({
                targetPresent: true,
                clipboardFails: true,
            });
            assert.deepEqual(failure.clipboardWrites, [copiedText]);
            assert.equal(failure.status.textContent, "复制失败，请手动选择");
            assert.equal(failure.button.textContent, "复制");
            assert.equal(failure.scheduled.length, 1);
            assert.equal(failure.scheduled[0].delay, 1800);
            assert.ok(!failure.status.textContent.includes(copiedText));
            failure.scheduled[0].callback();
            assert.equal(failure.status.textContent, "");
            assert.deepEqual(failure.externalCalls, []);

            const missingTarget = await exercise({
                targetPresent: false,
                clipboardFails: false,
            });
            assert.deepEqual(missingTarget.clipboardWrites, []);
            assert.equal(
                missingTarget.status.textContent,
                "复制失败，请手动选择");
            assert.equal(missingTarget.button.textContent, "复制");
            assert.equal(missingTarget.scheduled.length, 1);
            assert.equal(missingTarget.scheduled[0].delay, 1800);
            missingTarget.scheduled[0].callback();
            assert.equal(missingTarget.status.textContent, "");
            assert.deepEqual(missingTarget.externalCalls, []);

            process.stdout.write("copy-behavior-ok");
            """;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("node")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            },
        };
        process.StartInfo.ArgumentList.Add("--input-type=module");
        process.StartInfo.ArgumentList.Add("--eval");
        process.StartInfo.ArgumentList.Add(harness);
        process.StartInfo.Environment["WEBPASS_SITE_SCRIPT_BASE64"] =
            Convert.ToBase64String(Encoding.UTF8.GetBytes(script));

        Assert.True(process.Start());
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await standardOutput;
        var error = await standardError;

        Assert.True(
            process.ExitCode == 0,
            $"Node copy behavior test failed:{Environment.NewLine}{error}");
        Assert.Equal("copy-behavior-ok", output);
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
    public async Task Data_transfer_script_progressively_enhances_upload_and_export_controls()
    {
        using var factory = new PresentationFactory();
        using var client = factory.CreateClient();

        var script = await client.GetStringAsync("/js/site.js");

        Assert.Contains("[data-upload-zone]", script, StringComparison.Ordinal);
        Assert.Contains("[data-upload-input]", script, StringComparison.Ordinal);
        Assert.Contains("dragenter", script, StringComparison.Ordinal);
        Assert.Contains("dragleave", script, StringComparison.Ordinal);
        Assert.Contains("drop", script, StringComparison.Ordinal);
        Assert.Contains("files.length !== 1", script, StringComparison.Ordinal);
        Assert.Contains("new DataTransfer()", script, StringComparison.Ordinal);
        Assert.Contains("[data-export-format]", script, StringComparison.Ordinal);
        Assert.Contains("[data-export-submit]", script, StringComparison.Ordinal);
        Assert.Contains("下载 CSV", script, StringComparison.Ordinal);
        Assert.Contains("下载 XLSX", script, StringComparison.Ordinal);
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

    [Fact]
    public async Task Short_tablet_navigation_keeps_a_scrollable_rail_and_reveals_existing_labels_within_it()
    {
        using var factory = new PresentationFactory();
        factory.InitializeUser(
            false,
            PermissionCode.AssetView,
            PermissionCode.ExportData);
        using var client = factory.CreateAuthenticatedClient();

        var html = await client.GetStringAsync("/servers");
        var css = await client.GetStringAsync("/css/site.css");
        var tabletStart = css.IndexOf(
            "@media (min-width: 768px) and (max-width: 1279px)",
            StringComparison.Ordinal);
        var mobileStart = css.IndexOf(
            "@media (max-width: 767px)",
            tabletStart,
            StringComparison.Ordinal);
        Assert.InRange(tabletStart, 0, css.Length - 1);
        Assert.InRange(mobileStart, tabletStart + 1, css.Length - 1);
        var tabletCss = css[tabletStart..mobileStart];
        var serversLink = Regex.Match(
            html,
            "<a[^>]*href=\"/servers\"[^>]*aria-current[^>]*>.*?</a>",
            RegexOptions.Singleline).Value;

        Assert.Contains(
            "aria-current=\"page\"",
            serversLink,
            StringComparison.Ordinal);
        Assert.Contains(
            "<span class=\"nav-label\">服务器资产</span>",
            serversLink,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "class=\"nav-label\" aria-hidden",
            serversLink,
            StringComparison.Ordinal);
        Assert.DoesNotContain("<button", serversLink, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            ".app-sidebar:hover,",
            tabletCss,
            StringComparison.Ordinal);
        Assert.Contains(
            ".app-sidebar:focus-within",
            tabletCss,
            StringComparison.Ordinal);
        Assert.Contains(
            ".app-sidebar:hover .nav-label,",
            tabletCss,
            StringComparison.Ordinal);
        Assert.Contains(
            ".app-sidebar:focus-within .nav-label",
            tabletCss,
            StringComparison.Ordinal);
        Assert.Contains("width: 72px", tabletCss, StringComparison.Ordinal);
        Assert.Contains("width: 232px", tabletCss, StringComparison.Ordinal);
        Assert.Contains("overflow-x: hidden", tabletCss, StringComparison.Ordinal);
        Assert.Contains("overflow-y: auto", tabletCss, StringComparison.Ordinal);
        Assert.DoesNotContain(
            ".app-sidebar {\n        overflow: visible;",
            tabletCss,
            StringComparison.Ordinal);
        Assert.Contains("class=\"sidebar-logout\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pages_expose_skip_link_main_landmark_and_explicit_labels()
    {
        using var factory = new PresentationFactory();
        factory.InitializeUser(true);
        using var client = factory.CreateAuthenticatedClient();

        var html = await client.GetStringAsync("/servers");

        Assert.Contains("href=\"#main-content\"", html, StringComparison.Ordinal);
        Assert.Contains("<main id=\"main-content\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-current=\"page\"", html, StringComparison.Ordinal);
        var placeholderInputs = Regex.Matches(
            html,
            "<input(?=[^>]*placeholder=)(?<attributes>[^>]*)>",
            RegexOptions.IgnoreCase);
        foreach (Match input in placeholderInputs)
        {
            var id = Regex.Match(
                input.Groups["attributes"].Value,
                "id=\"(?<id>[^\"]+)\"",
                RegexOptions.IgnoreCase).Groups["id"].Value;
            Assert.False(
                string.IsNullOrWhiteSpace(id),
                "Inputs with placeholder text must expose an id for an explicit label.");
            Assert.Contains(
                $"<label for=\"{id}\"",
                html,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Static_assets_do_not_reference_remote_resources()
    {
        using var factory = new PresentationFactory();
        using var client = factory.CreateClient();

        var assets = string.Join('\n', new[]
        {
            await client.GetStringAsync("/css/site.css"),
            await client.GetStringAsync("/js/site.js"),
            await client.GetStringAsync("/js/secret-reveal.js"),
            await client.GetStringAsync("/js/subnet-preview.js"),
        });

        Assert.DoesNotContain(
            "https://",
            assets,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "http://",
            assets,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Local_svg_favicon_is_linked_and_self_contained()
    {
        using var factory = new PresentationFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/login");
        using var response = await client.GetAsync("/favicon.svg");
        var favicon = await response.Content.ReadAsStringAsync();

        Assert.Contains(
            "<link rel=\"icon\" type=\"image/svg+xml\" href=\"/favicon.svg\"",
            html,
            StringComparison.Ordinal);
        response.EnsureSuccessStatusCode();
        Assert.Equal(
            "image/svg+xml",
            response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<svg", favicon, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", favicon, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(
            "(?:href|src)\\s*=\\s*[\"']https?://",
            favicon);
    }

    [Fact]
    public async Task Stylesheet_defines_desktop_tablet_and_mobile_layout_contracts()
    {
        using var factory = new PresentationFactory();
        using var client = factory.CreateClient();

        var css = await client.GetStringAsync("/css/site.css");

        Assert.Contains(
            "@media (min-width: 1280px)",
            css,
            StringComparison.Ordinal);
        Assert.Contains(
            "grid-template-columns: 232px minmax(0, 1fr)",
            css,
            StringComparison.Ordinal);
        Assert.Contains(
            "@media (min-width: 768px) and (max-width: 1279px)",
            css,
            StringComparison.Ordinal);
        Assert.Contains(
            "width: min(88vw, 320px)",
            css,
            StringComparison.Ordinal);
        Assert.Contains(
            ".form-grid",
            css,
            StringComparison.Ordinal);
        Assert.Contains(
            ".data-table-wrap",
            css,
            StringComparison.Ordinal);
        Assert.Contains(
            "max-height: 100dvh",
            css,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mobile_styles_keep_no_script_navigation_visible_and_scope_off_canvas_controls_to_enhanced_pages()
    {
        using var factory = new PresentationFactory();
        factory.InitializeUser(
            false,
            PermissionCode.AssetView,
            PermissionCode.SubnetManage,
            PermissionCode.ImportData,
            PermissionCode.ExportData,
            PermissionCode.AuditView);
        using var client = factory.CreateAuthenticatedClient();

        var html = await client.GetStringAsync("/servers");
        var css = await client.GetStringAsync("/css/site.css");
        var mobileStart = css.IndexOf(
            "@media (max-width: 767px)",
            StringComparison.Ordinal);
        var reducedMotionStart = css.IndexOf(
            "@media (prefers-reduced-motion: reduce)",
            mobileStart,
            StringComparison.Ordinal);
        Assert.InRange(mobileStart, 0, css.Length - 1);
        Assert.InRange(reducedMotionStart, mobileStart + 1, css.Length - 1);
        var mobileCss = css[mobileStart..reducedMotionStart];
        var sidebarTag = Regex.Match(
            html,
            "<aside[^>]*id=\"app-sidebar\"[^>]*>",
            RegexOptions.Singleline).Value;
        var baselineSidebar = Regex.Match(
            mobileCss,
            "(?ms)^\\s*\\.app-sidebar\\s*\\{(?<rules>.*?)^\\s*\\}")
            .Groups["rules"].Value;
        var enhancedSidebar = Regex.Match(
            mobileCss,
            "(?ms)^\\s*html\\[data-js-enabled\\] \\.app-sidebar\\s*\\{(?<rules>.*?)^\\s*\\}")
            .Groups["rules"].Value;
        var enhancedToggle = Regex.Match(
            mobileCss,
            "(?ms)^\\s*html\\[data-js-enabled\\] \\.sidebar-close,\\s*^\\s*html\\[data-js-enabled\\] \\.nav-toggle\\s*\\{(?<rules>.*?)^\\s*\\}")
            .Groups["rules"].Value;

        Assert.DoesNotContain("aria-hidden", sidebarTag, StringComparison.OrdinalIgnoreCase);
        var navigation = Regex.Match(
            html,
            "<nav[^>]*class=\"primary-nav\"[^>]*>.*?</nav>",
            RegexOptions.Singleline).Value;
        Assert.True(Regex.Matches(navigation, "<a\\s").Count >= 5);
        Assert.DoesNotContain("visibility: hidden", baselineSidebar, StringComparison.Ordinal);
        Assert.DoesNotContain("translateX", baselineSidebar, StringComparison.Ordinal);
        Assert.Contains("visibility: hidden", enhancedSidebar, StringComparison.Ordinal);
        Assert.Contains("translateX", enhancedSidebar, StringComparison.Ordinal);
        Assert.Contains("display: inline-flex", enhancedToggle, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("enhancement-marker")]
    [InlineData("preopened-focus")]
    [InlineData("details-escape")]
    public async Task Site_script_executes_progressive_drawer_and_menu_behaviors(
        string scenario)
    {
        using var factory = new PresentationFactory();
        using var client = factory.CreateClient();

        var script = await client.GetStringAsync("/js/site.js");
        using var bootResponse = await client.GetAsync("/js/enhance-boot.js");
        var bootScript = bootResponse.IsSuccessStatusCode
            ? await bootResponse.Content.ReadAsStringAsync()
            : string.Empty;
        var harness =
            """
            import assert from "node:assert/strict";
            import vm from "node:vm";

            const bootSource = Buffer
                .from(process.env.WEBPASS_BOOT_SCRIPT_BASE64, "base64")
                .toString("utf8");
            const source = Buffer
                .from(process.env.WEBPASS_SITE_SCRIPT_BASE64, "base64")
                .toString("utf8");
            const scenario = process.env.WEBPASS_SITE_SCENARIO;
            const documentListeners = new Map();
            const elementsById = new Map();
            const drawers = [];
            const openers = [];
            let drawer = null;

            class Element {
                constructor(tagName = "div", id = "") {
                    this.tagName = tagName.toUpperCase();
                    this.id = id;
                    this.dataset = {};
                    this.attributes = new Map();
                    this.children = [];
                    this.listeners = new Map();
                    this.parentElement = null;
                    this.initialFocus = null;
                    this.primaryNavigationLink = null;
                    if (id) elementsById.set(id, this);
                }
                add(child) {
                    child.parentElement = this;
                    this.children.push(child);
                    return child;
                }
                addEventListener(type, handler) {
                    this.listeners.set(type, handler);
                }
                closest(selector) {
                    for (let current = this; current; current = current.parentElement) {
                        if (selector === "details[open]" &&
                            current.tagName === "DETAILS" &&
                            current.hasAttribute("open")) return current;
                        if (selector === "[data-drawer]" &&
                            current.hasAttribute("data-drawer")) return current;
                    }
                    return null;
                }
                contains(element) {
                    return this === element ||
                        this.children.some(child => child.contains(element));
                }
                focus() {
                    document.activeElement = this;
                }
                getAttribute(name) {
                    return this.attributes.get(name) ?? null;
                }
                hasAttribute(name) {
                    return this.attributes.has(name);
                }
                querySelector(selector) {
                    if (selector === "[data-drawer-initial-focus]") {
                        return this.initialFocus;
                    }
                    if (selector === ".primary-nav a") {
                        return this.primaryNavigationLink;
                    }
                    if (selector === "summary") {
                        return this.children.find(child =>
                            child.tagName === "SUMMARY") ?? null;
                    }
                    return null;
                }
                removeAttribute(name) {
                    this.attributes.delete(name);
                }
                setAttribute(name, value) {
                    if (scenario === "enhancement-marker" &&
                        this === drawer &&
                        name === "aria-hidden") {
                        assert.equal(
                            html.hasAttribute("data-js-loading"),
                            true,
                            "The head boot script must mark loading before site initialization");
                        assert.equal(
                            html.hasAttribute("data-js-enabled"),
                            false,
                            "Interactive enhancement is not committed until initialization finishes");
                    }
                    this.attributes.set(name, String(value));
                }
            }

            const html = new Element("html");
            const body = html.add(new Element("body"));
            const document = {
                activeElement: null,
                body,
                documentElement: html,
                addEventListener(type, handler) {
                    documentListeners.set(type, handler);
                },
                getElementById(id) {
                    return elementsById.get(id) ?? null;
                },
                querySelector() {
                    return null;
                },
                querySelectorAll(selector) {
                    if (selector === "[data-drawer]") return drawers;
                    if (selector === "[data-drawer-open], [data-nav-toggle]") {
                        return openers;
                    }
                    if (selector === "[data-nav-toggle]") return [];
                    if (selector === "[data-drawer][data-open]") {
                        return drawers.filter(item => item.hasAttribute("data-open"));
                    }
                    return [];
                },
            };

            if (scenario === "enhancement-marker" ||
                scenario === "preopened-focus") {
                drawer = body.add(new Element("aside", "register-server"));
                drawer.setAttribute("data-drawer", "register-server");
                drawers.push(drawer);
                const opener = body.add(new Element("button"));
                opener.dataset.drawerOpen = drawer.id;
                opener.setAttribute("data-drawer-open", drawer.id);
                opener.setAttribute("aria-controls", drawer.id);
                openers.push(opener);

                if (scenario === "preopened-focus") {
                    drawer.setAttribute("data-open", "");
                    drawer.initialFocus = drawer.add(new Element("input"));
                }
            }

            let details = null;
            let summary = null;
            let action = null;
            if (scenario === "details-escape") {
                details = body.add(new Element("details"));
                details.setAttribute("open", "");
                summary = details.add(new Element("summary"));
                action = details.add(new Element("button"));
            }

            const sandbox = {
                console,
                DataTransfer: class {},
                document,
                Element,
                HTMLButtonElement: class extends Element {},
                HTMLInputElement: class extends Element {},
                HTMLSelectElement: class extends Element {},
                navigator: {},
                window: {
                    addEventListener() {},
                    matchMedia() {
                        return {
                            matches: false,
                            addEventListener() {},
                        };
                    },
                    setTimeout() {},
                },
            };

            vm.createContext(sandbox);
            vm.runInContext(bootSource, sandbox);
            vm.runInContext(source, sandbox);

            if (scenario === "enhancement-marker") {
                assert.equal(html.hasAttribute("data-js-enabled"), true);
                assert.equal(html.hasAttribute("data-js-loading"), false);
                assert.equal(drawer.getAttribute("aria-hidden"), "true");
            }

            if (scenario === "preopened-focus") {
                assert.equal(drawer.getAttribute("aria-hidden"), "false");
                assert.equal(
                    document.activeElement,
                    drawer.initialFocus,
                    "A preopened drawer must focus its first actionable field");
            }

            if (scenario === "details-escape") {
                action.focus();
                let prevented = false;
                documentListeners.get("keydown")({
                    key: "Escape",
                    target: action,
                    preventDefault() {
                        prevented = true;
                    },
                });
                assert.equal(details.hasAttribute("open"), false);
                assert.equal(document.activeElement, summary);
                assert.equal(prevented, true);
            }

            process.stdout.write(`${scenario}-ok`);
            """;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("node")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            },
        };
        process.StartInfo.ArgumentList.Add("--input-type=module");
        process.StartInfo.ArgumentList.Add("--eval");
        process.StartInfo.ArgumentList.Add(harness);
        process.StartInfo.Environment["WEBPASS_BOOT_SCRIPT_BASE64"] =
            Convert.ToBase64String(Encoding.UTF8.GetBytes(bootScript));
        process.StartInfo.Environment["WEBPASS_SITE_SCRIPT_BASE64"] =
            Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        process.StartInfo.Environment["WEBPASS_SITE_SCENARIO"] = scenario;

        Assert.True(process.Start());
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await standardOutput;
        var error = await standardError;

        Assert.True(
            process.ExitCode == 0,
            $"Node {scenario} behavior test failed:{Environment.NewLine}{error}");
        Assert.Equal($"{scenario}-ok", output);
    }

    [Fact]
    public async Task Mobile_navigation_updates_visibility_and_restores_keyboard_focus()
    {
        using var factory = new PresentationFactory();
        using var client = factory.CreateClient();

        var script = await client.GetStringAsync("/js/site.js");
        var harness =
            """
            import assert from "node:assert/strict";
            import vm from "node:vm";

            const source = Buffer
                .from(process.env.WEBPASS_SITE_SCRIPT_BASE64, "base64")
                .toString("utf8");
            const mediaListeners = [];

            class Element {
                constructor(id = "") {
                    this.id = id;
                    this.dataset = {};
                    this.attributes = new Map();
                    this.listeners = new Map();
                    this.focused = false;
                }
                addEventListener(type, handler) {
                    this.listeners.set(type, handler);
                }
                closest() {
                    return null;
                }
                contains(element) {
                    return this === element ||
                        (this === sidebar &&
                            (element === closeButton || element === navLink));
                }
                focus() {
                    closeButton.focused = false;
                    navLink.focused = false;
                    toggle.focused = false;
                    this.focused = true;
                    document.activeElement = this;
                }
                getAttribute(name) {
                    return this.attributes.get(name) ?? null;
                }
                hasAttribute(name) {
                    return this.attributes.has(name);
                }
                querySelector(selector) {
                    if (selector === "[data-drawer-initial-focus]") {
                        return closeButton;
                    }
                    if (selector === ".primary-nav a") return navLink;
                    return null;
                }
                removeAttribute(name) {
                    this.attributes.delete(name);
                }
                setAttribute(name, value) {
                    if (this === sidebar &&
                        name === "aria-hidden" &&
                        String(value) === "true") {
                        assert.equal(
                            sidebar.contains(document.activeElement),
                            false,
                            "Focus must leave the sidebar before it is hidden");
                        assert.equal(
                            document.activeElement,
                            toggle,
                            "The visible navigation toggle must own focus before collapse");
                    }
                    this.attributes.set(name, String(value));
                }
            }

            const html = new Element("html");
            html.attributes.set("data-js-loading", "");
            const sidebar = new Element("app-sidebar");
            const closeButton = new Element();
            const navLink = new Element();
            const toggle = new Element();
            toggle.setAttribute("data-nav-toggle", "");
            toggle.setAttribute("aria-controls", sidebar.id);
            toggle.setAttribute("aria-expanded", "false");
            const documentListeners = new Map();
            const media = {
                matches: true,
                addEventListener(type, handler) {
                    if (type === "change") mediaListeners.push(handler);
                },
            };
            const document = {
                activeElement: null,
                documentElement: html,
                addEventListener(type, handler) {
                    documentListeners.set(type, handler);
                },
                getElementById(id) {
                    return id === sidebar.id ? sidebar : null;
                },
                querySelector() {
                    return null;
                },
                querySelectorAll(selector) {
                    if (selector === ".drawer[data-drawer]" ||
                        selector === "[data-drawer]") return [sidebar];
                    if (selector === "[data-drawer-open], [data-nav-toggle]") {
                        return [toggle];
                    }
                    if (selector === "[data-nav-toggle]") return [toggle];
                    if (selector === "[data-drawer][data-open]") {
                        return sidebar.hasAttribute("data-open") ? [sidebar] : [];
                    }
                    return [];
                },
            };
            const sandbox = {
                console,
                DataTransfer: class {},
                document,
                Element,
                HTMLButtonElement: class extends Element {},
                HTMLInputElement: class extends Element {},
                HTMLSelectElement: class extends Element {},
                navigator: {},
                window: {
                    matchMedia(query) {
                        assert.equal(query, "(max-width: 767px)");
                        return media;
                    },
                    setTimeout() {},
                },
            };

            navLink.focus();
            vm.createContext(sandbox);
            vm.runInContext(source, sandbox);

            assert.equal(html.hasAttribute("data-js-loading"), false);
            assert.equal(html.hasAttribute("data-js-enabled"), true);
            assert.equal(sidebar.getAttribute("aria-hidden"), "true");
            assert.equal(toggle.getAttribute("aria-expanded"), "false");
            assert.equal(toggle.focused, true);
            assert.equal(mediaListeners.length, 1);

            media.matches = false;
            mediaListeners[0]({ matches: false });
            assert.equal(sidebar.hasAttribute("aria-hidden"), false);

            navLink.focus();
            media.matches = true;
            mediaListeners[0]({ matches: true });
            assert.equal(sidebar.getAttribute("aria-hidden"), "true");
            assert.equal(toggle.getAttribute("aria-expanded"), "false");
            assert.equal(toggle.focused, true);

            toggle.listeners.get("click")();
            assert.equal(sidebar.hasAttribute("data-open"), true);
            assert.equal(sidebar.getAttribute("aria-hidden"), "false");
            assert.equal(toggle.getAttribute("aria-expanded"), "true");
            assert.equal(closeButton.focused, true);

            documentListeners.get("keydown")({ key: "Escape" });
            assert.equal(sidebar.hasAttribute("data-open"), false);
            assert.equal(sidebar.getAttribute("aria-hidden"), "true");
            assert.equal(toggle.getAttribute("aria-expanded"), "false");
            assert.equal(toggle.focused, true);

            toggle.listeners.get("click")();
            assert.equal(closeButton.focused, true);
            media.matches = false;
            mediaListeners[0]({ matches: false });
            assert.equal(sidebar.hasAttribute("aria-hidden"), false);
            assert.equal(sidebar.hasAttribute("data-open"), false);
            assert.equal(toggle.getAttribute("aria-expanded"), "false");
            assert.equal(navLink.focused, true);

            process.stdout.write("mobile-navigation-ok");
            """;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("node")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            },
        };
        process.StartInfo.ArgumentList.Add("--input-type=module");
        process.StartInfo.ArgumentList.Add("--eval");
        process.StartInfo.ArgumentList.Add(harness);
        process.StartInfo.Environment["WEBPASS_SITE_SCRIPT_BASE64"] =
            Convert.ToBase64String(Encoding.UTF8.GetBytes(script));

        Assert.True(process.Start());
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await standardOutput;
        var error = await standardError;

        Assert.True(
            process.ExitCode == 0,
            $"Node mobile navigation behavior test failed:{Environment.NewLine}{error}");
        Assert.Equal("mobile-navigation-ok", output);
    }
}
