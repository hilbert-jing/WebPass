using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WebPass.Web.Application.Authorization;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using Xunit;

namespace WebPass.IntegrationTests.Authorization;

public sealed class PermissionRouteTests
{
    [Fact]
    public async Task Direct_subnets_url_is_forbidden_to_an_authenticated_user_without_permission()
    {
        using var factory = new PermissionRouteFactory(NewUser());
        factory.InitializeData();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestHeaderAuthenticationHandler.UserIdHeader, factory.User.Id.ToString());

        var response = await client.GetAsync("/subnets");

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Subnets_url_allows_an_ordinary_user_with_subnet_permission()
    {
        using var factory = new PermissionRouteFactory(NewUser(), PermissionCode.SubnetManage);
        factory.InitializeData();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestHeaderAuthenticationHandler.UserIdHeader, factory.User.Id.ToString());

        var response = await client.GetAsync("/subnets");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_subnets_url_follows_the_login_flow()
    {
        using var factory = new WebPassFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/subnets");

        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location?.AbsolutePath);
    }

    private static AppUser NewUser() => new() { Username = Guid.NewGuid().ToString("N"), PasswordHash = "hash" };

    private sealed class PermissionRouteFactory(AppUser user, params string[] permissions) : WebApplicationFactory<Program>
    {
        private readonly string _databaseName = Guid.NewGuid().ToString("N");
        public AppUser User { get; } = user;

        public void InitializeData()
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WebPassDbContext>();
            db.Users.Add(User);
            db.UserPermissions.AddRange(permissions.Select(code => new UserPermission { UserId = User.Id, PermissionCode = code }));
            db.SaveChanges();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<WebPassDbContext>>();
            services.RemoveAll<WebPassDbContext>();
            services.RemoveAll<IDbContextOptionsConfiguration<WebPassDbContext>>();
            services.AddDbContext<WebPassDbContext>(options => options.UseInMemoryDatabase(_databaseName));
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = TestHeaderAuthenticationHandler.Scheme;
                options.DefaultChallengeScheme = TestHeaderAuthenticationHandler.Scheme;
            }).AddScheme<AuthenticationSchemeOptions, TestHeaderAuthenticationHandler>(TestHeaderAuthenticationHandler.Scheme, _ => { });
        });
    }
}
