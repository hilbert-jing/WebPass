using System.Net;
using System.Net.NetworkInformation;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using WebPass.Web.Application.Assets;
using WebPass.Web.Application.Authorization;
using WebPass.Web.Application.Ping;
using WebPass.Web.Configuration;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Domain.Enums;
using WebPass.Web.Infrastructure.Auditing;
using WebPass.Web.Infrastructure.Authorization;
using Xunit;

namespace WebPass.IntegrationTests.Assets;

public sealed class AssetAndPingTests
{
    [Fact]
    public async Task List_orders_10_0_0_9_before_10_0_0_10()
    {
        await using var db = NewDatabase();
        var actor = await AddUserAsync(db, PermissionCode.AssetCreate);
        var assets = NewAssetService(db);
        await AddEnabledSubnetAsync(db, "10.0.0.0/24");
        await assets.CreateAsync(Input("10.0.0.10"), actor.Id, default);
        await assets.CreateAsync(Input("10.0.0.9"), actor.Id, default);

        var page = await assets.ListAsync(new ServerListQuery(), default);

        Assert.Equal(new[] { "10.0.0.9", "10.0.0.10" }, page.Items.Select(x => x.BusinessIp));
    }

    [Theory]
    [InlineData("10.0.1.1")]
    [InlineData("10.0.0.0")]
    [InlineData("10.0.0.255")]
    public async Task Create_rejects_addresses_outside_or_not_usable_in_an_enabled_subnet(string address)
    {
        await using var db = NewDatabase();
        var actor = await AddUserAsync(db, PermissionCode.AssetCreate);
        var assets = NewAssetService(db);
        await AddEnabledSubnetAsync(db, "10.0.0.0/24");

        await Assert.ThrowsAsync<InvalidOperationException>(() => assets.CreateAsync(Input(address), actor.Id, default));

        Assert.Empty(db.ServerAssets);
    }

    [Fact]
    public async Task Create_rejects_an_active_duplicate_but_archive_allows_re_registration()
    {
        await using var db = NewDatabase();
        var actor = await AddUserAsync(db, PermissionCode.AssetCreate, PermissionCode.AssetArchive);
        var assets = NewAssetService(db);
        await AddEnabledSubnetAsync(db, "10.0.0.0/24");
        var first = await assets.CreateAsync(Input("10.0.0.9"), actor.Id, default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => assets.CreateAsync(Input("10.0.0.9"), actor.Id, default));
        await assets.ArchiveAsync(first.Id, first.RowVersion, actor.Id, default);
        var replacement = await assets.CreateAsync(Input("10.0.0.9"), actor.Id, default);

        Assert.NotEqual(first.Id, replacement.Id);
        Assert.True((await db.ServerAssets.FindAsync(first.Id))!.IsArchived);
    }

    [Fact]
    public async Task Update_with_a_stale_row_version_does_not_overwrite_current_data()
    {
        await using var db = NewDatabase();
        var actor = await AddUserAsync(db, PermissionCode.AssetCreate, PermissionCode.AssetEdit);
        var assets = NewAssetService(db);
        await AddEnabledSubnetAsync(db, "10.0.0.0/24");
        var asset = await assets.CreateAsync(Input("10.0.0.9", location: "Original"), actor.Id, default);
        asset.RowVersion = [1];
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<ServerAssetConcurrencyException>(() => assets.UpdateAsync(asset.Id, Input("10.0.0.9", location: "Changed"), [9], actor.Id, default));

        Assert.Equal("Original", (await db.ServerAssets.AsNoTracking().SingleAsync()) .Location);
    }

    [Fact]
    public async Task Pool_view_overlays_registered_assets_without_persisting_free_addresses()
    {
        await using var db = NewDatabase();
        var actor = await AddUserAsync(db, PermissionCode.AssetCreate);
        var assets = NewAssetService(db);
        var subnet = await AddEnabledSubnetAsync(db, "10.0.0.0/29");
        var registered = await assets.CreateAsync(Input("10.0.0.2"), actor.Id, default);

        var page = await assets.ListAsync(new ServerListQuery(SubnetId: subnet.Id, PoolMode: true, Skip: 0, Take: 6), default);

        Assert.Equal(new[] { "10.0.0.1", "10.0.0.2", "10.0.0.3", "10.0.0.4", "10.0.0.5", "10.0.0.6" }, page.Items.Select(x => x.BusinessIp));
        Assert.Equal(registered.Id, page.Items.Single(x => x.BusinessIp == "10.0.0.2").AssetId);
        Assert.Null(page.Items.Single(x => x.BusinessIp == "10.0.0.1").AssetId);
        Assert.Single(db.ServerAssets);
    }

