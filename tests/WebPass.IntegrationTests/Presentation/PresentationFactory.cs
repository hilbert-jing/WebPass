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

namespace WebPass.IntegrationTests.Presentation;

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
