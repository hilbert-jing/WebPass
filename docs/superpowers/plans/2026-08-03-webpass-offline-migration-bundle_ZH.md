# WebPass离线迁移捆绑包实施方案

> **对于代理工人：** 所需的子技能：使用超级权力：子代理驱动开发（推荐）或超级权力：执行计划来逐个任务地实施该计划。步骤使用复选框 (`- [ ]`) 语法进行跟踪。

**目标：** 在具有匹配的 .NET 10 SDK 但无法访问 `WebPass.Migrations.exe` 或内部 NuGet 源的 Windows 构建计算机上构建并验证 `WebPass.Migrations.exe`。

**架构：** 连接的准备脚本创建一个经过验证的、源提交绑定的离线依赖项工具包，其中包含 `dotnet-ef 10.0.0`、本地 NuGet 源和隔离的全局包缓存。现有的捆绑脚本获得严格的 `dotnet-ef 10.0.0` 模式，仅使用该套件，同时保留其当前的存储库本地开发人员模式。集成测试准备或使用工具包，在没有 HTTP 包源的情况下构建捆绑包，将其应用到唯一的本地 SQL Server 数据库，并比较所有应用的迁移。

**技术堆栈：** PowerShell 5.1+、.NET 10 SDK、EF Core/dotnet-ef 10.0.0、NuGet 本地源、Git、xUnit、SQL Server Express、Windows `win-x64` 依赖于框架的发布。

## 全局约束

- 来自 Windows 上经过审查的 WebPass 源代码提交。
- 离线构建机器具有匹配的.NET 10 SDK，但无法访问`nuget.org`并且没有内部NuGet源。
- 连接的准备机可以访问配置的可信NuGet源。
-  离线工具包通过操作员控制的媒体或内部文件共享进行传输，并且永远不会提交给 Git。
- 将`dotnet-ef`精确地固定到`dotnet-ef`；切勿隐式选择最新版本。
- 生成一个依赖于框架的`win-x64`捆绑包，名为`win-x64`。
- 保留现有的默认输出`src/WebPass.Web/bin/Release/migrations/win-x64/WebPass.Migrations.exe`。
- 省略`-OfflineKitPath`时保留现有的开发人员工作流程；不要在该模式下自动安装工具。
- 严格脱机模式不得回退到 HTTP 包源或修改用户级或计算机级 NuGet 配置。
- 不要提交脱机包、NuGet 缓存、工具二进制文件、临时捆绑包或 `WebPass.Migrations.exe`。
- 不要记录连接字符串、密码、包源凭据或其他机密。
- 请勿修改 WebPass UI、身份验证、授权、审核、实体、`WebPassDbContext`、模型快照或现有迁移。
- 使用精确唯一的临时目录并仅删除其解析路径。
- 运行无负载或压力测试。
- 在每个任务检查点后停止，报告下一个任务的预期文件和令牌成本，并在继续之前获得批准。

## 文件地图

- Create `scripts/Prepare-WebPassMigrationOfflineKit.ps1`：联网机器套件准备、验证、清单创建和安全发布。
- 修改 `scripts/Build-WebPassMigrationBundle.ps1`：可选的严格离线套件验证和捆绑包生成。
- 创建 `tests/WebPass.IntegrationTests/Deployment/MigrationOfflineKitFixture.cs`：准备一个测试拥有的套件或使用 `tests/WebPass.IntegrationTests/Deployment/MigrationOfflineKitFixture.cs`，运行超时的子进程，并仅清理拥有的路径。
- 修改`tests/WebPass.IntegrationTests/Deployment/MigrationBundleTests.cs`：准备验证、严格离线拒绝案例和实际的SQL Server捆绑应用。
- Modify `docs/deployment/windows-server-iis.md`：连接准备、离线构建和部署服务器工作流程。
- 修改`docs/deployment/windows-server-iis_ZH.md`：简体中文版相同的工作流程，保留UTF-8。
- 验证`.gitignore`：现有的`.gitignore`、`.gitignore`、`.gitignore`和`.gitignore`规则涵盖默认套件/工具/缓存/捆绑包位置；仅当测试证明生成的路径对 Git 可见时才进行修改。

---

### 任务 1：准备并验证离线依赖包

**检查点估计：** 2 个新文件； 14k-20k 代币。

**文件：**
- 创建：`scripts/Prepare-WebPassMigrationOfflineKit.ps1`
- 创建：`tests/WebPass.IntegrationTests/Deployment/MigrationOfflineKitFixture.cs`
- 修改：`tests/WebPass.IntegrationTests/Deployment/MigrationBundleTests.cs`

**接口：**
- 消耗：.NET 10 SDK、Git、配置的受信任 NuGet 源、`src/WebPass.Web/WebPass.Web.csproj` 和 `src/WebPass.Web/WebPass.Web.csproj`。
- 产品：