    [Fact]
    public async Task Commands_are_denied_when_the_actor_lacks_their_respective_permission()
    {
        await using var db = NewDatabase();
        var denied = await AddUserAsync(db);
        var allowed = await AddUserAsync(db, PermissionCode.AssetCreate);
        var assets = NewAssetService(db);
        var ping = NewPingService(db, new SuccessfulPingTransport());
        await AddEnabledSubnetAsync(db, "10.0.0.0/24");
        var asset = await assets.CreateAsync(Input("10.0.0.9"), allowed.Id, default);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => assets.CreateAsync(Input("10.0.0.10"), denied.Id, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => assets.UpdateAsync(asset.Id, Input("10.0.0.9", location: "Changed"), asset.RowVersion, denied.Id, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => assets.ArchiveAsync(asset.Id, asset.RowVersion, denied.Id, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ping.ExecuteAsync(asset.Id, denied.Id, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ping.MarkAliveAsync(asset.Id, denied.Id, asset.RowVersion, default));
    }

    [Fact]
    public async Task Ping_success_does_not_change_manual_status_and_is_audited()
    {
        await using var db = NewDatabase();
        var actor = await AddUserAsync(db, PermissionCode.AssetCreate, PermissionCode.PingExecute);
        var assets = NewAssetService(db);
        var ping = NewPingService(db, new SuccessfulPingTransport());
        await AddEnabledSubnetAsync(db, "10.0.0.0/24");
        var asset = await assets.CreateAsync(Input("10.0.0.9", status: AliveStatus.Unknown), actor.Id, default);

        var result = await ping.ExecuteAsync(asset.Id, actor.Id, default);

        Assert.Equal("Success", result.Outcome);
        Assert.Equal(AliveStatus.Unknown, (await db.ServerAssets.FindAsync(asset.Id))!.AliveStatus);
        Assert.Contains(db.AuditLogs, x => x.Action == "PingExecute" && x.ObjectId == asset.Id.ToString());
    }

    [Fact]
    public async Task Ping_rejects_an_asset_no_longer_in_an_enabled_subnet_and_per_user_rate_limit()
    {
        await using var db = NewDatabase();
        var actor = await AddUserAsync(db, PermissionCode.AssetCreate, PermissionCode.PingExecute);
        var assets = NewAssetService(db);
        var ping = NewPingService(db, new SuccessfulPingTransport(), perUserPerMinute: 1);
        var subnet = await AddEnabledSubnetAsync(db, "10.0.0.0/24");
        var asset = await assets.CreateAsync(Input("10.0.0.9"), actor.Id, default);

        await ping.ExecuteAsync(asset.Id, actor.Id, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ping.ExecuteAsync(asset.Id, actor.Id, default));
        subnet.IsEnabled = false;
        await db.SaveChangesAsync();
        var anotherActor = await AddUserAsync(db, PermissionCode.PingExecute);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ping.ExecuteAsync(asset.Id, anotherActor.Id, default));
    }

    [Fact]
    public async Task Mark_alive_changes_only_manual_status_and_writes_an_audit_entry()
    {
        await using var db = NewDatabase();
        var actor = await AddUserAsync(db, PermissionCode.AssetCreate, PermissionCode.StatusMarkAlive);
        var assets = NewAssetService(db);
        var ping = NewPingService(db, new SuccessfulPingTransport());
        await AddEnabledSubnetAsync(db, "10.0.0.0/24");
        var asset = await assets.CreateAsync(Input("10.0.0.9", status: AliveStatus.Fault), actor.Id, default);
        asset.RowVersion = [1];
        await db.SaveChangesAsync();

        await ping.MarkAliveAsync(asset.Id, actor.Id, [1], default);

        Assert.Equal(AliveStatus.Alive, (await db.ServerAssets.FindAsync(asset.Id))!.AliveStatus);
        Assert.Contains(db.AuditLogs, x => x.Action == "StatusMarkAlive" && x.ObjectId == asset.Id.ToString());
    }

    [Fact]
    public async Task Asset_mutations_write_redacted_audit_records()
    {
        await using var db = NewDatabase();
        var actor = await AddUserAsync(db, PermissionCode.AssetCreate, PermissionCode.AssetEdit, PermissionCode.AssetArchive);
        var assets = NewAssetService(db);
        await AddEnabledSubnetAsync(db, "10.0.0.0/24");
        var asset = await assets.CreateAsync(Input("10.0.0.9"), actor.Id, default);
        asset.RowVersion = [1];
        await db.SaveChangesAsync();
        await assets.UpdateAsync(asset.Id, Input("10.0.0.9", location: "Changed"), [1], actor.Id, default);
        var current = await db.ServerAssets.AsNoTracking().SingleAsync();
        await assets.ArchiveAsync(current.Id, current.RowVersion, actor.Id, default);

        var actions = db.AuditLogs.Select(x => x.Action).ToArray();
        Assert.Contains("AssetCreate", actions);
        Assert.Contains("AssetEdit", actions);
        Assert.Contains("AssetArchive", actions);
        Assert.All(db.AuditLogs, x => Assert.DoesNotContain("password", x.Details ?? string.Empty, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Authenticated_server_create_post_requires_antiforgery_and_reaches_the_service()
    {
        using var factory = new ServerPageFactory(NewUser(), PermissionCode.AssetView, PermissionCode.AssetCreate);
        factory.InitializeData();
        await factory.AddSubnetAsync();
        using var client = factory.CreateAuthenticatedClient();
        var html = await client.GetStringAsync("/servers");
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token));

        var response = await client.PostAsync("/servers?handler=Create", new FormUrlEncodedContent(
        [new("Input.BusinessIp", "10.0.0.9"), new("Input.Location", "HQ"), new("Input.AliveStatus", "Unknown"),
         new("Input.ComputerName", "web-09"), new("Input.SystemName", "Web"), new("Input.OperatingSystemVersion", ""),
         new("Input.DatabaseVersion", ""), new("Input.Notes", ""), new("__RequestVerificationToken", token)]));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("10.0.0.9", (await factory.GetAssetsAsync()).Single().BusinessIp);
    }

    [Fact]
    public async Task Navigation_shows_only_links_allowed_for_the_current_user()
    {
        using var factory = new ServerPageFactory(NewUser(), PermissionCode.AssetView);
        factory.InitializeData();
        using var client = factory.CreateAuthenticatedClient();

        var html = await client.GetStringAsync("/servers");

        Assert.Contains("href=\"/servers\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/audit\"", html, StringComparison.Ordinal);
    }
    [Fact]
    public async Task Pool_large_cidr_returns_only_the_requested_page_without_overflow()
    {
        await using var db = NewDatabase();
        await AddEnabledSubnetAsync(db, "0.0.0.0/0");
        var service = NewAssetService(db);

        var page = await service.ListAsync(new ServerListQuery(PoolMode: true, Skip: 100, Take: 50), default);

        Assert.Equal(4294967294L, page.TotalCount);
        Assert.Equal(50, page.Items.Count);
        Assert.Equal("0.0.0.101", page.Items[0].BusinessIp);
    }

    [Fact]
    public async Task Pool_include_archived_overlays_archived_asset_and_default_pool_leaves_it_free()
    {
        await using var db = NewDatabase();
        var actor = await AddUserAsync(db, PermissionCode.AssetCreate, PermissionCode.AssetArchive);
        var assets = NewAssetService(db);
        var subnet = await AddEnabledSubnetAsync(db, "10.0.0.0/29");
        var asset = await assets.CreateAsync(Input("10.0.0.2"), actor.Id, default);
        await assets.ArchiveAsync(asset.Id, asset.RowVersion, actor.Id, default);

        var defaultPool = await assets.ListAsync(new ServerListQuery(SubnetId: subnet.Id, PoolMode: true), default);
        var archivePool = await assets.ListAsync(new ServerListQuery(SubnetId: subnet.Id, PoolMode: true, IncludeArchived: true), default);

        Assert.Null(defaultPool.Items.Single(x => x.BusinessIp == "10.0.0.2").AssetId);
        var archived = archivePool.Items.Single(x => x.BusinessIp == "10.0.0.2");
        Assert.Equal(asset.Id, archived.AssetId);
        Assert.True(archived.IsArchived);
    }

    [Fact]
    public async Task Pool_free_row_links_to_prefilled_create_form_and_post_creates_asset()
    {
        using var factory = new ServerPageFactory(NewUser(), PermissionCode.AssetView, PermissionCode.AssetCreate);
        factory.InitializeData();
        await factory.AddSubnetAsync();
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/servers?Query.PoolMode=true&Query.SubnetId=" + (await factory.GetOnlySubnetAsync()).Id);
        var poolHtml = await response.Content.ReadAsStringAsync();
        Assert.Contains("Input.BusinessIp=10.0.0.1", poolHtml, StringComparison.Ordinal);

        var createHtml = await client.GetStringAsync("/servers?Input.BusinessIp=10.0.0.1");
        Assert.Contains("value=\"10.0.0.1\"", createHtml, StringComparison.Ordinal);
        var token = Regex.Match(createHtml, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var post = await client.PostAsync("/servers?handler=Create", new FormUrlEncodedContent(
        [new("Input.BusinessIp", "10.0.0.1"), new("Input.Location", "HQ"), new("Input.AliveStatus", "Unknown"),
         new("Input.ComputerName", "web-01"), new("Input.SystemName", "Web"), new("__RequestVerificationToken", token)]));

        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Equal("10.0.0.1", (await factory.GetAssetsAsync()).Single().BusinessIp);
    }

    [Fact]
    public async Task Edit_conflict_renders_current_database_values_not_stale_posted_values()
    {
        using var factory = new ServerPageFactory(NewUser(), PermissionCode.AssetView, PermissionCode.AssetEdit);
        factory.InitializeData();
        await factory.AddSubnetAsync();
        var asset = await factory.AddAssetAsync("10.0.0.9", "Current", [1]);
        using var client = factory.CreateAuthenticatedClient();
        var html = await client.GetStringAsync($"/servers/{asset.Id}/edit");
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

        var response = await client.PostAsync($"/servers/{asset.Id}/edit", new FormUrlEncodedContent(
        [new("id", asset.Id.ToString()), new("rowVersion", Convert.ToBase64String([9])),
         new("Input.BusinessIp", "10.0.0.9"), new("Input.Location", "Stale"), new("Input.AliveStatus", "Unknown"),
         new("Input.ComputerName", "stale"), new("Input.SystemName", "Stale"), new("__RequestVerificationToken", token)]));
        var conflictHtml = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Current", conflictHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("value=\"Stale\"", conflictHtml, StringComparison.Ordinal);
        Assert.Contains("changed by another user", conflictHtml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ping_global_concurrency_and_normalized_outcomes_are_enforced()
    {
        await using var db = NewDatabase();
        var actor = await AddUserAsync(db, PermissionCode.AssetCreate, PermissionCode.PingExecute);
        await AddEnabledSubnetAsync(db, "10.0.0.0/24");
        var assets = NewAssetService(db);
        var first = await assets.CreateAsync(Input("10.0.0.9"), actor.Id, default);
        var second = await assets.CreateAsync(Input("10.0.0.10"), actor.Id, default);
        var transport = new TrackingPingTransport("UnknownOutcome");
        var options = Options.Create(new WebPassOptions { PingTimeoutMilliseconds = 1000, PingMaxConcurrency = 1, PingPerUserPerMinute = 5 });
        var ping = new PingService(db, new PermissionAuthorizationHandler(db), new AuditWriter(db), transport, options);

        await Task.WhenAll(ping.ExecuteAsync(first.Id, actor.Id, default), ping.ExecuteAsync(second.Id, actor.Id, default));

        Assert.Equal(1, transport.MaximumConcurrent);
        Assert.All(await db.PingResults.ToListAsync(), result => Assert.Equal("InternalError", result.Outcome));
    }

    [Theory]
    [InlineData("Timeout")]
    [InlineData("Unreachable")]
    [InlineData("Success")]
    public async Task Ping_persists_normalized_transport_outcomes(string outcome)
    {
        await using var db = NewDatabase();
        var actor = await AddUserAsync(db, PermissionCode.AssetCreate, PermissionCode.PingExecute);
        await AddEnabledSubnetAsync(db, "10.0.0.0/24");
        var asset = await NewAssetService(db).CreateAsync(Input("10.0.0.9"), actor.Id, default);
        var options = Options.Create(new WebPassOptions { PingTimeoutMilliseconds = 1000, PingMaxConcurrency = 2, PingPerUserPerMinute = 5 });
        var ping = new PingService(db, new PermissionAuthorizationHandler(db), new AuditWriter(db), new TrackingPingTransport(outcome), options);

        await ping.ExecuteAsync(asset.Id, actor.Id, default);

        Assert.Equal(outcome, (await db.PingResults.SingleAsync()).Outcome);
    }

    [Fact]
    public async Task Filtered_zero_cidr_pool_uses_bounded_backend_query_without_enumerating_addresses()
    {
        await using var db = NewDatabase();
        await AddEnabledSubnetAsync(db, "0.0.0.0/0");
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var page = await NewAssetService(db).ListAsync(new ServerListQuery(PoolMode: true, Search: "not-a-real-server", Take: 50), default);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
    }
    [Fact]
    public async Task Unknown_status_pool_includes_free_rows_with_long_total_and_paging()
    {
        await using var db = NewDatabase();
        var actor = await AddUserAsync(db, PermissionCode.AssetCreate);
        var assets = NewAssetService(db);
        var subnet = await AddEnabledSubnetAsync(db, "10.0.0.0/29");
        await assets.CreateAsync(Input("10.0.0.2", status: AliveStatus.Fault), actor.Id, default);
        var unknown = await assets.CreateAsync(Input("10.0.0.3", status: AliveStatus.Unknown), actor.Id, default);

        var page = await assets.ListAsync(new ServerListQuery(SubnetId: subnet.Id, PoolMode: true, Status: AliveStatus.Unknown, Skip: 0, Take: 6), default);

        Assert.Equal(5, page.TotalCount);
        Assert.Equal(new[] { "10.0.0.1", "10.0.0.3", "10.0.0.4", "10.0.0.5", "10.0.0.6" }, page.Items.Select(x => x.BusinessIp));
        Assert.Equal(unknown.Id, page.Items.Single(x => x.BusinessIp == "10.0.0.3").AssetId);
    }

    [Fact]
    public async Task Broad_ip_search_pool_returns_free_rows_across_selected_cidrs_without_scanning_zero_cidr()
    {
        await using var db = NewDatabase();
        await AddEnabledSubnetAsync(db, "10.0.0.0/29");
        await AddEnabledSubnetAsync(db, "10.0.1.0/29");

        var page = await NewAssetService(db).ListAsync(new ServerListQuery(PoolMode: true, Search: "10.0.1.", Skip: 1, Take: 3), default);

        Assert.Equal(6, page.TotalCount);
        Assert.Equal(new[] { "10.0.1.2", "10.0.1.3", "10.0.1.4" }, page.Items.Select(x => x.BusinessIp));
        Assert.All(page.Items, x => Assert.Null(x.AssetId));
    }

    [Fact]
    public async Task Filtered_pool_include_archived_prefers_active_registration_over_archived_match()
    {
        await using var db = NewDatabase();
        var actor = await AddUserAsync(db, PermissionCode.AssetCreate, PermissionCode.AssetArchive);
        var assets = NewAssetService(db);
        var subnet = await AddEnabledSubnetAsync(db, "10.0.0.0/29");
        var archived = await assets.CreateAsync(Input("10.0.0.2", status: AliveStatus.Unknown), actor.Id, default);
        await assets.ArchiveAsync(archived.Id, archived.RowVersion, actor.Id, default);
        var active = await assets.CreateAsync(Input("10.0.0.2", status: AliveStatus.Fault), actor.Id, default);

        var page = await assets.ListAsync(new ServerListQuery(SubnetId: subnet.Id, PoolMode: true, IncludeArchived: true, Status: AliveStatus.Unknown), default);

        Assert.DoesNotContain(page.Items, x => x.BusinessIp == active.BusinessIp);
    }
    private static ServerAssetInput Input(string ip, string location = "HQ", AliveStatus status = AliveStatus.Unknown) =>
        new(ip, location, status, "server", "WebPass", null, null, "normal metadata");

    private static ServerAssetService NewAssetService(WebPassDbContext db) => new(db, new PermissionAuthorizationHandler(db), new AuditWriter(db));

    private static PingService NewPingService(WebPassDbContext db, IPingTransport transport, int perUserPerMinute = 5) => new(
        db, new PermissionAuthorizationHandler(db), new AuditWriter(db), transport,
        Options.Create(new WebPassOptions { PingTimeoutMilliseconds = 1000, PingMaxConcurrency = 2, PingPerUserPerMinute = perUserPerMinute }));

    private static WebPassDbContext NewDatabase() => new(new DbContextOptionsBuilder<WebPassDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<AppUser> AddUserAsync(WebPassDbContext db, params string[] permissions)
    {
        var user = NewUser();
        db.Users.Add(user);
        db.UserPermissions.AddRange(permissions.Select(code => new UserPermission { UserId = user.Id, PermissionCode = code }));
        await db.SaveChangesAsync();
        return user;
    }

    private static AppUser NewUser() => new() { Username = Guid.NewGuid().ToString("N"), PasswordHash = "hash" };

    private static async Task<Subnet> AddEnabledSubnetAsync(WebPassDbContext db, string cidr)
    {
        var split = cidr.Split('/');
        var subnet = new Subnet { Name = cidr, Cidr = cidr, NetworkAddress = split[0], PrefixLength = int.Parse(split[1]), Location = "HQ", IsEnabled = true };
        db.Subnets.Add(subnet);
        await db.SaveChangesAsync();
        return subnet;
    }

    private sealed class SuccessfulPingTransport : IPingTransport
    {
        public Task<PingTransportResult> SendAsync(string targetIp, int timeoutMilliseconds, CancellationToken ct) =>
            Task.FromResult(new PingTransportResult("Success", 12, null));
    }

    private sealed class TrackingPingTransport(string outcome) : IPingTransport
    {
        private int _current;
        public int MaximumConcurrent { get; private set; }

        public async Task<PingTransportResult> SendAsync(string targetIp, int timeoutMilliseconds, CancellationToken ct)
        {
            var current = Interlocked.Increment(ref _current);
            MaximumConcurrent = Math.Max(MaximumConcurrent, current);
            try
            {
                await Task.Delay(25, ct);
                return new PingTransportResult(outcome, outcome == "Success" ? 1 : null, null);
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }
        }
    }
    private sealed class ServerPageFactory(AppUser user, params string[] permissions) : WebApplicationFactory<Program>
    {
        private readonly string _databaseName = Guid.NewGuid().ToString("N");
        private readonly string[] _permissions = permissions;

        public void InitializeData()
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WebPassDbContext>();
            db.Users.Add(user);
            db.UserPermissions.AddRange(_permissions.Select(code => new UserPermission { UserId = user.Id, PermissionCode = code }));
            db.SaveChanges();
        }
        public async Task<Subnet> GetOnlySubnetAsync()
        {
            using var scope = Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<WebPassDbContext>().Subnets.SingleAsync();
        }

        public async Task<ServerAsset> AddAssetAsync(string ip, string location, byte[] rowVersion)
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WebPassDbContext>();
            var subnet = await db.Subnets.SingleAsync();
            var asset = new ServerAsset
            {
                SubnetId = subnet.Id,
                BusinessIp = ip,
                BusinessIpNumber = 167772169,
                Location = location,
                ComputerName = "current",
                SystemName = "Current",
                RowVersion = rowVersion,
            };
            db.ServerAssets.Add(asset);
            await db.SaveChangesAsync();
            return asset;
        }

        public HttpClient CreateAuthenticatedClient()
        {
            using var scope = Services.CreateScope();
            var cookieOptions = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], CookieAuthenticationDefaults.AuthenticationScheme);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), CookieAuthenticationDefaults.AuthenticationScheme);
            var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            client.DefaultRequestHeaders.Add("Cookie", $"{cookieOptions.Cookie.Name}={cookieOptions.TicketDataFormat.Protect(ticket)}");
            return client;
        }

        public async Task AddSubnetAsync()
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WebPassDbContext>();
            db.Subnets.Add(new Subnet { Name = "Operations", Cidr = "10.0.0.0/24", NetworkAddress = "10.0.0.0", PrefixLength = 24, Location = "HQ" });
            await db.SaveChangesAsync();
        }

        public async Task<List<ServerAsset>> GetAssetsAsync()
        {
            using var scope = Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<WebPassDbContext>().ServerAssets.AsNoTracking().ToListAsync();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<WebPassDbContext>>();
            services.RemoveAll<WebPassDbContext>();
            services.RemoveAll<IDbContextOptionsConfiguration<WebPassDbContext>>();
            services.AddDbContext<WebPassDbContext>(options => options.UseInMemoryDatabase(_databaseName));
        });
    }
}
