# WebPass 全站视觉系统实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在不改变 WebPass 后端安全与业务流程的前提下，将全部 Razor Pages 改造成已确认的简体中文“静稳运维”现代控制台。

**Architecture:** 继续使用 ASP.NET Core Razor Pages 服务端渲染，以一个全局布局、一个设计令牌样式表和少量职责单一的原生 JavaScript 模块构成展示层。页面模型只增加渲染所需的只读选项、摘要和安全状态消息；认证、授权、CSRF、限流、加密、审计和并发控制仍由现有服务端代码裁决。

**Tech Stack:** .NET 10、ASP.NET Core 10 Razor Pages、Entity Framework Core 10、原生 CSS、原生 JavaScript、xUnit、`Microsoft.AspNetCore.Mvc.Testing`

## Global Constraints

- 界面语言固定为简体中文；IP、Ping、CIDR、CSV、XLSX 等技术名词保留英文。
- 主题固定为深海蓝导航 `#1C3147` + 雾蓝工作区 `#F4F7FA`；核心令牌还包括主文本 `#182B3A`、轨迹蓝 `#2E75A8`、存活绿 `#27856A`、告警琥珀 `#C8862B`。
- 中文字体使用 `Microsoft YaHei UI`，拉丁标题使用 `Bahnschrift`，IP/CIDR/延迟/关联编号使用 `Cascadia Mono` 或 `Consolas`；不得依赖外部字体服务。
- 不增加暗色主题、前端框架、CDN、公共 API、自动 Ping、批量操作、实时推送或后端尚不支持的筛选能力。
- 保持现有路由、handler、字段绑定、服务端权限、CSRF、限流、加密、审计和并发行为。
- 动效只解释状态：120 ms 按压/焦点、180 ms 菜单/字段反馈、240 ms 抽屉/状态切换；`prefers-reduced-motion` 下取消位移和循环。
- 所有状态除颜色外必须有文字；所有交互必须支持键盘和可见焦点。
- `/health`、`/logout`、`/servers/{id}/ping`、`/secrets/reveal` 保持处理端点，不创建伪业务页面。
- 当前工作区中的 `README.md`、`WebPass.rar` 和 `artifacts/` 属于用户现有改动；每个提交只暂存本任务列出的文件。

---

## File Structure

### 新建文件

- `src/WebPass.Web/Presentation/UiLabels.cs`：状态、权限和 Ping 结果的简体中文展示名称。
- `src/WebPass.Web/wwwroot/css/site.css`：设计令牌、全局壳、页面模板、响应式与减少动态效果规则。
- `src/WebPass.Web/wwwroot/js/site.js`：导航抽屉、渐进增强抽屉、确认面板、提交忙碌状态和复制操作。
- `src/WebPass.Web/wwwroot/js/subnet-preview.js`：调用现有 Preview handler，并渲染 CIDR 预览。
- `tests/WebPass.UnitTests/Presentation/UiLabelsTests.cs`：展示标签的确定性单元测试。
- `tests/WebPass.IntegrationTests/Presentation/PresentationFactory.cs`：可复用的内存数据库和测试认证工厂。
- `tests/WebPass.IntegrationTests/Presentation/VisualSystemPageTests.cs`：全局壳、逐页中文结构、权限可见性和静态资源测试。

### 修改文件

- `src/WebPass.Web/Pages/Shared/_Layout.cshtml`：全局应用壳与专注布局变体。
- `src/WebPass.Web/Pages/_ViewImports.cshtml`：导入展示标签命名空间。
- `src/WebPass.Web/Pages/Login.cshtml`
- `src/WebPass.Web/Pages/Secrets/Reauthenticate.cshtml`
- `src/WebPass.Web/Pages/Error.cshtml`
- `src/WebPass.Web/Pages/Servers/Index.cshtml`
- `src/WebPass.Web/Pages/Servers/Index.cshtml.cs`
- `src/WebPass.Web/Pages/Servers/Edit.cshtml`
- `src/WebPass.Web/Pages/Subnets/Index.cshtml`
- `src/WebPass.Web/Pages/Imports/Index.cshtml`
- `src/WebPass.Web/Pages/Imports/Index.cshtml.cs`
- `src/WebPass.Web/Pages/Exports/Index.cshtml`
- `src/WebPass.Web/Pages/Admin/PasswordExport.cshtml`
- `src/WebPass.Web/Pages/Audit/Index.cshtml`
- `src/WebPass.Web/Pages/Admin/Users.cshtml`
- `src/WebPass.Web/Pages/Admin/Users.cshtml.cs`
- `src/WebPass.Web/wwwroot/js/secret-reveal.js`
- 对应现有集成测试：只更新因中文文案和新语义结构而变化的断言，不降低安全断言。

---

### Task 1: 建立展示标签层和页面测试夹具

**Files:**
- Create: `src/WebPass.Web/Presentation/UiLabels.cs`
- Create: `tests/WebPass.UnitTests/Presentation/UiLabelsTests.cs`
- Create: `tests/WebPass.IntegrationTests/Presentation/PresentationFactory.cs`
- Modify: `src/WebPass.Web/Pages/_ViewImports.cshtml`

**Interfaces:**
- Produces: `UiLabels.ForAliveStatus(AliveStatus? status) -> string`
- Produces: `UiLabels.ForPermission(string permissionCode) -> string`
- Produces: `UiLabels.ForPingOutcome(string outcome) -> string`
- Produces: `PresentationFactory.InitializeUser(bool isAdministrator = false, params string[] permissions)`
- Produces: `PresentationFactory.Seed(Action<WebPassDbContext> seed)`
- Produces: `PresentationFactory.CreateAuthenticatedClient() -> HttpClient`

- [ ] **Step 1: 写展示标签失败测试**

```csharp
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
public void Permission_codes_are_not_exposed_as_primary_copy()
{
    Assert.Equal("查看服务器密码", UiLabels.ForPermission(PermissionCode.SecretReveal));
    Assert.Equal("管理网段", UiLabels.ForPermission(PermissionCode.SubnetManage));
}
```

- [ ] **Step 2: 运行测试并确认因类型不存在而失败**

Run: `dotnet test tests/WebPass.UnitTests/WebPass.UnitTests.csproj -c Release --filter FullyQualifiedName~UiLabelsTests`

Expected: FAIL，错误包含 `UiLabels could not be found`。

- [ ] **Step 3: 实现确定性中文标签映射**