```powershell
.\scripts\Prepare-WebPassMigrationOfflineKit.ps1 `
  -OutputPath <offline-kit-directory> `
  [-Force]
```

-  生成具有确切属性的清单 JSON：

```json
{
  "formatVersion": 1,
  "sourceCommit": "<40-character lowercase Git commit>",
  "dotnetEfVersion": "10.0.0",
  "sdkMajorVersion": 10,
  "targetRuntime": "win-x64",
  "createdAtUtc": "<ISO-8601 UTC timestamp>"
}
```

- 生产测试治具合同：

```csharp
[CollectionDefinition(Name)]
public sealed class MigrationOfflineKitCollection
    : ICollectionFixture<MigrationOfflineKitFixture>
{
    public const string Name = "Migration offline kit";
}

public sealed class MigrationOfflineKitFixture : IAsyncLifetime
{
    public string RepositoryRoot { get; }
    public string KitPath { get; private set; } = string.Empty;
    public bool OwnsKit { get; private set; }
    public Task InitializeAsync();
    public Task DisposeAsync();
    public static Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment = null,
        TimeSpan? timeout = null);
}
```

- [ ] **步骤 1：添加失败的准备测试和共享夹具**

创建`MigrationOfflineKitFixture.cs`。使用五个 `MigrationOfflineKitFixture.cs` 段从 `MigrationOfflineKitFixture.cs` 解析存储库根，与现有捆绑测试完全相同。如果设置了`MigrationOfflineKitFixture.cs`，则解析它并设置`MigrationOfflineKitFixture.cs`。否则创建：

```csharp
var root = Path.Combine(
    Path.GetTempPath(),
    "WebPassMigrationOfflineKitTests",
    Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
KitPath = Path.Combine(root, "kit");
OwnsKit = true;
```

装置必须调用 PowerShell，并设置 15 分钟超时：

```csharp
var result = await RunAsync(
    "powershell.exe",
    [
        "-NoProfile",
        "-File",
        Path.Combine(
            RepositoryRoot,
            "scripts",
            "Prepare-WebPassMigrationOfflineKit.ps1"),
        "-OutputPath",
        KitPath,
    ],
    timeout: TimeSpan.FromMinutes(15));

Assert.True(
    result.ExitCode == 0,
    $"Offline-kit preparation failed.{Environment.NewLine}" +
    $"{result.Error}{Environment.NewLine}{result.Output}");
```

`RunAsync` 必须重定向标准输出/错误，使用 `WaitForExitAsync(cancellationToken)`，在超时时调用 `Kill(entireProcessTree: true)`，等待两个重定向的流，然后返回：

```csharp
public sealed record ProcessResult(
    int ExitCode,
    string Output,
    string Error);
```

`DisposeAsync` 仅删除夹具的唯一父目录。它永远不会删除调用者提供的工具包。

用以下内容注释`MigrationBundleTests`：

```csharp
[Collection(MigrationOfflineKitCollection.Name)]
public sealed class MigrationBundleTests(
    MigrationOfflineKitFixture offlineKit)
```

添加：

```csharp
[Fact]
public async Task Preparation_script_creates_a_valid_offline_kit()
{
    Assert.True(File.Exists(Path.Combine(
        offlineKit.KitPath,
        "manifest.json")));
    Assert.True(File.Exists(Path.Combine(
        offlineKit.KitPath,
        "NuGet.Config")));
    Assert.True(File.Exists(Path.Combine(
        offlineKit.KitPath,
        "tools",
        "dotnet-ef.exe")));
    Assert.NotEmpty(Directory.EnumerateFiles(
        Path.Combine(offlineKit.KitPath, "feed"),
        "*.nupkg"));
    Assert.NotEmpty(Directory.EnumerateDirectories(
        Path.Combine(offlineKit.KitPath, "packages")));

    using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
        Path.Combine(offlineKit.KitPath, "manifest.json")));
    var root = manifest.RootElement;
    Assert.Equal(1, root.GetProperty("formatVersion").GetInt32());
    Assert.Equal(
        "10.0.0",
        root.GetProperty("dotnetEfVersion").GetString());
    Assert.Equal(10, root.GetProperty("sdkMajorVersion").GetInt32());
    Assert.Equal(
        "win-x64",
        root.GetProperty("targetRuntime").GetString());

    var config = await File.ReadAllTextAsync(Path.Combine(
        offlineKit.KitPath,
        "NuGet.Config"));
    Assert.Contains("<clear />", config, StringComparison.Ordinal);
    Assert.DoesNotContain(
        "http://",
        config,
        StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain(
        "https://",
        config,
        StringComparison.OrdinalIgnoreCase);
}
```

