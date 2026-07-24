using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebPass.Web.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddAntiforgery();
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie();
builder.Services.AddDbContext<WebPassDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("WebPass")));
builder.Services.AddOptions<WebPassOptions>()
    .BindConfiguration(WebPassOptions.SectionName)
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<WebPassOptions>, WebPassOptionsValidator>();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

app.Run();

public partial class Program;

internal sealed class WebPassDbContext(DbContextOptions<WebPassDbContext> options) : DbContext(options);