```csharp
public static class UiLabels
{
    public static string ForAliveStatus(AliveStatus? status) => status switch
    {
        AliveStatus.Alive => "存活",
        AliveStatus.Fault => "异常",
        AliveStatus.Decommissioned => "停用",
        _ => "未知",
    };

    public static string ForPermission(string code) => code switch
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
```

- [ ] **Step 4: 实现可复用展示测试工厂**

`PresentationFactory` 使用 `UseInMemoryDatabase`、`TestHeaderAuthenticationHandler` 和唯一数据库名。`InitializeUser` 创建 `AppUser` 及 `UserPermission`；`Seed` 在独立 scope 中写入额外数据；`CreateAuthenticatedClient` 设置 `X-Test-User-Id` 并禁用自动跳转。

```csharp
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WebPass.IntegrationTests.Authorization;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;

public sealed class PresentationFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = Guid.NewGuid().ToString("N");
    public Guid UserId { get; } = Guid.NewGuid();

    public void InitializeUser(
        bool isAdministrator = false,
        params string[] permissions)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WebPassDbContext>();
        db.Users.Add(new AppUser
        {
            Id = UserId,
            Username = "presentation-user",
            PasswordHash = "unused",
            IsAdministrator = isAdministrator,
        });
        db.UserPermissions.AddRange(permissions.Select(code =>
            new UserPermission { UserId = UserId, PermissionCode = code }));
        db.SaveChanges();
    }

    public void Seed(Action<WebPassDbContext> seed)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WebPassDbContext>();
        seed(db);
        db.SaveChanges();
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            TestHeaderAuthenticationHandler.UserIdHeader,
            UserId.ToString());
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<WebPassDbContext>>();
            services.RemoveAll<WebPassDbContext>();
            services.RemoveAll<IDbContextOptionsConfiguration<WebPassDbContext>>();
            services.AddDbContext<WebPassDbContext>(options =>
                options.UseInMemoryDatabase(_databaseName));
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = TestHeaderAuthenticationHandler.Scheme;
                options.DefaultChallengeScheme = TestHeaderAuthenticationHandler.Scheme;
            }).AddScheme<AuthenticationSchemeOptions, TestHeaderAuthenticationHandler>(
                TestHeaderAuthenticationHandler.Scheme,
                _ => { });
        });
}
```

- [ ] **Step 5: 在 Razor 视图中导入展示层**

在 `_ViewImports.cshtml` 增加：

```razor
@using WebPass.Web.Presentation
```

- [ ] **Step 6: 运行单元测试**

Run: `dotnet test tests/WebPass.UnitTests/WebPass.UnitTests.csproj -c Release --filter FullyQualifiedName~UiLabelsTests`

Expected: PASS，0 failed。

- [ ] **Step 7: 提交任务**

```powershell
git add src/WebPass.Web/Presentation/UiLabels.cs src/WebPass.Web/Pages/_ViewImports.cshtml tests/WebPass.UnitTests/Presentation/UiLabelsTests.cs tests/WebPass.IntegrationTests/Presentation/PresentationFactory.cs
git commit -m "test: add visual presentation helpers"
```

---

### Task 2: 构建全局应用壳、设计令牌和渐进增强脚本

**Files:**
- Create: `src/WebPass.Web/wwwroot/css/site.css`
- Create: `src/WebPass.Web/wwwroot/js/site.js`
- Create: `tests/WebPass.IntegrationTests/Presentation/VisualSystemPageTests.cs`
- Modify: `src/WebPass.Web/Pages/Shared/_Layout.cshtml`

**Interfaces:**
- Consumes: `PresentationFactory`
- Produces: `ViewData["LayoutVariant"] = "Focused"` 布局契约
- Produces: `[data-nav-toggle]`, `[data-drawer]`, `[data-drawer-open]`, `[data-drawer-close]`, `[data-copy]`, `[data-submit-label]` DOM 契约

- [ ] **Step 1: 写全局壳和静态资源失败测试**

```csharp
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
```

- [ ] **Step 2: 运行测试并确认 404/旧英文壳失败**

Run: `dotnet test tests/WebPass.IntegrationTests/WebPass.IntegrationTests.csproj -c Release --filter FullyQualifiedName~VisualSystemPageTests`

Expected: FAIL，CSS 404 或缺少 `zh-CN`、中文导航。

- [ ] **Step 3: 在 `site.css` 建立令牌和基础控件**

```css
:root {
  --color-nav: #1c3147;
  --color-canvas: #f4f7fa;
  --color-ink: #182b3a;
  --color-accent: #2e75a8;
  --color-success: #27856a;
  --color-warning: #c8862b;
  --color-danger: #973e43;
  --color-line: #d9e3eb;
  --radius-control: 7px;
  --radius-surface: 10px;
  --motion-fast: 120ms;
  --motion-standard: 180ms;
  --motion-panel: 240ms;
}

body {
  margin: 0;
  background: var(--color-canvas);
  color: var(--color-ink);
  font-family: "Microsoft YaHei UI", "Segoe UI", sans-serif;
}

.data-value { font-family: "Cascadia Mono", Consolas, monospace; }
:focus-visible { outline: 3px solid var(--color-accent); outline-offset: 2px; }
@media (prefers-reduced-motion: reduce) {
  *, *::before, *::after { scroll-behavior: auto !important; transition-duration: 0.01ms !important; animation-duration: 0.01ms !important; animation-iteration-count: 1 !important; }
}
```

同一文件继续实现 `.app-shell`、`.app-sidebar`、`.topbar`、`.page-header`、`.command-bar`、`.data-table`、`.form-section`、`.drawer`、`.confirm-panel`、`.status-badge`、`.toast-region`、`.focused-shell` 和断点规则；所有颜色必须从上述令牌或其透明派生值获得。

- [ ] **Step 4: 重写 `_Layout.cshtml`**

布局必须：

- 设置 `<html lang="zh-CN">`。
- 在 `<head>` 引用 `/css/site.css`，在 `</body>` 前以 `defer` 引用 `/js/site.js`。
- 提供“跳到主要内容”链接和 `<main id="main-content">`。
- 当 `ViewData["LayoutVariant"]` 为 `Focused` 或用户未登录时渲染 `.focused-shell`，不显示业务侧栏。
- 登录用户按现有权限变量渲染三组中文导航，并为当前路径设置 `aria-current="page"`。
- 导航图标使用本地内联 SVG，统一 `18px` 和 `1.5px` 描边，不使用表情符号、图标字体或远程图标。
- 退出仍使用 POST 表单。
- 在主要内容开头渲染统一状态区：`TempData["StatusMessage"]` 使用 `role="status"`，ModelState 阻断错误使用 `role="alert"`；敏感值不得进入该区域。