添加`using System.Text.Json;`，并从`using System.Text.Json;`中删除旧的私有`using System.Text.Json;`和`using System.Text.Json;`；后面的测试会消耗夹具助手。

- [ ] **步骤 2：运行重点测试并验证 RED**

运行：

```powershell
dotnet test tests\WebPass.IntegrationTests\WebPass.IntegrationTests.csproj `
  -c Release `
  --filter FullyQualifiedName~Preparation_script_creates_a_valid_offline_kit
```

预期：失败，因为 `scripts/Prepare-WebPassMigrationOfflineKit.ps1` 不存在。

- [ ] **步骤 3：在准备脚本中实现安全路径和命令帮助程序**

启动脚本：

```powershell
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputPath,

    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$DotnetEfVersion = '10.0.0'
$TargetRuntime = 'win-x64'
$KitFormatVersion = 1
$repositoryRoot = (Resolve-Path -LiteralPath (
    Join-Path $PSScriptRoot '..')).Path
$webProject = Join-Path $repositoryRoot (
    'src\WebPass.Web\WebPass.Web.csproj')
$solution = Join-Path $repositoryRoot 'WebPass.sln'
```

添加一个保留本机退出代码的外部命令帮助程序：

```powershell
function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$ArgumentList
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE."
    }
}
```

解析绝对目标，拒绝存储库根及其父级，并将暂存目录放在目标旁边：

```powershell
$resolvedOutput = [System.IO.Path]::GetFullPath(
    $(if ([System.IO.Path]::IsPathRooted($OutputPath)) {
        $OutputPath
    }
    else {
        Join-Path $repositoryRoot $OutputPath
    }))
$repositoryPrefix = $repositoryRoot.TrimEnd('\') + '\'
if ($repositoryRoot.StartsWith(
        $resolvedOutput.TrimEnd('\') + '\',
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The offline-kit output cannot be the repository or its parent.'
}
if ((Test-Path -LiteralPath $resolvedOutput) -and -not $Force) {
    throw "Offline-kit output already exists: $resolvedOutput"
}

$parent = Split-Path -Parent $resolvedOutput
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$staging = Join-Path $parent (
    '.webpass-offline-kit-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging | Out-Null
```

 脚本的 `finally` 块仅删除仍然存在的 `finally`。

- [ ] **步骤 4：实施连接恢复和第一个捆绑包生成**

验证 SDK 并提交：

```powershell
$sdkVersion = (& dotnet --version).Trim()
$parsedSdk = $null
if ($LASTEXITCODE -ne 0 -or
    -not [Version]::TryParse($sdkVersion, [ref]$parsedSdk) -or
    $parsedSdk.Major -ne 10) {
    throw "A .NET 10 SDK is required. Found: $sdkVersion"
}
$sourceCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[a-f0-9]{40}$') {
    throw 'The reviewed source Git commit could not be determined.'
}
```

创建 `tools`、`tools` 和 `tools`，安装固定工具，并预热项目/运行时依赖关系图：

```powershell
$tools = Join-Path $staging 'tools'
$seedPackages = Join-Path $staging 'seed-packages'
$feed = Join-Path $staging 'feed'
New-Item -ItemType Directory -Path $tools,$seedPackages,$feed |
    Out-Null

Invoke-CheckedCommand dotnet @(
    'tool', 'install', 'dotnet-ef',
    '--tool-path', $tools,
    '--version', $DotnetEfVersion)

$oldPackages = $env:NUGET_PACKAGES
try {
    $env:NUGET_PACKAGES = $seedPackages
    Invoke-CheckedCommand dotnet @(
        'restore', $solution,
        '--runtime', $TargetRuntime)
    Invoke-CheckedCommand (Join-Path $tools 'dotnet-ef.exe') @(
        'migrations', 'bundle',
        '--project', $webProject,
        '--startup-project', $webProject,
        '--configuration', 'Release',
        '--target-runtime', $TargetRuntime,
        '--output', (Join-Path $staging 'seed-bundle.exe'),
        '--force')
}
finally {
    $env:NUGET_PACKAGES = $oldPackages
}
```

将`*.nupkg`中的每个唯一的`*.nupkg`复制到`*.nupkg`中；如果两个文件具有相同的名称但 SHA-256 哈希值不同，则终止而不是覆盖：

```powershell
Get-ChildItem -LiteralPath $seedPackages -Recurse -Filter '*.nupkg' |
    ForEach-Object {
        $destination = Join-Path $feed $_.Name
        if (Test-Path -LiteralPath $destination) {
            $sourceHash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            $targetHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
            if ($sourceHash -ne $targetHash) {
                throw "Conflicting package file: $($_.Name)"
            }
        }
        else {
            Copy-Item -LiteralPath $_.FullName -Destination $destination
        }
    }
```

