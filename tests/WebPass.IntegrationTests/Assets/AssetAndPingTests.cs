using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using WebPass.Web.Application.Assets;
using WebPass.Web.Application.Authorization;
using WebPass.Web.Application.Ping;
using WebPass.Web.Application.Secrets;
using WebPass.Web.Configuration;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Domain.Enums;
using WebPass.Web.Infrastructure.Auditing;
using WebPass.Web.Infrastructure.Authorization;
using WebPass.Web.Pages;
using Xunit;

namespace WebPass.IntegrationTests.Assets;

public sealed class AssetAndPingTests
{
    private const string StoredUserPasswordHash =
        "stored-user-password-hash-7e42d9";
    private static readonly byte[] StoredSecretCiphertext =
        [31, 41, 59, 26, 53, 58];

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
    public async Task Server_create_validation_uses_chinese_required_messages()
    {
        using var factory = new ServerPageFactory(NewUser(), PermissionCode.AssetView, PermissionCode.AssetCreate);
        factory.InitializeData();
        using var client = factory.CreateAuthenticatedClient();
        var html = await client.GetStringAsync("/servers");
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

        var response = await client.PostAsync("/servers?handler=Create", new FormUrlEncodedContent(
            [new("Input.BusinessIp", ""), new("Input.Location", ""), new("Input.ComputerName", ""),
             new("Input.SystemName", ""), new("__RequestVerificationToken", token)]));
        var responseHtml = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(">请输入业务 IP。</span>", responseHtml, StringComparison.Ordinal);
        Assert.Contains(">请输入位置。</span>", responseHtml, StringComparison.Ordinal);
        Assert.Contains(">请输入计算机名。</span>", responseHtml, StringComparison.Ordinal);
        Assert.Contains(">请输入系统名称。</span>", responseHtml, StringComparison.Ordinal);
        Assert.Contains("data-open", responseHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Server_create_service_error_uses_safe_message_without_internal_details()
    {
        using var factory = new ServerPageFactory(NewUser(), PermissionCode.AssetView, PermissionCode.AssetCreate);
        factory.InitializeData();
        await factory.AddSubnetAsync();
        await factory.AddAssetAsync("10.0.0.9", "HQ", [1]);
        using var client = factory.CreateAuthenticatedClient();
        var html = await client.GetStringAsync("/servers");
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

        var response = await client.PostAsync("/servers?handler=Create", new FormUrlEncodedContent(
            [new("Input.BusinessIp", "10.0.0.9"), new("Input.Location", "HQ"), new("Input.AliveStatus", "Unknown"),
             new("Input.ComputerName", "web-09"), new("Input.SystemName", "Web"), new("__RequestVerificationToken", token)]));
        var responseHtml = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("无法登记服务器：请检查 IP、网段和必填信息。", responseHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("An active server asset already uses this business IP.", responseHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Successful_ping_redirect_displays_localized_status_message()
    {
        using var factory = new ServerPageFactory(
            NewUser(),
            PermissionCode.AssetView,
            PermissionCode.PingExecute);
        factory.InitializeData();
        await factory.AddSubnetAsync();
        var asset = await factory.AddAssetAsync("10.0.0.9", "HQ", [1]);
        using var client = factory.CreateAuthenticatedClient();
        var html = await client.GetStringAsync("/servers");
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

        var response = await client.PostAsync("/servers?handler=Ping", new FormUrlEncodedContent(
            [new("id", asset.Id.ToString()), new("__RequestVerificationToken", token)]));
        var responseHtml = WebUtility.HtmlDecode(
            await client.GetStringAsync(response.Headers.Location!));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("Ping 可达 · 12 ms", responseHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Direct_ping_route_rejects_get_with_method_not_allowed()
    {
        using var factory = new ServerPageFactory(
            NewUser(),
            PermissionCode.AssetView,
            PermissionCode.PingExecute);
        factory.InitializeData();
        await factory.AddSubnetAsync();
        var asset = await factory.AddAssetAsync("10.0.0.9", "HQ", [1]);
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync($"/servers/{asset.Id}/ping");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ping_feedback_is_scoped_to_the_target_row_and_success_alone_offers_mark_alive(
        bool useDirectRoute)
    {
        using var factory = new ServerPageFactory(
            NewUser(),
            PermissionCode.AssetView,
            PermissionCode.PingExecute,
            PermissionCode.StatusMarkAlive);
        factory.InitializeData();
        await factory.AddSubnetAsync();
        var target = await factory.AddAssetAsync(
            "10.0.0.9",
            "Target rack",
            [1],
            AliveStatus.Fault,
            "target-computer",
            "Target system");
        var other = await factory.AddAssetAsync(
            "10.0.0.10",
            "Other rack",
            [2],
            AliveStatus.Unknown,
            "other-computer",
            "Other system");
        using var client = factory.CreateAuthenticatedClient();
        var token = AntiforgeryToken(await client.GetStringAsync("/servers"));
        var endpoint = useDirectRoute
            ? $"/servers/{target.Id}/ping"
            : "/servers?handler=Ping";

        var response = await PostPingAsync(client, endpoint, target.Id, token);
        var responseHtml = WebUtility.HtmlDecode(
            await client.GetStringAsync(response.Headers.Location!));
        var targetRow = ServerRow(responseHtml, target.Id);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("data-ping-feedback", targetRow, StringComparison.Ordinal);
        Assert.Contains("Ping 可达 · 12 ms", targetRow, StringComparison.Ordinal);
        Assert.Contains(">标记为存活<", targetRow, StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"data-asset-id=\"{other.Id}\"",
            responseHtml,
            StringComparison.Ordinal);
        Assert.Equal(
            AliveStatus.Fault,
            (await factory.GetAssetsAsync()).Single(x => x.Id == target.Id).AliveStatus);
    }

    [Fact]
    public async Task Inventory_ping_from_second_page_redirects_to_the_target_row_with_feedback()
    {
        using var factory = new ServerPageFactory(
            NewUser(),
            PermissionCode.AssetView,
            PermissionCode.PingExecute);
        factory.InitializeData();
        await factory.AddSubnetAsync();
        var target = await AddPagedAssetsAsync(factory);
        using var client = factory.CreateAuthenticatedClient();
        var secondPage = await client.GetStringAsync(
            "/servers?Query.Skip=50&Query.Take=50");
        var token = AntiforgeryToken(secondPage);
        Assert.Contains(
            $"data-asset-id=\"{target.Id}\"",
            secondPage,
            StringComparison.Ordinal);

        var response = await PostPingAsync(
            client,
            "/servers?handler=Ping",
            target.Id,
            token);
        var responseHtml = WebUtility.HtmlDecode(
            await client.GetStringAsync(response.Headers.Location!));
        var targetRow = ServerRow(responseHtml, target.Id);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "Query.Search=10.0.0.51",
            Uri.UnescapeDataString(response.Headers.Location!.OriginalString),
            StringComparison.Ordinal);
        Assert.Contains("Ping 可达 · 12 ms", targetRow, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ping_follow_redirect_finds_target_excluded_by_inventory_filter_or_direct_default_page(
        bool useDirectRoute)
    {
        using var factory = new ServerPageFactory(
            NewUser(),
            PermissionCode.AssetView,
            PermissionCode.PingExecute);
        factory.InitializeData();
        await factory.AddSubnetAsync();
        var target = await AddPagedAssetsAsync(
            factory,
            targetStatus: AliveStatus.Fault);
        using var client = factory.CreateAuthenticatedClient();
        var token = AntiforgeryToken(await client.GetStringAsync("/servers"));
        var endpoint = useDirectRoute
            ? $"/servers/{target.Id}/ping"
            : "/servers?handler=Ping&Query.Status=Alive";

        var response = await PostPingAsync(
            client,
            endpoint,
            target.Id,
            token);
        var responseHtml = WebUtility.HtmlDecode(
            await client.GetStringAsync(response.Headers.Location!));
        var targetRow = ServerRow(responseHtml, target.Id);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "Query.Search=10.0.0.51",
            Uri.UnescapeDataString(response.Headers.Location!.OriginalString),
            StringComparison.Ordinal);
        Assert.Contains("Ping 可达 · 12 ms", targetRow, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Timeout", "Ping 超时 · 无延迟数据")]
    [InlineData("Unreachable", "Ping 不可达 · 无延迟数据")]
    [InlineData("UnexpectedOutcome", "Ping 检测失败 · 无延迟数据")]
    public async Task Non_successful_ping_feedback_has_no_mark_alive_action(
        string outcome,
        string expectedFeedback)
    {
        using var factory = new ServerPageFactory(
            NewUser(),
            PermissionCode.AssetView,
            PermissionCode.PingExecute,
            PermissionCode.StatusMarkAlive)
        {
            PingResponse = new PingTransportResult(outcome, null, "SafeCode"),
        };
        factory.InitializeData();
        await factory.AddSubnetAsync();
        var asset = await factory.AddAssetAsync("10.0.0.9", "HQ", [1]);
        using var client = factory.CreateAuthenticatedClient();
        var token = AntiforgeryToken(await client.GetStringAsync("/servers"));

        var response = await PostPingAsync(
            client,
            "/servers?handler=Ping",
            asset.Id,
            token);
        var responseHtml = WebUtility.HtmlDecode(
            await client.GetStringAsync(response.Headers.Location!));
        var row = ServerRow(responseHtml, asset.Id);

        Assert.Contains(expectedFeedback, row, StringComparison.Ordinal);
        Assert.DoesNotContain(">标记为存活<", row, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ping_invalid_target_returns_fixed_chinese_bad_request_without_internal_details(
        bool useDirectRoute)
    {
        using var factory = new ServerPageFactory(
            NewUser(),
            PermissionCode.AssetView,
            PermissionCode.PingExecute);
        factory.InitializeData();
        await factory.AddSubnetAsync();
        var asset = await factory.AddAssetAsync("10.0.0.9", "HQ", [1]);
        await factory.DisableSubnetAsync();
        using var client = factory.CreateAuthenticatedClient();
        var token = AntiforgeryToken(await client.GetStringAsync("/servers"));
        var endpoint = useDirectRoute
            ? $"/servers/{asset.Id}/ping"
            : "/servers?handler=Ping";

        var response = await PostPingAsync(client, endpoint, asset.Id, token);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("无法检测此服务器：目标无效或当前不可用。", body);
        Assert.DoesNotContain(
            "The Ping target is not a registered address in an enabled subnet.",
            body,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ping_rate_limit_returns_shared_safe_chinese_feedback(
        bool useDirectRoute)
    {
        using var factory = new ServerPageFactory(
            NewUser(),
            PermissionCode.AssetView,
            PermissionCode.PingExecute);
        factory.InitializeData();
        await factory.AddSubnetAsync();
        var asset = await factory.AddAssetAsync("10.0.0.9", "HQ", [1]);
        using var client = factory.CreateAuthenticatedClient();
        var token = AntiforgeryToken(await client.GetStringAsync("/servers"));
        var endpoint = useDirectRoute
            ? $"/servers/{asset.Id}/ping"
            : "/servers?handler=Ping";

        HttpResponseMessage? response = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            response?.Dispose();
            response = await PostPingAsync(client, endpoint, asset.Id, token);
            if (attempt < 5)
                Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        }

        using (response)
        {
            Assert.NotNull(response);
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.Equal(
                "Ping 操作过于频繁，请稍后重试。",
                await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(5, await factory.GetPingResultCountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ping_not_found_returns_shared_safe_chinese_feedback(
        bool useDirectRoute)
    {
        using var factory = new ServerPageFactory(
            NewUser(),
            PermissionCode.AssetView,
            PermissionCode.PingExecute);
        factory.InitializeData();
        using var client = factory.CreateAuthenticatedClient();
        var missingId = Guid.NewGuid();
        var token = AntiforgeryToken(await client.GetStringAsync("/servers"));
        var endpoint = useDirectRoute
            ? $"/servers/{missingId}/ping"
            : "/servers?handler=Ping";

        var response = await PostPingAsync(client, endpoint, missingId, token);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("未找到要检测的服务器。", body);
        Assert.DoesNotContain("Server asset not found.", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ping_unknown_failure_returns_fixed_message_without_exception_text(
        bool useDirectRoute)
    {
        using var factory = new ServerPageFactory(
            NewUser(),
            PermissionCode.AssetView,
            PermissionCode.PingExecute)
        {
            FailPingPersistence = true,
        };
        factory.InitializeData();
        await factory.AddSubnetAsync();
        var asset = await factory.AddAssetAsync("10.0.0.9", "HQ", [1]);
        using var client = factory.CreateAuthenticatedClient();
        var token = AntiforgeryToken(await client.GetStringAsync("/servers"));
        var endpoint = useDirectRoute
            ? $"/servers/{asset.Id}/ping"
            : "/servers?handler=Ping";

        var response = await PostPingAsync(client, endpoint, asset.Id, token);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("Ping 检测失败，请稍后重试。", body);
        Assert.DoesNotContain(
            ThrowOnPingSaveInterceptor.InternalFailure,
            body,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("MarkAlive", PermissionCode.StatusMarkAlive, "服务器已标记为存活。")]
    [InlineData("Archive", PermissionCode.AssetArchive, "服务器已归档。")]
    public async Task Successful_asset_command_redirect_displays_localized_status_message(
        string handler,
        string permission,
        string expectedMessage)
    {
        using var factory = new ServerPageFactory(NewUser(), PermissionCode.AssetView, permission);
        factory.InitializeData();
        await factory.AddSubnetAsync();
        var asset = await factory.AddAssetAsync("10.0.0.9", "HQ", [1]);
        using var client = factory.CreateAuthenticatedClient();
        var html = await client.GetStringAsync("/servers");
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

        var response = await client.PostAsync($"/servers?handler={handler}", new FormUrlEncodedContent(
            [new("id", asset.Id.ToString()), new("rowVersion", Convert.ToBase64String([1])),
             new("__RequestVerificationToken", token)]));
        var responseHtml = WebUtility.HtmlDecode(
            await client.GetStringAsync(response.Headers.Location!));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(expectedMessage, responseHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Archive_confirmation_identifies_target_explains_impact_and_preserves_secure_payload()
    {
        using var factory = new ServerPageFactory(
            NewUser(),
            PermissionCode.AssetView,
            PermissionCode.AssetArchive);
        factory.InitializeData();
        await factory.AddSubnetAsync();
        var asset = await factory.AddAssetAsync(
            "10.0.0.9",
            "HQ",
            [1, 2],
            AliveStatus.Fault,
            "archive-computer",
            "Archive system");
        using var client = factory.CreateAuthenticatedClient();

        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/servers"));
        var row = ServerRow(html, asset.Id);

        Assert.Contains("确认归档服务器", row, StringComparison.Ordinal);
        Assert.Contains("10.0.0.9", row, StringComparison.Ordinal);
        Assert.Contains("archive-computer", row, StringComparison.Ordinal);
        Assert.Contains("Archive system", row, StringComparison.Ordinal);
        Assert.Contains(
            "归档后，该服务器将从默认资产列表中隐藏，历史记录与审计记录仍会保留。",
            row,
            StringComparison.Ordinal);
        Assert.Contains(">归档服务器<", row, StringComparison.Ordinal);
        Assert.Contains($"name=\"id\" value=\"{asset.Id}\"", row, StringComparison.Ordinal);
        Assert.Contains(
            $"name=\"rowVersion\" value=\"{Convert.ToBase64String([1, 2])}\"",
            row,
            StringComparison.Ordinal);
        Assert.Contains("name=\"__RequestVerificationToken\"", row, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Archive_stale_row_version_returns_conflict_without_archiving_or_leaking_exception()
    {
        using var factory = new ServerPageFactory(
            NewUser(),
            PermissionCode.AssetView,
            PermissionCode.AssetArchive);
        factory.InitializeData();
        await factory.AddSubnetAsync();
        var asset = await factory.AddAssetAsync("10.0.0.9", "HQ", [1]);
        using var client = factory.CreateAuthenticatedClient();
        var token = AntiforgeryToken(await client.GetStringAsync("/servers"));

        var response = await client.PostAsync(
            "/servers?handler=Archive",
            new FormUrlEncodedContent(
            [
                new("id", asset.Id.ToString()),
                new("rowVersion", Convert.ToBase64String([9])),
                new("__RequestVerificationToken", token),
            ]));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("该服务器已被其他用户修改，请刷新后重试。", body);
        Assert.False((await factory.GetAssetsAsync()).Single().IsArchived);
        Assert.DoesNotContain(
            "The server was changed by another user.",
            body,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Archive_invalid_row_version_returns_safe_bad_request_without_exception_text()
    {
        using var factory = new ServerPageFactory(
            NewUser(),
            PermissionCode.AssetView,
            PermissionCode.AssetArchive);
        factory.InitializeData();
        await factory.AddSubnetAsync();
        var asset = await factory.AddAssetAsync("10.0.0.9", "HQ", [1]);
        using var client = factory.CreateAuthenticatedClient();
        var token = AntiforgeryToken(await client.GetStringAsync("/servers"));

        var response = await client.PostAsync(
            "/servers?handler=Archive",
            new FormUrlEncodedContent(
            [
                new("id", asset.Id.ToString()),
                new("rowVersion", "internal-row-version-secret"),
                new("__RequestVerificationToken", token),
            ]));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("请求无效，请刷新页面后重试。", body);
        Assert.DoesNotContain("row version", body, StringComparison.OrdinalIgnoreCase);
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
    public async Task Asset_view_only_user_sees_no_mutation_controls_and_direct_ping_post_is_forbidden()
    {
        using var factory = new ServerPageFactory(NewUser(), PermissionCode.AssetView);
        factory.InitializeData();
        await factory.AddSubnetAsync();
        var asset = await factory.AddAssetAsync("10.0.0.9", "HQ", [1]);
        using var client = factory.CreateAuthenticatedClient();
        var html = await client.GetStringAsync("/servers");
        var tokenHtml = await client.GetStringAsync("/login");
        var token = Regex.Match(tokenHtml, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

        Assert.DoesNotContain("data-drawer=\"register-server\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">编辑<", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">Ping<", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-secret-reveal", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">标记存活<", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">归档<", html, StringComparison.Ordinal);

        var inventoryResponse = await client.PostAsync("/servers?handler=Ping", new FormUrlEncodedContent(
            [new("id", asset.Id.ToString()), new("__RequestVerificationToken", token)]));
        var directResponse = await client.PostAsync($"/servers/{asset.Id}/ping", new FormUrlEncodedContent(
            [new("id", asset.Id.ToString()), new("__RequestVerificationToken", token)]));

        Assert.Equal(HttpStatusCode.Forbidden, inventoryResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, directResponse.StatusCode);
        Assert.Equal(
            "没有权限执行 Ping。",
            await inventoryResponse.Content.ReadAsStringAsync());
        Assert.Equal(
            "没有权限执行 Ping。",
            await directResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Invalid_get_filter_keeps_query_binding_error_visible()
    {
        using var factory = new ServerPageFactory(NewUser(), PermissionCode.AssetView);
        factory.InitializeData();
        using var client = factory.CreateAuthenticatedClient();

        var html = await client.GetStringAsync("/servers?Query.Status=not-a-status");

        Assert.Contains("请检查表单中标记的错误后重试。", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(501, 500)]
    public async Task Server_page_normalizes_page_size_before_querying(
        int requested,
        int expected)
    {
        using var factory = new ServerPageFactory(NewUser(), PermissionCode.AssetView);
        factory.InitializeData();
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync($"/servers?Query.Take={requested}");
        var html = await response.Content.ReadAsStringAsync();
        var takeInput = Regex.Match(
            html,
            "<input[^>]*name=\"Query.Take\"[^>]*>",
            RegexOptions.Singleline).Value;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEmpty(takeInput);
        Assert.Contains("type=\"number\"", takeInput, StringComparison.Ordinal);
        Assert.Contains($"value=\"{expected}\"", takeInput, StringComparison.Ordinal);
        Assert.Contains("min=\"1\"", takeInput, StringComparison.Ordinal);
        Assert.Contains("max=\"500\"", takeInput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Server_filter_resets_skip_and_normalizes_negative_offset()
    {
        using var factory = new ServerPageFactory(NewUser(), PermissionCode.AssetView);
        factory.InitializeData();
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/servers?Query.Skip=-1&Query.Take=25");
        var html = await response.Content.ReadAsStringAsync();
        var filter = HtmlRegion(html, "form", "class=\"command-bar\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "name=\"Query.Skip\" value=\"0\"",
            filter,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("MarkAlive", PermissionCode.StatusMarkAlive)]
    [InlineData("Archive", PermissionCode.AssetArchive)]
    public async Task Failed_non_create_command_does_not_validate_or_open_registration_drawer(
        string handler,
        string permission)
    {
        using var factory = new ServerPageFactory(
            NewUser(),
            PermissionCode.AssetView,
            PermissionCode.AssetCreate,
            permission);
        factory.InitializeData();
        using var client = factory.CreateAuthenticatedClient();
        var html = await client.GetStringAsync("/servers");
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

        var response = await client.PostAsync($"/servers?handler={handler}", new FormUrlEncodedContent(
            [new("id", Guid.NewGuid().ToString()), new("rowVersion", Convert.ToBase64String([1])),
             new("__RequestVerificationToken", token)]));
        var responseHtml = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        var drawerTag = Regex.Match(
            responseHtml,
            "<aside[^>]*data-drawer=\"register-server\"[^>]*>",
            RegexOptions.Singleline).Value;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(">请输入业务 IP。</span>", responseHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("data-open", drawerTag, StringComparison.Ordinal);
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
        Assert.Contains("data-open", createHtml, StringComparison.Ordinal);
        var token = Regex.Match(createHtml, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var post = await client.PostAsync("/servers?handler=Create", new FormUrlEncodedContent(
        [new("Input.BusinessIp", "10.0.0.1"), new("Input.Location", "HQ"), new("Input.AliveStatus", "Unknown"),
         new("Input.ComputerName", "web-01"), new("Input.SystemName", "Web"), new("__RequestVerificationToken", token)]));

        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Equal("10.0.0.1", (await factory.GetAssetsAsync()).Single().BusinessIp);
    }

    [Fact]
    public async Task Edit_page_groups_server_fields_and_keeps_update_concurrency_inputs()
    {
        using var factory = new ServerPageFactory(NewUser(), PermissionCode.AssetView, PermissionCode.AssetEdit);
        factory.InitializeData();
        await factory.AddSubnetAsync();
        var asset = await factory.AddAssetAsync("10.0.0.9", "HQ", [1]);
        using var client = factory.CreateAuthenticatedClient();

        var editHtml = await client.GetStringAsync($"/servers/{asset.Id}/edit");

        Assert.Contains("身份与位置", editHtml, StringComparison.Ordinal);
        Assert.Contains("系统信息", editHtml, StringComparison.Ordinal);
        Assert.Contains("凭据与备注", editHtml, StringComparison.Ordinal);
        Assert.Contains("留空则保留当前密码", editHtml, StringComparison.Ordinal);
        Assert.Contains($"name=\"id\" value=\"{asset.Id}\"", editHtml, StringComparison.Ordinal);
        Assert.Contains("name=\"rowVersion\"", editHtml, StringComparison.Ordinal);
        Assert.Contains("autocomplete=\"new-password\"", editHtml, StringComparison.Ordinal);
        Assert.Contains("保存更改", editHtml, StringComparison.Ordinal);
        Assert.Contains("返回服务器资产", editHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edit_alive_status_options_use_chinese_labels_without_changing_values_or_selection()
    {
        using var factory = new ServerPageFactory(NewUser(), PermissionCode.AssetView, PermissionCode.AssetEdit);
        factory.InitializeData();
        await factory.AddSubnetAsync();
        var asset = await factory.AddAssetAsync(
            "10.0.0.9",
            "HQ",
            [1],
            AliveStatus.Fault);
        using var client = factory.CreateAuthenticatedClient();

        var html = WebUtility.HtmlDecode(
            await client.GetStringAsync($"/servers/{asset.Id}/edit"));
        var select = Regex.Match(
            html,
            "<select[^>]*name=\"Input.AliveStatus\"[^>]*>.*?</select>",
            RegexOptions.Singleline).Value;
        var expectedOptions = new[]
        {
            (Value: "0", Label: "未知", Selected: false),
            (Value: "1", Label: "存活", Selected: false),
            (Value: "2", Label: "异常", Selected: true),
            (Value: "3", Label: "停用", Selected: false),
        };

        Assert.NotEmpty(select);
        foreach (var expected in expectedOptions)
        {
            var option = Regex.Match(
                select,
                $"<option(?=[^>]*value=\"{expected.Value}\")[^>]*>(?<label>.*?)</option>",
                RegexOptions.Singleline);
            Assert.True(option.Success, $"Missing alive-status option value {expected.Value}.");
            Assert.Equal(expected.Label, option.Groups["label"].Value.Trim());
            Assert.Equal(
                expected.Selected,
                option.Value.Contains("selected", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task Edit_business_rule_failure_uses_safe_chinese_message()
    {
        using var factory = new ServerPageFactory(NewUser(), PermissionCode.AssetView, PermissionCode.AssetEdit);
        factory.InitializeData();
        await factory.AddSubnetAsync();
        var duplicate = await factory.AddAssetAsync("10.0.0.9", "HQ", [1]);
        var asset = await factory.AddAssetAsync("10.0.0.10", "Branch", [2]);
        using var client = factory.CreateAuthenticatedClient();
        var html = await client.GetStringAsync($"/servers/{asset.Id}/edit");
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

        var response = await client.PostAsync($"/servers/{asset.Id}/edit", new FormUrlEncodedContent(
        [new("id", asset.Id.ToString()), new("rowVersion", Convert.ToBase64String([2])),
         new("Input.BusinessIp", duplicate.BusinessIp), new("Input.Location", "Branch"),
         new("Input.AliveStatus", "Unknown"), new("Input.ComputerName", "branch"),
         new("Input.SystemName", "Branch"), new("__RequestVerificationToken", token)]));
        var responseHtml = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("无法保存服务器：请检查 IP、网段和字段内容。", responseHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "An active server asset already uses this business IP.",
            responseHtml,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edit_conflict_preserves_the_posted_draft_beside_the_current_snapshot_and_refreshes_the_token()
    {
        using var factory = new ServerPageFactory(NewUser(), PermissionCode.AssetView, PermissionCode.AssetEdit);
        factory.InitializeData();
        await factory.AddSubnetAsync();
        var asset = await factory.AddAssetAsync(
            "10.0.0.9",
            "Original location",
            [1],
            AliveStatus.Unknown,
            "original-computer",
            "Original system");
        using var client = factory.CreateAuthenticatedClient();
        var html = await client.GetStringAsync($"/servers/{asset.Id}/edit");
        var token = AntiforgeryToken(html);
        await factory.ReplaceCurrentAssetAsync(
            asset.Id,
            "10.0.0.10",
            "Current location",
            AliveStatus.Fault,
            "current-computer",
            "Current system",
            "Current OS",
            "Current DB",
            "Current notes",
            [2]);

        var response = await client.PostAsync($"/servers/{asset.Id}/edit", new FormUrlEncodedContent(
        [new("id", asset.Id.ToString()), new("rowVersion", Convert.ToBase64String([1])),
         new("Input.BusinessIp", "10.0.0.11"), new("Input.Location", "Draft location"), new("Input.AliveStatus", "Alive"),
         new("Input.ComputerName", "draft-computer"), new("Input.SystemName", "Draft system"),
         new("Input.OperatingSystemVersion", "Draft OS"), new("Input.DatabaseVersion", "Draft DB"),
         new("Input.Notes", "Draft notes"), new("__RequestVerificationToken", token)]));
        var conflictHtml = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var draft = HtmlRegion(conflictHtml, "form", "data-server-draft");
        Assert.Equal("10.0.0.11", FormValue(draft, "Input.BusinessIp"));
        Assert.Equal("Draft location", FormValue(draft, "Input.Location"));
        Assert.Equal("1", SelectedValue(draft, "Input.AliveStatus"));
        Assert.Equal("draft-computer", FormValue(draft, "Input.ComputerName"));
        Assert.Equal("Draft system", FormValue(draft, "Input.SystemName"));
        Assert.Equal("Draft OS", FormValue(draft, "Input.OperatingSystemVersion"));
        Assert.Equal("Draft DB", FormValue(draft, "Input.DatabaseVersion"));
        Assert.Equal("Draft notes", TextAreaValue(draft, "Input.Notes").TrimStart('\r', '\n'));
        Assert.Equal(Convert.ToBase64String([2]), FormValue(draft, "rowVersion"));

        var current = WebUtility.HtmlDecode(HtmlRegion(conflictHtml, "section", "data-current-snapshot"));
        Assert.Contains("10.0.0.10", current, StringComparison.Ordinal);
        Assert.Contains("Current location", current, StringComparison.Ordinal);
        Assert.Contains("异常", current, StringComparison.Ordinal);
        Assert.DoesNotContain("Fault", current, StringComparison.Ordinal);
        Assert.Contains("current-computer", current, StringComparison.Ordinal);
        Assert.Contains("Current system", current, StringComparison.Ordinal);
        Assert.Contains("Current OS", current, StringComparison.Ordinal);
        Assert.Contains("Current DB", current, StringComparison.Ordinal);
        Assert.Contains("Current notes", current, StringComparison.Ordinal);
        Assert.DoesNotContain("Draft location", current, StringComparison.Ordinal);
        Assert.DoesNotContain("Input.Password", current, StringComparison.Ordinal);
        Assert.Contains(
            "该服务器已被其他用户修改。您的草稿仍保留在编辑表单中；请与数据库最新数据核对后，再明确重新保存。",
            conflictHtml,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edit_conflict_does_not_overwrite_current_data_until_the_user_explicitly_retries_with_the_new_token()
    {
        using var factory = new ServerPageFactory(NewUser(), PermissionCode.AssetView, PermissionCode.AssetEdit);
        factory.InitializeData();
        await factory.AddSubnetAsync();
        var asset = await factory.AddAssetAsync("10.0.0.9", "Original location", [1]);
        using var client = factory.CreateAuthenticatedClient();
        var editHtml = await client.GetStringAsync($"/servers/{asset.Id}/edit");
        await factory.ReplaceCurrentAssetAsync(
            asset.Id,
            "10.0.0.10",
            "Current location",
            AliveStatus.Fault,
            "current-computer",
            "Current system",
            "Current OS",
            "Current DB",
            "Current notes",
            [2]);

        var conflict = await client.PostAsync(
            $"/servers/{asset.Id}/edit",
            EditForm(asset.Id, [1], AntiforgeryToken(editHtml)));
        var conflictHtml = await conflict.Content.ReadAsStringAsync();
        var afterConflict = await factory.GetAssetAsync(asset.Id);

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("Current location", afterConflict.Location);
        Assert.Equal("current-computer", afterConflict.ComputerName);
        Assert.Equal("Current system", afterConflict.SystemName);

        var retry = await client.PostAsync(
            $"/servers/{asset.Id}/edit",
            EditForm(
                asset.Id,
                Convert.FromBase64String(FormValue(conflictHtml, "rowVersion")),
                AntiforgeryToken(conflictHtml)));
        var afterRetry = await factory.GetAssetAsync(asset.Id);

        Assert.Equal(HttpStatusCode.Redirect, retry.StatusCode);
        Assert.Equal("10.0.0.11", afterRetry.BusinessIp);
        Assert.Equal("Draft location", afterRetry.Location);
        Assert.Equal(AliveStatus.Alive, afterRetry.AliveStatus);
        Assert.Equal("draft-computer", afterRetry.ComputerName);
        Assert.Equal("Draft system", afterRetry.SystemName);
        Assert.Equal("Draft OS", afterRetry.OperatingSystemVersion);
        Assert.Equal("Draft DB", afterRetry.DatabaseVersion);
        Assert.Equal("Draft notes", afterRetry.Notes);
    }

    [Fact]
    public async Task Edit_conflict_never_discloses_or_silently_reuses_a_posted_password_or_stored_secret()
    {
        const string postedPassword =
            "posted-conflict-password-93f6a1";
        using var factory = new ServerPageFactory(
            NewUser(),
            PermissionCode.AssetView,
            PermissionCode.AssetEdit);
        factory.InitializeData();
        await factory.AddSubnetAsync();
        var asset = await factory.AddAssetAsync(
            "10.0.0.9",
            "Original location",
            [1]);
        await factory.AddSecretAsync(asset.Id, StoredSecretCiphertext);
        using var client = factory.CreateAuthenticatedClient();
        var editHtml = await client.GetStringAsync($"/servers/{asset.Id}/edit");
        await factory.ReplaceCurrentAssetAsync(
            asset.Id,
            "10.0.0.10",
            "Current location",
            AliveStatus.Fault,
            "current-computer",
            "Current system",
            "Current OS",
            "Current DB",
            "Current notes",
            [2]);

        using var conflict = await client.PostAsync(
            $"/servers/{asset.Id}/edit",
            new FormUrlEncodedContent(
            [
                new("id", asset.Id.ToString()),
                new("rowVersion", Convert.ToBase64String([1])),
                new("Input.BusinessIp", "10.0.0.11"),
                new("Input.Location", "Draft location"),
                new("Input.AliveStatus", "Alive"),
                new("Input.ComputerName", "draft-computer"),
                new("Input.SystemName", "Draft system"),
                new("Input.Password", postedPassword),
                new("__RequestVerificationToken", AntiforgeryToken(editHtml)),
            ]));
        var conflictHtml = await conflict.Content.ReadAsStringAsync();
        var passwordInput = Regex.Match(
            conflictHtml,
            "<input[^>]*name=\"Input.Password\"[^>]*>",
            RegexOptions.Singleline).Value;

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.NotEmpty(passwordInput);
        Assert.DoesNotContain(postedPassword, conflictHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(
            StoredUserPasswordHash,
            conflictHtml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            Convert.ToBase64String(StoredSecretCiphertext),
            conflictHtml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("value=", passwordInput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "密码不会保留；如需更新密码，请重新输入。",
            conflictHtml,
            StringComparison.Ordinal);
        Assert.Equal(
            StoredSecretCiphertext,
            await factory.GetSecretCiphertextAsync(asset.Id));

        using var retry = await client.PostAsync(
            $"/servers/{asset.Id}/edit",
            EditForm(
                asset.Id,
                Convert.FromBase64String(FormValue(conflictHtml, "rowVersion")),
                AntiforgeryToken(conflictHtml)));

        Assert.Equal(HttpStatusCode.Redirect, retry.StatusCode);
        Assert.Equal(
            StoredSecretCiphertext,
            await factory.GetSecretCiphertextAsync(asset.Id));
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
    [Fact]
    public async Task Prefix_and_alive_filters_use_registered_rows_for_total_and_paging()
    {
        await using var db = NewDatabase();
        var actor = await AddUserAsync(db, PermissionCode.AssetCreate);
        var assets = NewAssetService(db);
        var subnet = await AddEnabledSubnetAsync(db, "10.0.0.0/29");
        var alive = await assets.CreateAsync(Input("10.0.0.2", status: AliveStatus.Alive), actor.Id, default);

        var page = await assets.ListAsync(new ServerListQuery(SubnetId: subnet.Id, PoolMode: true, Search: "10.0.0.", Status: AliveStatus.Alive, Skip: 0, Take: 3), default);

        Assert.Equal(1, page.TotalCount);
        Assert.Equal(alive.Id, Assert.Single(page.Items).AssetId);
    }

    [Fact]
    public async Task Text_and_unknown_filters_do_not_count_free_pool_rows()
    {
        await using var db = NewDatabase();
        var actor = await AddUserAsync(db, PermissionCode.AssetCreate);
        var assets = NewAssetService(db);
        var subnet = await AddEnabledSubnetAsync(db, "10.0.0.0/29");
        var unknown = await assets.CreateAsync(Input("10.0.0.3", location: "Unique Rack", status: AliveStatus.Unknown), actor.Id, default);

        var page = await assets.ListAsync(new ServerListQuery(SubnetId: subnet.Id, PoolMode: true, Search: "Unique", Status: AliveStatus.Unknown, Skip: 0, Take: 3), default);

        Assert.Equal(1, page.TotalCount);
        Assert.Equal(unknown.Id, Assert.Single(page.Items).AssetId);
    }

    [Fact]
    public async Task Large_subnet_registered_filter_returns_only_registered_page()
    {
        await using var db = NewDatabase();
        var actor = await AddUserAsync(db, PermissionCode.AssetCreate);
        var assets = NewAssetService(db);
        await AddEnabledSubnetAsync(db, "0.0.0.0/0");
        var alive = await assets.CreateAsync(Input("0.0.0.1", status: AliveStatus.Alive), actor.Id, default);

        var page = await assets.ListAsync(new ServerListQuery(PoolMode: true, Status: AliveStatus.Alive, Skip: 0, Take: 1), default);

        Assert.Equal(1, page.TotalCount);
        Assert.Equal(alive.Id, Assert.Single(page.Items).AssetId);
    }

    private static ServerAssetInput Input(string ip, string location = "HQ", AliveStatus status = AliveStatus.Unknown) =>
        new(ip, location, status, "server", "WebPass", null, null, "normal metadata");

    private static ServerAssetService NewAssetService(WebPassDbContext db) => new(db, new PermissionAuthorizationHandler(db), new AuditWriter(db));

    private static PingService NewPingService(WebPassDbContext db, IPingTransport transport, int perUserPerMinute = 5) => new(
        db, new PermissionAuthorizationHandler(db), new AuditWriter(db), transport,
        Options.Create(new WebPassOptions { PingTimeoutMilliseconds = 1000, PingMaxConcurrency = 2, PingPerUserPerMinute = perUserPerMinute }));

    private static WebPassDbContext NewDatabase() => new(new DbContextOptionsBuilder<WebPassDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static string AntiforgeryToken(string html)
    {
        var token = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")
            .Groups[1]
            .Value;
        Assert.False(string.IsNullOrEmpty(token));
        return token;
    }

    private static FormUrlEncodedContent EditForm(Guid assetId, byte[] rowVersion, string antiforgeryToken) =>
        new(
        [
            new("id", assetId.ToString()),
            new("rowVersion", Convert.ToBase64String(rowVersion)),
            new("Input.BusinessIp", "10.0.0.11"),
            new("Input.Location", "Draft location"),
            new("Input.AliveStatus", "Alive"),
            new("Input.ComputerName", "draft-computer"),
            new("Input.SystemName", "Draft system"),
            new("Input.OperatingSystemVersion", "Draft OS"),
            new("Input.DatabaseVersion", "Draft DB"),
            new("Input.Notes", "Draft notes"),
            new("__RequestVerificationToken", antiforgeryToken),
        ]);

    private static string FormValue(string html, string name)
    {
        var match = Regex.Match(
            html,
            $"<input[^>]*name=\"{Regex.Escape(name)}\"[^>]*value=\"([^\"]*)\"",
            RegexOptions.Singleline);
        Assert.True(match.Success, $"Could not find form value for {name}.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static string SelectedValue(string html, string name)
    {
        var select = Regex.Match(
            html,
            $"<select[^>]*name=\"{Regex.Escape(name)}\"[^>]*>(.*?)</select>",
            RegexOptions.Singleline);
        Assert.True(select.Success, $"Could not find select for {name}.");
        var option = Regex.Match(
            select.Groups[1].Value,
            "<option[^>]*selected=\"selected\"[^>]*value=\"([^\"]*)\"|<option[^>]*value=\"([^\"]*)\"[^>]*selected=\"selected\"",
            RegexOptions.Singleline);
        Assert.True(option.Success, $"Could not find selected option for {name}.");
        return WebUtility.HtmlDecode(option.Groups[1].Success ? option.Groups[1].Value : option.Groups[2].Value);
    }

    private static string TextAreaValue(string html, string name)
    {
        var match = Regex.Match(
            html,
            $"<textarea[^>]*name=\"{Regex.Escape(name)}\"[^>]*>(.*?)</textarea>",
            RegexOptions.Singleline);
        Assert.True(match.Success, $"Could not find textarea for {name}.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static string HtmlRegion(string html, string element, string attribute)
    {
        var match = Regex.Match(
            html,
            $"<{element}[^>]*{attribute}[^>]*>.*?</{element}>",
            RegexOptions.Singleline);
        Assert.True(match.Success, $"Could not find {element}[{attribute}].");
        return match.Value;
    }

    private static Task<HttpResponseMessage> PostPingAsync(
        HttpClient client,
        string endpoint,
        Guid assetId,
        string antiforgeryToken) =>
        client.PostAsync(
            endpoint,
            new FormUrlEncodedContent(
            [
                new("id", assetId.ToString()),
                new("__RequestVerificationToken", antiforgeryToken),
            ]));

    private static string ServerRow(string html, Guid assetId)
    {
        var match = Regex.Match(
            html,
            $"<tr[^>]*data-asset-id=\"{assetId}\"[^>]*>.*?</tr>",
            RegexOptions.Singleline);
        Assert.True(match.Success, $"Could not find the server row for {assetId}.");
        return match.Value;
    }

    private static async Task<ServerAsset> AddPagedAssetsAsync(
        ServerPageFactory factory,
        AliveStatus targetStatus = AliveStatus.Unknown)
    {
        ServerAsset? target = null;
        for (var host = 1; host <= 51; host++)
        {
            target = await factory.AddAssetAsync(
                $"10.0.0.{host}",
                $"Rack {host}",
                [(byte)host],
                host == 51 ? targetStatus : AliveStatus.Unknown,
                $"computer-{host}",
                $"System {host}");
        }

        return target!;
    }

    private static async Task<AppUser> AddUserAsync(WebPassDbContext db, params string[] permissions)
    {
        var user = NewUser();
        db.Users.Add(user);
        db.UserPermissions.AddRange(permissions.Select(code => new UserPermission { UserId = user.Id, PermissionCode = code }));
        await db.SaveChangesAsync();
        return user;
    }

    private static AppUser NewUser() => new()
    {
        Username = Guid.NewGuid().ToString("N"),
        PasswordHash = StoredUserPasswordHash,
    };

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
    private sealed class ServerPageFactory : WebApplicationFactory<Program>
    {
        private readonly AppUser _user;
        private readonly string _databaseName = Guid.NewGuid().ToString("N");
        private readonly string[] _permissions;

        public ServerPageFactory(AppUser user, params string[] permissions)
        {
            _user = user;
            _permissions = permissions;
        }

        public PingTransportResult PingResponse { get; init; } =
            new("Success", 12, null);

        public bool FailPingPersistence { get; init; }

        public void InitializeData()
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WebPassDbContext>();
            db.Users.Add(_user);
            db.UserPermissions.AddRange(_permissions.Select(code => new UserPermission { UserId = _user.Id, PermissionCode = code }));
            db.SaveChanges();
        }
        public async Task<Subnet> GetOnlySubnetAsync()
        {
            using var scope = Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<WebPassDbContext>().Subnets.SingleAsync();
        }

        public async Task<ServerAsset> AddAssetAsync(
            string ip,
            string location,
            byte[] rowVersion,
            AliveStatus aliveStatus = AliveStatus.Unknown,
            string computerName = "current",
            string systemName = "Current")
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WebPassDbContext>();
            var subnet = await db.Subnets.SingleAsync();
            var asset = new ServerAsset
            {
                SubnetId = subnet.Id,
                BusinessIp = ip,
                BusinessIpNumber = Ipv4Number(ip),
                Location = location,
                AliveStatus = aliveStatus,
                ComputerName = computerName,
                SystemName = systemName,
                RowVersion = rowVersion,
            };
            db.ServerAssets.Add(asset);
            await db.SaveChangesAsync();
            return asset;
        }

        public async Task AddSecretAsync(Guid assetId, byte[] ciphertext)
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WebPassDbContext>();
            db.ServerSecrets.Add(new ServerSecret
            {
                ServerAssetId = assetId,
                Ciphertext = ciphertext,
                Nonce = new byte[12],
                AuthenticationTag = new byte[16],
                KeyVersion = 1,
            });
            await db.SaveChangesAsync();
        }

        public async Task<byte[]> GetSecretCiphertextAsync(Guid assetId)
        {
            using var scope = Services.CreateScope();
            return await scope.ServiceProvider
                .GetRequiredService<WebPassDbContext>()
                .ServerSecrets
                .Where(secret => secret.ServerAssetId == assetId)
                .Select(secret => secret.Ciphertext)
                .SingleAsync();
        }

        public async Task ReplaceCurrentAssetAsync(
            Guid assetId,
            string businessIp,
            string location,
            AliveStatus aliveStatus,
            string computerName,
            string systemName,
            string? operatingSystemVersion,
            string? databaseVersion,
            string? notes,
            byte[] rowVersion)
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WebPassDbContext>();
            var asset = await db.ServerAssets.SingleAsync(x => x.Id == assetId);
            asset.BusinessIp = businessIp;
            asset.BusinessIpNumber = Ipv4Number(businessIp);
            asset.Location = location;
            asset.AliveStatus = aliveStatus;
            asset.ComputerName = computerName;
            asset.SystemName = systemName;
            asset.OperatingSystemVersion = operatingSystemVersion;
            asset.DatabaseVersion = databaseVersion;
            asset.Notes = notes;
            asset.RowVersion = rowVersion;
            await db.SaveChangesAsync();
        }

        public async Task<ServerAsset> GetAssetAsync(Guid assetId)
        {
            using var scope = Services.CreateScope();
            return await scope.ServiceProvider
                .GetRequiredService<WebPassDbContext>()
                .ServerAssets
                .AsNoTracking()
                .SingleAsync(x => x.Id == assetId);
        }

        private static long Ipv4Number(string ip)
        {
            var bytes = IPAddress.Parse(ip).GetAddressBytes();
            return ((long)bytes[0] << 24) |
                ((long)bytes[1] << 16) |
                ((long)bytes[2] << 8) |
                bytes[3];
        }

        public HttpClient CreateAuthenticatedClient()
        {
            using var scope = Services.CreateScope();
            var cookieOptions = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);
            var identity = new ClaimsIdentity(
                [
                    new Claim(
                        ClaimTypes.NameIdentifier,
                        _user.Id.ToString()),
                    new Claim(
                        LoginModel.SessionStartedClaimType,
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                            .ToString(CultureInfo.InvariantCulture)),
                ],
                CookieAuthenticationDefaults.AuthenticationScheme);
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

        public async Task DisableSubnetAsync()
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WebPassDbContext>();
            var subnet = await db.Subnets.SingleAsync();
            subnet.IsEnabled = false;
            await db.SaveChangesAsync();
        }

        public async Task<List<ServerAsset>> GetAssetsAsync()
        {
            using var scope = Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<WebPassDbContext>().ServerAssets.AsNoTracking().ToListAsync();
        }

        public async Task<int> GetPingResultCountAsync()
        {
            using var scope = Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<WebPassDbContext>().PingResults.CountAsync();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<WebPassDbContext>>();
            services.RemoveAll<WebPassDbContext>();
            services.RemoveAll<IDbContextOptionsConfiguration<WebPassDbContext>>();
            services.AddDbContext<WebPassDbContext>(options =>
            {
                options.UseInMemoryDatabase(_databaseName);
                if (FailPingPersistence)
                    options.AddInterceptors(new ThrowOnPingSaveInterceptor());
            });
            services.RemoveAll<IPingTransport>();
            services.AddSingleton<IPingTransport>(
                new ConfigurablePingTransport(() => PingResponse));
            services.RemoveAll<ISecretCipher>();
            services.AddSingleton<ISecretCipher, NonDisclosingTestCipher>();
        });
    }

    private sealed class NonDisclosingTestCipher : ISecretCipher
    {
        public Task<SecretEnvelope> EncryptAsync(
            Guid secretId,
            string plaintext,
            CancellationToken ct) =>
            Task.FromResult(new SecretEnvelope(
                [2, 7, 1, 8, 2, 8],
                new byte[12],
                new byte[16],
                2));

        public Task<string> DecryptAsync(
            Guid secretId,
            SecretEnvelope envelope,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class ConfigurablePingTransport(
        Func<PingTransportResult> response) : IPingTransport
    {
        public Task<PingTransportResult> SendAsync(
            string targetIp,
            int timeoutMilliseconds,
            CancellationToken ct) =>
            Task.FromResult(response());
    }

    private sealed class ThrowOnPingSaveInterceptor : SaveChangesInterceptor
    {
        public const string InternalFailure =
            "sensitive persistence detail: PingResults table unavailable";

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context?.ChangeTracker.Entries<PingResult>()
                .Any(entry => entry.State == EntityState.Added) == true)
            {
                throw new InvalidOperationException(InternalFailure);
            }

            return base.SavingChangesAsync(
                eventData,
                result,
                cancellationToken);
        }
    }
}