- [ ] **Step 5: 实现 `site.js` 的渐进增强行为**

```javascript
(() => {
    const focusable = "a[href],button:not([disabled]),input:not([disabled]),select:not([disabled]),textarea:not([disabled]),[tabindex]:not([tabindex='-1'])";

    document.querySelectorAll("[data-nav-toggle]").forEach(button => {
        button.addEventListener("click", () => {
            const sidebar = document.getElementById(button.getAttribute("aria-controls"));
            const open = sidebar?.toggleAttribute("data-open");
            button.setAttribute("aria-expanded", String(Boolean(open)));
        });
    });

    document.addEventListener("click", event => {
        const opener = event.target.closest("[data-drawer-open]");
        const closer = event.target.closest("[data-drawer-close]");
        const copy = event.target.closest("[data-copy]");
        if (opener) openDrawer(opener.dataset.drawerOpen, opener);
        if (closer) closeDrawer(closer.closest("[data-drawer]"));
        if (copy) copyText(copy);
    });

    document.addEventListener("submit", event => {
        const button = event.submitter;
        if (!button?.dataset.submitLabel) return;
        button.disabled = true;
        button.textContent = button.dataset.submitLabel;
    });
})();
```

在同一 IIFE 内完整定义 `openDrawer(id, opener)`、`closeDrawer(drawer)`、焦点返回、`Escape` 关闭和 `copyText(button)`；不得将权限或业务校验放入脚本。

- [ ] **Step 6: 运行展示测试和现有导航权限测试**

Run: `dotnet test tests/WebPass.IntegrationTests/WebPass.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~VisualSystemPageTests|FullyQualifiedName~Navigation_shows_only_links"`

Expected: PASS，0 failed。

- [ ] **Step 7: 提交任务**

```powershell
git add src/WebPass.Web/Pages/Shared/_Layout.cshtml src/WebPass.Web/wwwroot/css/site.css src/WebPass.Web/wwwroot/js/site.js tests/WebPass.IntegrationTests/Presentation/VisualSystemPageTests.cs
git commit -m "feat: add WebPass visual application shell"
```

---

### Task 3: 迁移登录、二次验证和错误页到专注布局

**Files:**
- Modify: `src/WebPass.Web/Pages/Login.cshtml`
- Modify: `src/WebPass.Web/Pages/Login.cshtml.cs`
- Modify: `src/WebPass.Web/Pages/Logout.cshtml.cs`
- Modify: `src/WebPass.Web/Pages/Secrets/Reauthenticate.cshtml`
- Modify: `src/WebPass.Web/Pages/Secrets/Reauthenticate.cshtml.cs`
- Modify: `src/WebPass.Web/Pages/Error.cshtml`
- Modify: `tests/WebPass.IntegrationTests/Presentation/VisualSystemPageTests.cs`
- Modify: `tests/WebPass.IntegrationTests/Secrets/RevealTests.cs`

**Interfaces:**
- Consumes: `ViewData["LayoutVariant"] = "Focused"`
- Produces: `.focused-shell`, `.auth-panel`, `.auth-brand`, `.correlation-id` 页面结构

- [ ] **Step 1: 写专注页面失败测试**

```csharp
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
```

- [ ] **Step 2: 运行失败测试**

Run: `dotnet test tests/WebPass.IntegrationTests/WebPass.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~Focused_pages|FullyQualifiedName~RevealTests"`

Expected: FAIL，页面仍是英文且没有 `focused-shell`。

- [ ] **Step 3: 重写三个页面的语义结构和文案**

每页设置：

```razor
@{
    ViewData["LayoutVariant"] = "Focused";
}
```

登录页使用“登录 WebPass”“用户名”“密码”“正在登录”；二次验证页使用“验证当前密码”“验证通过后，可在当前会话中查看服务器密码 5 分钟”；错误页使用“无法完成此请求”、关联编号和返回服务器资产链接。所有输入保留现有 `asp-for`、`autocomplete`、验证摘要与防伪行为。

- [ ] **Step 4: 本地化认证错误和退出反馈**

- `LoginInput.Username` 使用 `[Required(ErrorMessage = "请输入用户名。")]`。
- `LoginInput.Password` 使用 `[Required(ErrorMessage = "请输入密码。")]`。
- 登录失败统一显示“用户名或密码不正确。”，不区分用户不存在、密码错误或锁定。
- 二次验证密码必填与验证失败使用中文，但保持相同限流、审计和 ReturnUrl 校验。
- `LogoutModel.OnPostAsync` 在成功审计并退出后设置 `TempData["StatusMessage"] = "已安全退出。"`，登录页在表单上方渲染该消息。

- [ ] **Step 5: 更新 Reveal 和会话测试中的中文页面断言**

保留 GET 不消耗预算、POST 限流、ReturnUrl 和 no-store 断言，只把页面文案断言更新为中文；不得删除任何安全测试。

- [ ] **Step 6: 运行相关测试**

Run: `dotnet test tests/WebPass.IntegrationTests/WebPass.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~VisualSystemPageTests|FullyQualifiedName~RevealTests|FullyQualifiedName~AuthenticationSessionTests"`

Expected: PASS，0 failed。

- [ ] **Step 7: 提交任务**

```powershell
git add src/WebPass.Web/Pages/Login.cshtml src/WebPass.Web/Pages/Login.cshtml.cs src/WebPass.Web/Pages/Logout.cshtml.cs src/WebPass.Web/Pages/Secrets/Reauthenticate.cshtml src/WebPass.Web/Pages/Secrets/Reauthenticate.cshtml.cs src/WebPass.Web/Pages/Error.cshtml tests/WebPass.IntegrationTests/Presentation/VisualSystemPageTests.cs tests/WebPass.IntegrationTests/Secrets/RevealTests.cs tests/WebPass.IntegrationTests/Security/AuthenticationSessionTests.cs
git commit -m "feat: redesign focused security pages"
```

---

### Task 4: 迁移服务器资产页和 IP 地址脉络

**Files:**
- Modify: `src/WebPass.Web/Pages/Servers/Index.cshtml`
- Modify: `src/WebPass.Web/Pages/Servers/Index.cshtml.cs`
- Modify: `tests/WebPass.IntegrationTests/Assets/AssetAndPingTests.cs`
- Modify: `tests/WebPass.IntegrationTests/Presentation/VisualSystemPageTests.cs`