- [ ] **第 5 步：仅从本地源进行验证并发布套件**

在没有网络源的情况下以UTF-8写入`NuGet.Config`：

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="WebPassOffline" value="feed" />
  </packageSources>
</configuration>
```

创建一个新的 `packages` 目录，设置进程范围的恢复变量，使用套件配置运行显式恢复，并生成验证包：

```powershell
$packages = Join-Path $staging 'packages'
$config = Join-Path $staging 'NuGet.Config'
New-Item -ItemType Directory -Path $packages | Out-Null
$oldRestoreSources = $env:RestoreSources
$oldAudit = $env:NuGetAudit
try {
    $env:NUGET_PACKAGES = $packages
    $env:RestoreSources = $feed
    $env:NuGetAudit = 'false'
    Invoke-CheckedCommand dotnet @(
        'restore', $webProject,
        '--runtime', $TargetRuntime,
        '--configfile', $config,
        '--packages', $packages,
        '--no-http-cache')
    Invoke-CheckedCommand (Join-Path $tools 'dotnet-ef.exe') @(
        'migrations', 'bundle',
        '--project', $webProject,
        '--startup-project', $webProject,
        '--configuration', 'Release',
        '--target-runtime', $TargetRuntime,
        '--output', (Join-Path $staging 'validation-bundle.exe'),
        '--force')
}
finally {
    $env:NUGET_PACKAGES = $oldPackages
    $env:RestoreSources = $oldRestoreSources
    $env:NuGetAudit = $oldAudit
}
```

需要验证包，删除 `seed-packages`（两个临时包），然后编写清单：

```powershell
$manifest = [ordered]@{
    formatVersion = $KitFormatVersion
    sourceCommit = $sourceCommit
    dotnetEfVersion = $DotnetEfVersion
    sdkMajorVersion = 10
    targetRuntime = $TargetRuntime
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
}
$manifest | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $staging 'manifest.json') `
        -Encoding utf8
Remove-Item -LiteralPath `
    (Join-Path $staging 'seed-bundle.exe'), `
    (Join-Path $staging 'validation-bundle.exe'), `
    $seedPackages `
    -Recurse -Force
```

发布而不暴露部分目标：

```powershell
$backup = $null
try {
    if (Test-Path -LiteralPath $resolvedOutput) {
        $backup = Join-Path $parent (
            '.webpass-offline-kit-backup-' +
            [Guid]::NewGuid().ToString('N'))
        Move-Item -LiteralPath $resolvedOutput -Destination $backup
    }
    Move-Item -LiteralPath $staging -Destination $resolvedOutput
    $staging = $null
    if ($backup -and (Test-Path -LiteralPath $backup)) {
        Remove-Item -LiteralPath $backup -Recurse -Force
        $backup = $null
    }
}
catch {
    if ($backup -and (Test-Path -LiteralPath $backup)) {
        if (Test-Path -LiteralPath $resolvedOutput) {
            Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
        }
        Move-Item -LiteralPath $backup -Destination $resolvedOutput
        $backup = $null
    }
    throw
}
```

 仅当变量非空并且确切解析的暂存路径仍然存在时，外部 `finally` 才会删除 `finally`。它永远不会删除未恢复的备份。

- [ ] **步骤 6：运行准备测试并验证绿色**

运行步骤2命令。

预期：一项测试通过，不跳过任何测试，测试进程退出后删除临时测试拥有的套件。

- [ ] **步骤 7：验证生成的工件仍然被忽略并提交任务 1**

运行：

```powershell
.\scripts\Prepare-WebPassMigrationOfflineKit.ps1 `
  -OutputPath artifacts\WebPassMigrationOfflineKit `
  -Force
git check-ignore -v -- `
  artifacts\WebPassMigrationOfflineKit\manifest.json `
  artifacts\WebPassMigrationOfflineKit\tools\dotnet-ef.exe `
  artifacts\WebPassMigrationOfflineKit\feed
git diff --check
git status --short
```

预期：`.gitignore` 报告 `.gitignore`；仅三个任务 1 源/测试文件出现在状态中。

阶段并提交：

```powershell
git add -- `
  scripts/Prepare-WebPassMigrationOfflineKit.ps1 `
  tests/WebPass.IntegrationTests/Deployment/MigrationOfflineKitFixture.cs `
  tests/WebPass.IntegrationTests/Deployment/MigrationBundleTests.cs
git commit -m "feat: prepare offline migration dependency kit"
```

停止并获得任务 2 的批准。

---

### 任务 2：在严格离线模式下构建并应用迁移包

**检查点估计：** 2 个修改文件； 14k-20k 代币。

