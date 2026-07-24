using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using WebPass.Web.Application.Authorization;
using WebPass.Web.Application.Subnets;
using WebPass.Web.Infrastructure.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebPass.Web.Data;
using WebPass.Web.Configuration;
using WebPass.Web.Infrastructure.Auditing;
using WebPass.Web.Infrastructure.Identity;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddAntiforgery();
builder.Services.AddAuthorization();
builder.Services.AddAuthorization(options =>
{
    foreach (var code in PermissionCode.OrdinaryUserCodes)
    {
        options.AddPolicy(code, policy => policy.RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(code)));
    }

    options.AddPolicy(PermissionCode.AdministratorPolicy, policy => policy.RequireAuthenticatedUser().AddRequirements(new AdministratorRequirement()));
});
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
    });
builder.Services.AddDbContext<WebPassDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("WebPass")));
builder.Services.AddOptions<WebPassOptions>()
    .BindConfiguration(WebPassOptions.SectionName)
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<WebPassOptions>, WebPassOptionsValidator>();
builder.Services.AddScoped<IPasswordHasher, Argon2PasswordHasher>();
builder.Services.AddScoped<AuditWriter>();
builder.Services.AddScoped<LoginService>();
builder.Services.AddScoped<PermissionAuthorizationHandler>();
builder.Services.AddScoped<IAuthorizationHandler>(services => services.GetRequiredService<PermissionAuthorizationHandler>());
builder.Services.AddScoped<SubnetService>();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

app.Run();

public partial class Program;