**Interfaces:**
- Produces: `IReadOnlyList<SubnetFilterOption> SubnetOptions`
- Produces: `SelectedSubnetSummary? SelectedSubnet`
- Produces: `SubnetFilterOption(Guid Id, string Name, string Cidr)`
- Produces: `SelectedSubnetSummary(Guid Id, string Name, string Cidr, long RegisteredCount, long UsableAddressCount)`
- Consumes: `UiLabels.ForAliveStatus`
- Consumes: `site.js` drawer and submit-state DOM contracts

- [ ] **Step 1: 写服务器页面结构失败测试**

新增测试数据包含一个 `/24` 网段和一台资产，然后断言：

```csharp
Assert.Contains("服务器资产", html, StringComparison.Ordinal);
Assert.Contains("data-ip-rail", html, StringComparison.Ordinal);
Assert.Contains("10.0.0.0/24", html, StringComparison.Ordinal);
Assert.Contains("1 / 254", html, StringComparison.Ordinal);
Assert.Contains("<option value=", html, StringComparison.Ordinal);
Assert.Contains("data-drawer=\"register-server\"", html, StringComparison.Ordinal);
Assert.Contains("data-submit-label=\"正在检测\"", html, StringComparison.Ordinal);
```

继续保留“只读用户看不到变更控件”和“空闲 IP 预填”现有测试。

- [ ] **Step 2: 运行服务器页面测试并确认失败**

Run: `dotnet test tests/WebPass.IntegrationTests/WebPass.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~AssetAndPingTests|FullyQualifiedName~VisualSystemPageTests"`

Expected: FAIL，缺少 IP rail、网段选项和抽屉结构。

- [ ] **Step 3: 为页面模型增加只读筛选和摘要数据**

向 `IndexModel` 注入 `WebPassDbContext`。`LoadAsync` 在现有资产查询后：

```csharp
var subnets = await db.Subnets.AsNoTracking()
    .OrderBy(x => x.NetworkAddress)
    .Select(x => new SubnetFilterOption(x.Id, x.Name, x.Cidr))
    .ToListAsync(ct);
SubnetOptions = subnets;

if (Query.SubnetId is { } subnetId &&
    subnets.SingleOrDefault(x => x.Id == subnetId) is { } selected)
{
    var registered = await db.ServerAssets.LongCountAsync(
        x => x.SubnetId == subnetId && !x.IsArchived,
        ct);
    var usable = Ipv4Cidr.Parse(selected.Cidr).GetUsableAddressCount();
    SelectedSubnet = new(selected.Id, selected.Name, selected.Cidr, registered, usable);
}
```

添加所需 `using`，但不改变 `ServerAssetService.ListAsync`。

同时为 `ServerForm` 设置 `[Required(ErrorMessage = "请输入业务 IP。")]`、`[Required(ErrorMessage = "请输入位置。")]`、`[Required(ErrorMessage = "请输入计算机名。")]`、`[Required(ErrorMessage = "请输入系统名称。")]`；密码保持可选。服务异常进入 ModelState 时使用“无法登记服务器：请检查 IP、网段和必填信息。”作为安全兜底，不向页面输出内部异常细节。

- [ ] **Step 4: 重写服务器标题、命令条、表格和分页**

- 标题：“服务器资产”；主操作：“登记服务器”。
- 筛选保持 GET，网段改为 `select`，状态使用中文标签，复选框保留现有绑定名。
- 选中网段时渲染 `data-ip-rail`、CIDR、`RegisteredCount / UsableAddressCount` 和静态节点。
- 表格保留全部字段；IP 使用 `.data-value`；行内显示中文状态和显式文字。
- 高频操作为“编辑”“Ping”；查看密码、标记存活、归档收纳到语义操作区，但对应表单与权限条件保持服务端渲染。
- 登记表单保留在 DOM 中，使用 `data-drawer="register-server"`；无 JavaScript 时作为普通页面区块出现。
- 空闲 IP 查询存在时自动为抽屉添加 `data-open` 并预填。

- [ ] **Step 5: 为 Ping、标记存活和归档提供安全状态消息**

Ping 成功后设置中文 `TempData["StatusMessage"]`；`ExecuteAsync` 新增 `successMessage` 参数，成功后设置对应消息。错误仍由现有 handler 和 ModelState 返回，权限/限流状态码不变。

```csharp
TempData["StatusMessage"] =
    $"Ping {UiLabels.ForPingOutcome(result.Outcome)} · " +
    (result.LatencyMilliseconds is null ? "无延迟数据" : $"{result.LatencyMilliseconds} ms");
```

- [ ] **Step 6: 运行服务器与权限测试**

Run: `dotnet test tests/WebPass.IntegrationTests/WebPass.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~AssetAndPingTests|FullyQualifiedName~PermissionRouteTests|FullyQualifiedName~RevealTests|FullyQualifiedName~VisualSystemPageTests"`

Expected: PASS，0 failed。

- [ ] **Step 7: 提交任务**

```powershell
git add src/WebPass.Web/Pages/Servers/Index.cshtml src/WebPass.Web/Pages/Servers/Index.cshtml.cs tests/WebPass.IntegrationTests/Assets/AssetAndPingTests.cs tests/WebPass.IntegrationTests/Presentation/VisualSystemPageTests.cs
git commit -m "feat: redesign server inventory workspace"
```

---

### Task 5: 迁移服务器编辑和限时密码展示

**Files:**
- Modify: `src/WebPass.Web/Pages/Servers/Edit.cshtml`
- Modify: `src/WebPass.Web/Pages/Servers/Index.cshtml`
- Modify: `src/WebPass.Web/wwwroot/js/secret-reveal.js`
- Modify: `tests/WebPass.IntegrationTests/Assets/AssetAndPingTests.cs`
- Modify: `tests/WebPass.IntegrationTests/Secrets/RevealTests.cs`
- Modify: `tests/WebPass.IntegrationTests/Presentation/VisualSystemPageTests.cs`

**Interfaces:**
- Consumes: `[data-secret-reveal]`, `[data-asset-id]`, `[data-output]`
- Produces: `[data-secret-panel]`, `[data-secret-value]`, `[data-secret-countdown]`, `[data-secret-status]`

- [ ] **Step 1: 写编辑分组和敏感输出失败测试**