**文件：**
- 修改：`scripts/Build-WebPassMigrationBundle.ps1`
- 修改：`tests/WebPass.IntegrationTests/Deployment/MigrationBundleTests.cs`

**接口：**
-  使用任务 1 套件和清单。
- 蜜饯：

```powershell
.\scripts\Build-WebPassMigrationBundle.ps1 `
  [-OutputPath <path-to-WebPass.Migrations.exe>]
```

- 添加：

```powershell
.\scripts\Build-WebPassMigrationBundle.ps1 `
  [-OutputPath <path-to-WebPass.Migrations.exe>] `
  [-OfflineKitPath <offline-kit-directory>]
```

- [ ] **步骤1：用严格的离线模式替换现有的端到端测试，并添加拒绝测试**

将现有的捆绑包构建调用更改为：

```csharp
var build = await MigrationOfflineKitFixture.RunAsync(
    "powershell.exe",
    [
        "-NoProfile",
        "-File",
        Path.Combine(
            offlineKit.RepositoryRoot,
            "scripts",
            "Build-WebPassMigrationBundle.ps1"),
        "-OfflineKitPath",
        offlineKit.KitPath,
        "-OutputPath",
        bundle,
    ],
    timeout: TimeSpan.FromMinutes(10));
```

保留独特的 SQL Server 数据库、实际捆绑执行、迁移列表比较以及现有测试的精确清理。断言构建输出不包含 `http://` 或 `http://`。

添加一个仅创建测试拥有的无效套件目录的帮助程序：

```csharp
private static async Task<string> NewInvalidKitAsync(
    string repositoryRoot,
    Action<Dictionary<string, object?>> mutate)
{
    var path = Path.Combine(
        Path.GetTempPath(),
        "WebPassInvalidMigrationKits",
        Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(path, "tools"));
    Directory.CreateDirectory(Path.Combine(path, "packages"));
    Directory.CreateDirectory(Path.Combine(path, "feed"));
    await File.WriteAllTextAsync(
        Path.Combine(path, "NuGet.Config"),
        "<configuration><packageSources><clear />" +
        "<add key=\"WebPassOffline\" value=\"feed\" />" +
        "</packageSources></configuration>");
    var commit = (await MigrationOfflineKitFixture.RunAsync(
        "git",
        ["-C", repositoryRoot, "rev-parse", "HEAD"])).Output.Trim();
    var manifest = new Dictionary<string, object?>
    {
        ["formatVersion"] = 1,
        ["sourceCommit"] = commit,
        ["dotnetEfVersion"] = "10.0.0",
        ["sdkMajorVersion"] = 10,
        ["targetRuntime"] = "win-x64",
        ["createdAtUtc"] = DateTimeOffset.UtcNow,
    };
    mutate(manifest);
    await File.WriteAllTextAsync(
        Path.Combine(path, "manifest.json"),
        JsonSerializer.Serialize(manifest));
    return path;
}
```

添加测试名为：

```text
Offline_build_rejects_an_incomplete_kit_without_output
Offline_build_rejects_the_wrong_tool_version_without_output
Offline_build_rejects_a_source_commit_mismatch_without_output
Offline_build_fails_on_missing_packages_without_http_fallback
```

 前三个使用最小无效套件并断言非零进程退出、预期的稳定错误片段和 `File.Exists(output) == false`。

对于丢失包测试，仅将真实套件的 `tools` 目录复制到唯一的无效套件中，将 `tools` 和 `tools` 留空，编写有效清单和仅限本地配置，然后断言失败，无输出，并且在任一套件中都没有 `tools` 或 `tools`输出流。删除 `tools` 中每个确切的无效套件根。

使用此仅测试目录复制帮助程序，因此永远不会遵循连接或不相关的父路径：

```csharp
private static void CopyDirectory(string source, string destination)
{
    Directory.CreateDirectory(destination);
    foreach (var file in Directory.EnumerateFiles(source))
    {
        File.Copy(
            file,
            Path.Combine(destination, Path.GetFileName(file)));
    }
    foreach (var directory in Directory.EnumerateDirectories(source))
    {
        var attributes = File.GetAttributes(directory);
        Assert.False(attributes.HasFlag(FileAttributes.ReparsePoint));
        CopyDirectory(
            directory,
            Path.Combine(destination, Path.GetFileName(directory)));
    }
}
```

- [ ] **第 2 步：运行严格的离线测试并验证 RED**

运行：

```powershell
dotnet test tests\WebPass.IntegrationTests\WebPass.IntegrationTests.csproj `
  -c Release `
  --filter "FullyQualifiedName~MigrationBundleTests"
```

Expected：失败，因为当前构建脚本不接受 `-OfflineKitPath` 并且不验证清单。

- [ ] **步骤 3：将离线套件解析和验证添加到构建脚本**

