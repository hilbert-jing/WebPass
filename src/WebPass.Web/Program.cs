using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using WebPass.Web.Application.Authorization;
using WebPass.Web.Application.Assets;
using WebPass.Web.Application.Exporting;
using WebPass.Web.Application.Importing;
using WebPass.Web.Application.Ping;
using WebPass.Web.Application.Secrets;
using WebPass.Web.Application.Subnets;
using WebPass.Web.Infrastructure.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebPass.Web.Data;
using WebPass.Web.Configuration;
using WebPass.Web.Infrastructure.Auditing;
using WebPass.Web.Infrastructure.Exporting;
using WebPass.Web.Infrastructure.Identity;
using WebPass.Web.Infrastructure.Importing;
using WebPass.Web.Infrastructure.Networking;
using WebPass.Web.Infrastructure.Secrets;
using WebPass.Web.Infrastructure.Security;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddAntiforgery();
builder.Services.Configure<FormOptions>(options =>
{
    options.MemoryBufferThreshold = 11 * 1024 * 1024;
    options.MultipartBodyLengthLimit = 11 * 1024 * 1024;
});
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
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddHttpContextAccessor();
builder.Services.AddDbContext<WebPassDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("WebPass")));
builder.Services.AddOptions<WebPassOptions>()
    .BindConfiguration(WebPassOptions.SectionName)
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<WebPassOptions>, WebPassOptionsValidator>();
builder.Services.Configure<SecretEncryptionOptions>(
    builder.Configuration.GetSection(SecretEncryptionOptions.SectionName));
builder.Services.AddScoped<IPasswordHasher, Argon2PasswordHasher>();
builder.Services.AddScoped<AuditWriter>();
builder.Services.AddScoped<LoginService>();
builder.Services.AddMemoryCache();
builder.Services.AddRateLimiter(SecretRateLimitPolicies.AddTo);
builder.Services.AddScoped<PermissionAuthorizationHandler>();
builder.Services.AddScoped<IAuthorizationHandler>(services => services.GetRequiredService<PermissionAuthorizationHandler>());
builder.Services.AddScoped<ServerAssetService>();
builder.Services.AddScoped<ExportDocumentWriter>();
builder.Services.AddScoped<AssetExportService>();
builder.Services.AddScoped<PingService>();
builder.Services.AddScoped<IPingTransport, SystemPingTransport>();
builder.Services.AddScoped<SubnetService>();
builder.Services.AddSingleton<ICertificateProvider, WindowsCertificateProvider>();
builder.Services.AddScoped<IDataKeyWrapper, CertificateKeyWrapper>();
builder.Services.AddScoped<IDataEncryptionKeyProvider, DatabaseDataEncryptionKeyProvider>();
builder.Services.AddScoped<ISecretCipher, AesGcmSecretCipher>();
builder.Services.AddScoped<DataKeyRotationService>();
builder.Services.AddSingleton<IReauthenticationGrantStore, InMemoryReauthenticationGrantStore>();
builder.Services.AddScoped<IAuthenticationSessionFingerprint, CookieAuthenticationSessionFingerprint>();
builder.Services.AddScoped<ReauthenticationService>();
builder.Services.AddScoped<SecretRevealService>();
builder.Services.AddSingleton<InMemoryImportStageStore>();
builder.Services.AddScoped<CsvAssetParser>();
builder.Services.AddScoped<XlsxAssetParser>();
builder.Services.AddScoped<IImportService, ImportService>();

var app = builder.Build();
app.UseExceptionHandler("/error");

app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.MapRazorPages();

app.Run();

public partial class Program;