```csharp
Assert.Contains("身份与位置", editHtml, StringComparison.Ordinal);
Assert.Contains("系统信息", editHtml, StringComparison.Ordinal);
Assert.Contains("凭据与备注", editHtml, StringComparison.Ordinal);
Assert.Contains("留空则保留当前密码", editHtml, StringComparison.Ordinal);
Assert.Contains("data-secret-countdown", serversHtml, StringComparison.Ordinal);
Assert.DoesNotContain("server-password", serversHtml, StringComparison.Ordinal);
```

- [ ] **Step 2: 运行测试并确认失败**

Run: `dotnet test tests/WebPass.IntegrationTests/WebPass.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~AssetAndPingTests|FullyQualifiedName~RevealTests|FullyQualifiedName~VisualSystemPageTests"`

Expected: FAIL，缺少中文分组和倒计时结构。

- [ ] **Step 3: 重写服务器编辑表单**

使用三个 `<fieldset class="form-section">`，保留隐藏 `id`、`rowVersion`、全部 `asp-for` 和密码 `autocomplete="new-password"`。页底操作为“保存更改”和“返回服务器资产”；AssetId 缺失时显示包含预填 IP 的可行动链接。

并发冲突进入页面时显示“该服务器已被其他用户修改。以下为最新数据，请核对后重新保存。”；参数或业务规则失败显示“无法保存服务器：请检查 IP、网段和字段内容。”。继续保留最新数据库值和用户重新确认入口，不改变 409/BadRequest 的安全语义。

- [ ] **Step 4: 为每行密码输出建立受控区域**

`Index.cshtml` 的敏感区域使用：

```razor
<section id="secret-@id" data-secret-panel hidden>
    <span data-secret-status role="status" aria-live="polite">服务器密码将在 30 秒后自动隐藏</span>
    <code data-secret-value></code>
    <span data-secret-countdown aria-hidden="true">30</span>
    <button type="button" data-secret-copy>复制密码</button>
</section>
```

明文只能写入 `[data-secret-value]`，不得写入按钮、Toast、属性或 URL。

- [ ] **Step 5: 重写 `secret-reveal.js` 清理和倒计时**

脚本必须保留 POST、防伪令牌、same-origin credentials 和 403 重定向。增加：

- 每秒更新文字倒计时。
- 30 秒清空 `textContent` 并隐藏面板。
- `pagehide`、`visibilitychange`（进入 hidden）、再次 Reveal 前全部清空。
- 失败显示“暂时无法查看密码，请重试”，不放入 Toast。
- `[data-secret-copy]` 只读取同一面板内 `[data-secret-value]` 并写入剪贴板；复制成功显示“已复制”，但不重建或延长倒计时。

```javascript
document.addEventListener("visibilitychange", () => {
    if (document.visibilityState === "hidden") {
        document.querySelectorAll("[data-secret-panel]").forEach(clearPanel);
    }
});
window.addEventListener("pagehide", clearAll);
```

- [ ] **Step 6: 运行密码和并发回归测试**

Run: `dotnet test tests/WebPass.IntegrationTests/WebPass.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~RevealTests|FullyQualifiedName~AssetSecret|FullyQualifiedName~Edit_conflict|FullyQualifiedName~VisualSystemPageTests"`

Expected: PASS，0 failed。

- [ ] **Step 7: 提交任务**

```powershell
git add src/WebPass.Web/Pages/Servers/Edit.cshtml src/WebPass.Web/Pages/Servers/Index.cshtml src/WebPass.Web/wwwroot/js/secret-reveal.js tests/WebPass.IntegrationTests/Assets/AssetAndPingTests.cs tests/WebPass.IntegrationTests/Secrets/RevealTests.cs tests/WebPass.IntegrationTests/Presentation/VisualSystemPageTests.cs
git commit -m "feat: redesign server credentials workflow"
```

---

### Task 6: 迁移网段管理和 CIDR 预览

**Files:**
- Create: `src/WebPass.Web/wwwroot/js/subnet-preview.js`
- Modify: `src/WebPass.Web/Pages/Subnets/Index.cshtml`
- Modify: `src/WebPass.Web/Pages/Subnets/Index.cshtml.cs`
- Modify: `tests/WebPass.IntegrationTests/Authorization/SubnetFormSecurityTests.cs`
- Modify: `tests/WebPass.IntegrationTests/Presentation/VisualSystemPageTests.cs`

**Interfaces:**
- Consumes: `POST /subnets?handler=Preview`
- Produces: `[data-subnet-preview-form]`, `[data-subnet-preview-result]`, `[data-subnet-edit]`

- [ ] **Step 1: 写网段结构和预览失败测试**

```csharp
Assert.Contains("网段管理", html, StringComparison.Ordinal);
Assert.Contains("data-drawer=\"create-subnet\"", html, StringComparison.Ordinal);
Assert.Contains("data-subnet-preview-form", html, StringComparison.Ordinal);
Assert.Contains("data-subnet-edit", html, StringComparison.Ordinal);
Assert.Contains("/js/subnet-preview.js", html, StringComparison.Ordinal);
```

现有测试继续断言 Create handler URL、防伪令牌、权限撤销和行版本校验。

- [ ] **Step 2: 运行网段测试并确认失败**

Run: `dotnet test tests/WebPass.IntegrationTests/WebPass.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~SubnetFormSecurityTests|FullyQualifiedName~VisualSystemPageTests"`

Expected: FAIL，缺少抽屉、预览区域和脚本。

- [ ] **Step 3: 重写网段页面**

- 标题“网段管理”，主操作“添加网段”。
- 创建表单作为渐进增强抽屉，保持 `asp-page-handler="Create"`。
- 预览按钮为 `type="button"`，脚本不可用时仍提供正常提交到 `?handler=Preview` 的备用链接/按钮说明。
- 主列表只显示名称、CIDR、位置、状态和操作。
- 编辑表单放在每行的 `<details data-subnet-edit>` 内，不再同时展开所有字段。
- 删除确认显示网段名称和 CIDR；启用/停用显示影响说明。

- [ ] **Step 4: 实现 `subnet-preview.js`**

读取创建表单中的防伪令牌与字段，以 `URLSearchParams` POST 到现有 Preview handler。成功时渲染网络地址、广播地址、可用地址数和静态 IP rail；失败时在 `[data-subnet-preview-result]` 显示服务端 `error`，使用 `role="alert"`。