扩展参数块：

```powershell
param(
    [string]$OutputPath,
    [string]$OfflineKitPath
)
```

添加常量。仅在 Offline-kit 分支内解析源提交，以便现有开发人员模式保留其当前行为：

```powershell
$DotnetEfVersion = '10.0.0'
$TargetRuntime = 'win-x64'
```

当提供 `-OfflineKitPath` 时，解析并验证：

```powershell
$kit = (Resolve-Path -LiteralPath $OfflineKitPath).Path
$manifestPath = Join-Path $kit 'manifest.json'
$configPath = Join-Path $kit 'NuGet.Config'
$efTool = Join-Path $kit 'tools\dotnet-ef.exe'
$packages = Join-Path $kit 'packages'
$feed = Join-Path $kit 'feed'

foreach ($file in @($manifestPath, $configPath, $efTool)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Offline migration kit file is missing: $file"
    }
}
foreach ($directory in @($packages, $feed)) {
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        throw "Offline migration kit directory is missing: $directory"
    }
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw |
    ConvertFrom-Json
$sourceCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[a-f0-9]{40}$') {
    throw 'The current source Git commit could not be determined.'
}
if ($manifest.formatVersion -ne 1) {
    throw 'Offline migration kit format version must be 1.'
}
if ($manifest.dotnetEfVersion -ne $DotnetEfVersion) {
    throw 'Offline migration kit dotnet-ef version must be 10.0.0.'
}
if ($manifest.sdkMajorVersion -ne 10 -or
    $manifest.targetRuntime -ne $TargetRuntime) {
    throw 'Offline migration kit SDK or target runtime does not match WebPass.'
}
if ($manifest.sourceCommit -ne $sourceCommit) {
    throw 'Offline migration kit source commit does not match the current source.'
}
```

捕获并验证工具版本：

```powershell
$toolVersionOutput = (& $efTool --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or
    $toolVersionOutput -notmatch '(^|\s)10\.0\.0($|\s)') {
    throw 'Offline migration kit dotnet-ef executable is not version 10.0.0.'
}
```

当不提供套件时，保留现有的`.tools\dotnet-ef.exe`验证不变。

- [ ] **步骤 4：实现进程范围的离线恢复和原子输出**

创建输出目录并在该目录中创建唯一的临时输出：

```powershell
$temporaryOutput = Join-Path $outputDirectory (
    '.' + [System.IO.Path]::GetFileName($resolvedOutputPath) +
    '.' + [Guid]::NewGuid().ToString('N') + '.tmp.exe')
```

离线模式下，在`finally`中保存和恢复这些变量：

```powershell
$oldPackages = $env:NUGET_PACKAGES
$oldRestoreSources = $env:RestoreSources
$oldAudit = $env:NuGetAudit
try {
    $env:NUGET_PACKAGES = $packages
    $env:RestoreSources = $feed
    $env:NuGetAudit = 'false'
    & dotnet restore $webProject `
        --runtime $TargetRuntime `
        --configfile $configPath `
        --packages $packages `
        --no-http-cache
    if ($LASTEXITCODE -ne 0) {
        throw "Offline restore failed with exit code $LASTEXITCODE."
    }

    & $efTool migrations bundle `
        --project $webProject `
        --startup-project $webProject `
        --configuration Release `
        --target-runtime $TargetRuntime `
        --output $temporaryOutput `
        --force
    if ($LASTEXITCODE -ne 0) {
        throw "Migration bundle build failed with exit code $LASTEXITCODE."
    }
}
finally {
    $env:NUGET_PACKAGES = $oldPackages
    $env:RestoreSources = $oldRestoreSources
    $env:NuGetAudit = $oldAudit
    if (Test-Path -LiteralPath $temporaryOutput -PathType Leaf) {
        Remove-Item -LiteralPath $temporaryOutput -Force
    }
}
```

 仅在检查生成的临时文件存在后才移动它：

```powershell
if (-not (Test-Path -LiteralPath $temporaryOutput -PathType Leaf)) {
    throw "Migration bundle was not created: $temporaryOutput"
}
Move-Item -LiteralPath $temporaryOutput `
    -Destination $resolvedOutputPath `
    -Force
$temporaryOutput = $null
Write-Output $resolvedOutputPath
```

在开发人员模式下，使用相同的临时输出策略，但不更改 NuGet 环境变量。 `finally` 清理首先检查 `finally` 是否为非空，然后仅在该文件仍然存在时删除该文件。

- [ ] **步骤 5：运行任务 2 测试并验证绿色**

运行步骤2命令。

预期：准备、拒绝、严格离线构建、实际 SQL 迁移和清理测试全部通过，零失败和跳过。

- [ ] **第 6 步：重新运行默认开发人员工作流程两次**

运行：

```powershell
.\scripts\Build-WebPassMigrationBundle.ps1
.\scripts\Build-WebPassMigrationBundle.ps1
$bundle = 'src\WebPass.Web\bin\Release\migrations\win-x64\WebPass.Migrations.exe'
if (-not (Test-Path -LiteralPath $bundle -PathType Leaf)) {
    throw "Missing bundle: $bundle"
}
git check-ignore -v -- $bundle
```

Expected：两个构建都成功，文件存在，`**/bin/` 忽略它。

- [ ] **步骤 7：审核并提交任务 2**

运行：

```powershell
git diff --check
git diff -- `
  scripts/Build-WebPassMigrationBundle.ps1 `
  tests/WebPass.IntegrationTests/Deployment/MigrationBundleTests.cs
git status --short
```

仅暂存两个任务 2 文件并提交：

```powershell
git add -- `
  scripts/Build-WebPassMigrationBundle.ps1 `
  tests/WebPass.IntegrationTests/Deployment/MigrationBundleTests.cs
git commit -m "feat: build migration bundle from offline kit"
```

停止并获得任务 3 的批准。

---

### 任务3：文档连接准备、离线构建和IIS部署

**检查点估计：** 2 个修改文件； 5k-8k 代币。

**文件：**
- 修改：`docs/deployment/windows-server-iis.md`
- 修改：`docs/deployment/windows-server-iis_ZH.md`

**接口：**
-  记录任务 1 和 2 生成的两个 PowerShell 界面。
- 保留现有的 IIS 初始化、管理员初始化、证书、验收检查、更新和回滚指导。

- [ ] **第1步：添加英文连接机器准备说明**

在第3节中，在网站发布命令之前添加：

```markdown
On an internet-connected Windows preparation machine, check out the reviewed
source commit and create the offline dependency kit:

```powershell
.\scripts\Prepare-WebPassMigrationOfflineKit.ps1 `   -OutputPath D:\WebPassTransfer\WebPassMigrationOfflineKit ` -强制
```

Transfer the entire `WebPassMigrationOfflineKit` directory to the offline
build machine through approved removable media or an internal file share. The
kit is build material, not a deployment artifact, and must not be copied to
the IIS server.
```

- [ ] **第2步：用严格的离线模式替换英文捆绑构建命令**

用途：

```powershell
.\scripts\Build-WebPassMigrationBundle.ps1 `
  -OfflineKitPath E:\WebPassMigrationOfflineKit `
  -OutputPath C:\WebPass\staging\WebPass.Migrations.exe
```

 明确声明源签出必须在 `manifest.json` 中的提交处进行，任何缺少的依赖项都会停止部署，并且离线构建计算机不需要访问 HTTP NuGet 源。

保留现有的捆绑包执行命令和数据库权限指导。添加IIS/部署服务器既不需要.NET SDK也不需要`dotnet-ef`；它保留了现有的 .NET 10 Hosting Bundle 要求。

- [ ] **第 3 步：应用等效的简体中文说明**

将`windows-server-iis_ZH.md`中对应的部分写为UTF-8：

```markdown
在可联网的 Windows 准备机上检出已审核的源代码提交，并制作离线依赖包：

```powershell
.\scripts\Prepare-WebPassMigrationOfflineKit.ps1 `   -OutputPath D:\WebPassTransfer\WebPassMigrationOfflineKit ` -强制
```

通过批准的移动介质或内网文件共享，将整个
`WebPassMigrationOfflineKit` 目录复制到离线构建机。该目录属于构建材料，
不是部署产物，不要将其复制到 IIS 服务器。
```

使用与英文文档相同的严格离线构建命令。声明源提交必须与清单匹配，缺少包会终止构建，构建计算机不会访问 HTTP NuGet 源，并且 IIS 服务器既不需要 SDK，也不需要 `dotnet-ef`。

- [ ] **步骤 4：验证两个文档保持同步**

运行：

```powershell
$documents = @(
  'docs\deployment\windows-server-iis.md',
  'docs\deployment\windows-server-iis_ZH.md')
foreach ($document in $documents) {
    $content = Get-Content -LiteralPath $document -Raw -Encoding utf8
    foreach ($required in @(
        'Prepare-WebPassMigrationOfflineKit.ps1',
        'Build-WebPassMigrationBundle.ps1',
        '-OfflineKitPath',
        'WebPass.Migrations.exe')) {
        if (-not $content.Contains($required)) {
            throw "$document is missing $required"
        }
    }
}
```

预期：退出零。目视确认两个文档保留顺序：连接准备、传输、离线构建、捆绑执行、最小数据库权限。

- [ ] **步骤 5：审核并提交任务 3**

运行：