- [ ] **Step 5: 将阻断错误转为可行动中文文案**

不改变异常类型或状态码。页面模型捕获现有异常后添加：

- 重叠：`无法保存网段：该范围与现有网段重叠。`
- 关联资产超出新范围：`无法缩小网段：已有服务器地址将落在新范围之外。`
- 有关联资产删除：`无法删除网段：请先解除关联服务器，或停用该网段。`
- 其他参数错误：`网段信息无效，请检查 CIDR 和必填字段。`

`SubnetForm.Name`、`Cidr`、`Location` 分别使用中文必填错误“请输入网段名称。”“请输入 CIDR。”“请输入位置。”。

- [ ] **Step 6: 运行网段服务、权限和页面测试**

Run: `dotnet test WebPass.sln -c Release --filter "FullyQualifiedName~Subnet"`

Expected: PASS，0 failed。

- [ ] **Step 7: 提交任务**

```powershell
git add src/WebPass.Web/Pages/Subnets/Index.cshtml src/WebPass.Web/Pages/Subnets/Index.cshtml.cs src/WebPass.Web/wwwroot/js/subnet-preview.js tests/WebPass.IntegrationTests/Authorization/SubnetFormSecurityTests.cs tests/WebPass.IntegrationTests/Presentation/VisualSystemPageTests.cs
git commit -m "feat: redesign subnet management"
```

---

### Task 7: 迁移导入、普通导出和密码导出流程

**Files:**
- Modify: `src/WebPass.Web/Pages/Imports/Index.cshtml`
- Modify: `src/WebPass.Web/Pages/Imports/Index.cshtml.cs`
- Modify: `src/WebPass.Web/Pages/Exports/Index.cshtml`
- Modify: `src/WebPass.Web/Pages/Admin/PasswordExport.cshtml`
- Modify: `src/WebPass.Web/wwwroot/js/site.js`
- Modify: `tests/WebPass.IntegrationTests/Importing/ImportPageTests.cs`
- Modify: `tests/WebPass.IntegrationTests/Exporting/ExportPageTests.cs`
- Modify: `tests/WebPass.IntegrationTests/Exporting/AdministratorPasswordExportPageTests.cs`
- Modify: `tests/WebPass.IntegrationTests/Presentation/VisualSystemPageTests.cs`

**Interfaces:**
- Produces: `.upload-zone`, `.import-summary`, `.import-errors`, `.export-scope`, `.risk-callout`
- Produces: `[data-upload-zone]`, `[data-upload-input]`, `[data-export-format]`, `[data-export-submit]`
- Consumes: 现有 Preview/Commit/Download handlers 和 reauthentication redirect

- [ ] **Step 1: 写三个流程的中文结构失败测试**

```csharp
Assert.Contains("导入服务器数据", importHtml, StringComparison.Ordinal);
Assert.Contains("最大 10 MB，最多 5,000 行", importHtml, StringComparison.Ordinal);
Assert.Contains("普通导出不包含服务器密码", exportHtml, StringComparison.Ordinal);
Assert.Contains("导出服务器密码", passwordExportHtml, StringComparison.Ordinal);
Assert.Contains("文件包含明文服务器密码", passwordExportHtml, StringComparison.Ordinal);
Assert.DoesNotContain("name=\"Format\"", passwordExportHtml, StringComparison.Ordinal);
```

- [ ] **Step 2: 运行页面测试并确认旧英文断言失败**

Run: `dotnet test tests/WebPass.IntegrationTests/WebPass.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~ImportPageTests|FullyQualifiedName~ExportPageTests|FullyQualifiedName~AdministratorPasswordExportPageTests|FullyQualifiedName~VisualSystemPageTests"`

Expected: FAIL，页面仍为英文或缺少新结构。

- [ ] **Step 3: 重写导入页面**

- 上传区保留标准 `<input type="file" accept=".csv,.xlsx">`，添加可聚焦标签和文件限制。
- `site.js` 为 `[data-upload-zone]` 增加 dragenter/dragleave/drop 渐进增强：只把单个拖入文件赋给 `[data-upload-input]` 并更新可见文件名，真正上传仍由现有 multipart 表单提交；键盘文件选择始终可用。
- 预览摘要使用四项：新增、更新、跳过、错误。
- 错误以表格显示行号、字段、原因，不输出原始密码单元格。
- `HasBlockingErrors` 为 true 时不渲染提交表单，并说明必须修复文件后重新上传。
- 提交按钮文字“提交导入”，忙碌文字“正在导入”。
- GET 页面显示 `TempData["ImportResult"]` 的中文成功消息；将 PageModel 中英文结果改为“已新增 X 项，更新 Y 项，跳过 Z 项”。
- 未选择文件显示“请选择 CSV 或 XLSX 文件。”；类型或内容类型不允许时显示“仅支持 CSV 和 XLSX 服务器清单文件。”；预览失效显示“导入预览已失效，请重新上传文件。”。

- [ ] **Step 4: 重写普通导出页面**

- 顶部显示绿色安全说明。
- 字段使用中文标签，状态选项使用 `UiLabels`。
- 格式选择变化时按钮文字由 `site.js` 的 `change` 监听更新为“下载 CSV”或“下载 XLSX”；无 JavaScript 时默认“下载导出文件”。
- 不添加虚假进度条，保留 no-store 文件响应。
- 参数错误进入 ModelState 时显示“无法导出：请检查筛选条件和文件格式。”，不直接回显内部异常。

- [ ] **Step 5: 重写管理员密码导出页面**

- 琥珀风险说明明确“文件包含明文服务器密码”。
- 显示筛选范围、固定 XLSX 格式和审计说明，不显示后端未提供的预计数量。
- 主操作“确认并导出密码”，忙碌文字“正在准备文件”。
- 未二次验证时继续由 handler 重定向 `/secrets/reauthenticate`；不得以前端状态替代。
- 参数错误进入 ModelState 时显示“无法导出服务器密码：请检查筛选条件。”，不回显服务端内部细节。

- [ ] **Step 6: 更新旧英文测试但保留安全断言**

将 `plaintext server passwords` 断言替换为 `明文服务器密码`。继续断言普通导出无密码、密码导出只允许管理员、需要防伪、需要二次验证、文件 no-store 且只为 XLSX。

- [ ] **Step 7: 运行导入导出完整测试**

Run: `dotnet test tests/WebPass.IntegrationTests/WebPass.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~Import|FullyQualifiedName~Export"`