```powershell
git diff --check
git diff -- `
  docs/deployment/windows-server-iis.md `
  docs/deployment/windows-server-iis_ZH.md
git status --short
```

阶段并提交：

```powershell
git add -- `
  docs/deployment/windows-server-iis.md `
  docs/deployment/windows-server-iis_ZH.md
git commit -m "docs: describe offline migration bundle deployment"
```

停止并获得任务 4 的批准。

---

### 任务4：最终回归和离线部署验证

**检查点估计：**没有计划的源更改； 6k-10k 代币。

**文件：**
- 仅验证；除非已验证的缺陷需要返回其所属任务并重复红色/绿色，否则不要修改任何文件。

**接口：**
-  使用任务 1-3 的所有可交付成果。
- 产生最终验证证据和干净的实施差异。

- [ ] **第 1 步：生成新套件并离线构建捆绑包两次**

运行：

```powershell
$kit = 'artifacts\WebPassMigrationOfflineKit'
$bundle = 'artifacts\WebPass.Migrations.exe'
.\scripts\Prepare-WebPassMigrationOfflineKit.ps1 `
  -OutputPath $kit `
  -Force
.\scripts\Build-WebPassMigrationBundle.ps1 `
  -OfflineKitPath $kit `
  -OutputPath $bundle
.\scripts\Build-WebPassMigrationBundle.ps1 `
  -OfflineKitPath $kit `
  -OutputPath $bundle
if (-not (Test-Path -LiteralPath $bundle -PathType Leaf)) {
    throw "Missing bundle: $bundle"
}
```

Expected：使用本地源进行准备并且两个构建都成功，并且第二个构建安全地替换了第一个输出。

- [ ] **步骤 2：运行重点部署集成测试**

运行：

```powershell
$env:WEBPASS_MIGRATION_OFFLINE_KIT = (
  Resolve-Path -LiteralPath $kit).Path
try {
    dotnet test `
      tests\WebPass.IntegrationTests\WebPass.IntegrationTests.csproj `
      -c Release `
      --filter FullyQualifiedName~Deployment
}
finally {
    Remove-Item Env:WEBPASS_MIGRATION_OFFLINE_KIT `
      -ErrorAction SilentlyContinue
}
```

预期：所有部署测试均以零失败和跳过的方式通过，包括针对 SQL Server 的实际捆绑执行。

- [ ] **步骤 3：验证 EF 模型稳定性**

运行：

```powershell
.\.tools\dotnet-ef.exe migrations has-pending-model-changes `
  --project src\WebPass.Web\WebPass.Web.csproj `
  --startup-project src\WebPass.Web\WebPass.Web.csproj `
  --configuration Release `
  --no-build
```

预计：

```text
No changes have been made to the model since the last migration.
```

- [ ] **步骤 4：运行完整的发布解决方案**

运行：

```powershell
$env:WEBPASS_MIGRATION_OFFLINE_KIT = (
  Resolve-Path -LiteralPath $kit).Path
try {
    dotnet test WebPass.sln -c Release
}
finally {
    Remove-Item Env:WEBPASS_MIGRATION_OFFLINE_KIT `
      -ErrorAction SilentlyContinue
}
```

预期：单元和集成项目以零失败和零跳过的方式通过。记录每个项目的确切计数和总计数。

- [ ] **步骤 5：审核范围、生成的工件和存储库状态**

运行：

```powershell
git diff --check
git status --short
git log --oneline -4
git diff --name-only b698999
git ls-files -- `
  '*.nupkg' `
  '*WebPass.Migrations.exe' `
  'artifacts/**' `
  '.tools/**'
git diff --name-only b698999 -- `
  'src/WebPass.Web/Data/**' `
  'src/WebPass.Web/Migrations/**' `
  'src/WebPass.Web/Pages/**' `
  'src/WebPass.Web/wwwroot/**'
```

确认：

- 设计提交`b698999`后，仅此计划中列出的脚本、部署测试和两个部署文档发生了变化；
- 没有跟踪离线工具包、程序包、工具二进制文件、缓存或生成的捆绑包；
- 没有更改 WebPass 运行时、UI、实体、DbContext、快照或迁移文件；
- 工作树是干净的。

- [ ] **第6步：完成分支**

 在做出任何完成声明之前使用 `superpowers:verification-before-completion`。然后使用 `superpowers:verification-before-completion` 检测基础分支并提供本地合并、推送/PR 或保持原样。未经用户明确选择，请勿合并、推送、删除分支或删除工作树。

## 预期提交顺序

1. `feat: prepare offline migration dependency kit`
2. `feat: build migration bundle from offline kit`
3. `docs: describe offline migration bundle deployment`

批准的设计和实施计划在实施提交之前仍然是单独提交的。