Expected: PASS，0 failed。

- [ ] **Step 8: 提交任务**

```powershell
git add src/WebPass.Web/Pages/Imports/Index.cshtml src/WebPass.Web/Pages/Imports/Index.cshtml.cs src/WebPass.Web/Pages/Exports/Index.cshtml src/WebPass.Web/Pages/Admin/PasswordExport.cshtml src/WebPass.Web/wwwroot/js/site.js tests/WebPass.IntegrationTests/Importing/ImportPageTests.cs tests/WebPass.IntegrationTests/Exporting/ExportPageTests.cs tests/WebPass.IntegrationTests/Exporting/AdministratorPasswordExportPageTests.cs tests/WebPass.IntegrationTests/Presentation/VisualSystemPageTests.cs
git commit -m "feat: redesign data transfer workflows"
```

---

### Task 8: 迁移审计日志和用户权限管理

**Files:**
- Modify: `src/WebPass.Web/Pages/Audit/Index.cshtml`
- Modify: `src/WebPass.Web/Pages/Admin/Users.cshtml`
- Modify: `src/WebPass.Web/Pages/Admin/Users.cshtml.cs`
- Modify: `src/WebPass.Web/wwwroot/js/site.js`
- Modify: `tests/WebPass.IntegrationTests/Authorization/AdminUsersTests.cs`
- Modify: `tests/WebPass.IntegrationTests/Presentation/VisualSystemPageTests.cs`

**Interfaces:**
- Consumes: `UiLabels.ForPermission`
- Consumes: `[data-copy]` DOM contract
- Produces: `TempData["StatusMessage"]` for create/reset/enablement/permission updates

- [ ] **Step 1: 写审计和用户页面失败测试**

```csharp
Assert.Contains("审计日志", auditHtml, StringComparison.Ordinal);
Assert.Contains("只读记录", auditHtml, StringComparison.Ordinal);
Assert.DoesNotContain("筛选审计", auditHtml, StringComparison.Ordinal);
Assert.Contains("data-copy", auditHtml, StringComparison.Ordinal);

Assert.Contains("用户与权限", usersHtml, StringComparison.Ordinal);
Assert.Contains("创建普通用户", usersHtml, StringComparison.Ordinal);
Assert.Contains("查看服务器密码", usersHtml, StringComparison.Ordinal);
Assert.DoesNotContain(PermissionCode.SecretReveal + "</label>", usersHtml, StringComparison.Ordinal);
```

- [ ] **Step 2: 运行测试并确认失败**

Run: `dotnet test tests/WebPass.IntegrationTests/WebPass.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~AdminUsersTests|FullyQualifiedName~VisualSystemPageTests"`

Expected: FAIL，页面仍暴露英文和原始权限代码。

- [ ] **Step 3: 重写审计页面**

- 列保持时间、操作人、动作、对象、结果、关联编号。
- 关联编号使用 `.data-value` 和 `data-copy` 按钮。
- 显示“审计日志为只读记录，不能在此修改或删除”。
- 不添加当前后端不支持的筛选、分页或删除入口。
- 空集合显示“尚无审计记录”。

- [ ] **Step 4: 重写用户页面**

- 创建表单进入渐进增强抽屉。
- 表格列：用户、角色、状态、权限摘要、操作。
- 普通用户的权限编辑放入行内 `<details>`；权限标签使用 `UiLabels.ForPermission`。
- 管理员行显示“管理员拥有全部权限”，不渲染复选框、禁用或重置按钮。
- 重置、启用/禁用和保存权限的确认区域显示用户名与影响。
- 不回显系统预设初始密码。

- [ ] **Step 5: 添加非敏感成功状态消息**

在各成功 handler 重定向前设置：

```csharp
TempData["StatusMessage"] = $"用户 {user.Username} 的密码已重置为系统预设初始密码。";
TempData["StatusMessage"] = $"已{(isEnabled ? "启用" : "禁用")}用户 {user.Username}。";
TempData["StatusMessage"] = $"已更新用户 {user.Username} 的权限。";
```

创建成功设置“已创建用户 …”。状态消息不得包含密码哈希或预设密码文本。

- [ ] **Step 6: 完成 `site.js` 的复制反馈**

`copyText` 从 `data-copy-target` 指向的元素读取 `textContent`，使用 `navigator.clipboard.writeText`；成功后按钮短暂显示“已复制”，1800 ms 后恢复。失败时显示“复制失败，请手动选择”，不把复制内容写入日志。

- [ ] **Step 7: 运行用户、权限、审计测试**

Run: `dotnet test tests/WebPass.IntegrationTests/WebPass.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~AdminUsersTests|FullyQualifiedName~Permission|FullyQualifiedName~Audit|FullyQualifiedName~VisualSystemPageTests"`

Expected: PASS，0 failed。

- [ ] **Step 8: 提交任务**

```powershell
git add src/WebPass.Web/Pages/Audit/Index.cshtml src/WebPass.Web/Pages/Admin/Users.cshtml src/WebPass.Web/Pages/Admin/Users.cshtml.cs src/WebPass.Web/wwwroot/js/site.js tests/WebPass.IntegrationTests/Authorization/AdminUsersTests.cs tests/WebPass.IntegrationTests/Presentation/VisualSystemPageTests.cs
git commit -m "feat: redesign governance pages"
```

---

### Task 9: 完成响应式、可访问性和静态安全回归

**Files:**
- Modify: `src/WebPass.Web/wwwroot/css/site.css`
- Modify: `src/WebPass.Web/wwwroot/js/site.js`
- Modify: `tests/WebPass.IntegrationTests/Presentation/VisualSystemPageTests.cs`

**Interfaces:**
- Consumes: Tasks 2–8 的全部 DOM 契约
- Produces: 1280 px、768 px、375 px 三档布局和 WCAG 2.2 AA 基线

- [ ] **Step 1: 写可访问性和静态安全失败测试**

```csharp
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
    Assert.DoesNotContain("<input placeholder=", html, StringComparison.OrdinalIgnoreCase);
}

[Fact]
public async Task Static_assets_do_not_reference_remote_resources()
{
    using var factory = new PresentationFactory();
    using var client = factory.CreateClient();
    var assets = string.Join('\n', new[] {
        await client.GetStringAsync("/css/site.css"),
        await client.GetStringAsync("/js/site.js"),
        await client.GetStringAsync("/js/secret-reveal.js"),
        await client.GetStringAsync("/js/subnet-preview.js")
    });
    Assert.DoesNotContain("https://", assets, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("http://", assets, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: 运行展示测试并确认遗漏**

Run: `dotnet test tests/WebPass.IntegrationTests/WebPass.IntegrationTests.csproj -c Release --filter FullyQualifiedName~VisualSystemPageTests`

Expected: FAIL 于尚未完成的 skip link、ARIA、远程引用或静态资源断言。

- [ ] **Step 3: 完成桌面、平板和手机断点**

在 `site.css` 明确定义：

```css
@media (min-width: 1280px) { .app-shell { grid-template-columns: 232px minmax(0, 1fr); } }
@media (min-width: 768px) and (max-width: 1279px) { .app-shell { grid-template-columns: 72px minmax(0, 1fr); } .nav-label, .nav-group-title { position: absolute; clip: rect(0 0 0 0); } }
@media (max-width: 767px) { .app-shell { display: block; } .app-sidebar { position: fixed; inset: 0 auto 0 0; width: min(88vw, 320px); transform: translateX(-100%); } .app-sidebar[data-open] { transform: translateX(0); } .form-grid { grid-template-columns: 1fr; } .data-table-wrap { overflow-x: auto; } }
```

保证 200% 缩放时抽屉和确认按钮仍可滚动到达；焦点环不得被表格或菜单裁切。

- [ ] **Step 4: 完成键盘和 ARIA 行为**

- 当前导航设置 `aria-current="page"`。
- 移动导航按钮维护 `aria-expanded`。
- 抽屉打开后聚焦第一个可交互元素，`Escape` 关闭并返回焦点。
- Toast 使用 `role="status"`；阻断错误使用 `role="alert"`。
- 密码值本身不放入自动朗读区域；状态说明使用 `aria-live="polite"`。
- 表格操作菜单和 `<details>` 保持原生键盘行为。

- [ ] **Step 5: 运行展示与安全头测试**

Run: `dotnet test tests/WebPass.IntegrationTests/WebPass.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~VisualSystemPageTests|FullyQualifiedName~ProductionSecurityTests"`

Expected: PASS，0 failed。

- [ ] **Step 6: 使用真实浏览器执行视觉验收**

在已按 README 配置的本地 HTTPS 开发环境运行：

```powershell
$env:ASPNETCORE_URLS='https://localhost:5001'
dotnet run --project src/WebPass.Web --no-build
```

使用 Playwright 技能在 1366×768、1920×1080、768×1024、375×812 四个视口检查 `/login`；登录开发账户后检查 `/servers`、`/subnets`、`/imports`、`/exports`、`/audit`、`/admin/users`、`/admin/password-export`。每个视口保存截图，确认无遮挡、不可达操作、溢出正文或错误焦点。检查 `prefers-reduced-motion: reduce` 时 IP rail 不循环移动。

- [ ] **Step 7: 提交任务**

```powershell
git add src/WebPass.Web/wwwroot/css/site.css src/WebPass.Web/wwwroot/js/site.js tests/WebPass.IntegrationTests/Presentation/VisualSystemPageTests.cs
git commit -m "test: verify responsive accessible presentation"
```

---

### Task 10: 全量验证、页面清单核对与发布前整理

**Files:**
- Verify only: all files changed in Tasks 1–9

**Interfaces:**
- Consumes: 完整视觉系统、页面测试与现有安全测试
- Produces: 可构建、测试通过、无外部依赖且页面范围完整的实现分支

- [ ] **Step 1: 核对全部页面和处理端点**

确认测试或设计明确覆盖：

```text
/login
/servers
/servers/{id}/edit
/servers/{id}/ping (handler only)
/subnets
/imports
/exports
/audit
/admin/users
/admin/password-export
/secrets/reauthenticate
/secrets/reveal (handler only)
/error
/logout (handler only)
/health (machine-readable only)
```

- [ ] **Step 2: 运行 Release 构建**

Run: `dotnet build WebPass.sln -c Release`

Expected: exit 0，0 errors。

- [ ] **Step 3: 运行完整测试套件**

Run: `dotnet test WebPass.sln -c Release --no-build`

Expected: exit 0，0 failed。

- [ ] **Step 4: 检查外部依赖和敏感文案**

```powershell
Get-ChildItem src/WebPass.Web/Pages,src/WebPass.Web/wwwroot -Recurse -File |
    Select-String -Pattern 'https?://|server-password|abc123' -CaseSensitive:$false
```

Expected: 页面与静态资源无远程 URL、测试密码或系统预设密码文本。若 CSP/文档性字符串产生合法命中，逐条核对，不能直接忽略整类结果。

- [ ] **Step 5: 检查格式和工作区边界**

Run: `git diff --check`

Expected: no output，exit 0。

Run: `git status --short`

Expected: 只保留用户原有的 `README.md`、`WebPass.rar`、`artifacts/`，以及尚未提交的本任务文件；不得暂存用户原有改动。

- [ ] **Step 6: 执行最终代码审查**

使用 `requesting-code-review` 技能检查：设计规格覆盖、权限条件、敏感数据清理、无 JavaScript 降级、中文文案一致性、响应式和测试完整性。修复审查发现的问题后，重新执行 Steps 2–5。

- [ ] **Step 7: 提交最终修正（仅在确有修正时）**

最终审查修正限定为跨页样式、交互和展示测试收口；如审查要求修改业务页，应返回对应 Task 的测试循环和提交边界，而不是混入最终提交。

```powershell
git add src/WebPass.Web/wwwroot/css/site.css src/WebPass.Web/wwwroot/js/site.js tests/WebPass.IntegrationTests/Presentation/VisualSystemPageTests.cs
git commit -m "fix: address visual system review"
```

如果没有修正，不创建空提交。

---

## 实施顺序与验收门槛

1. Tasks 1–3 建立共享展示基础；通过后才迁移业务页。
2. Tasks 4–8 每个任务都必须保持对应业务与安全测试通过，不能等到最后集中修复。
3. Task 9 只做跨页可访问性和响应式收口，不借机改变业务流程。
4. Task 10 的 Release build、完整测试、外部依赖扫描和 `git diff --check` 全部通过后，才可以宣称实现完成。

## 明确不在本计划中的后续工作

- 暗色主题和主题切换。
- 审计筛选、分页或报表。
- 一次性随机初始密码策略。
- 自动 Ping、实时更新或批量操作。
- 双语资源文件和本地化框架。
- 将 Razor Pages 改写为 SPA。
