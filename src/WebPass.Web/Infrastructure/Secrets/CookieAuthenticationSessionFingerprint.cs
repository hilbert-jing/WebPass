using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using WebPass.Web.Application.Secrets;

namespace WebPass.Web.Infrastructure.Secrets;

public sealed class CookieAuthenticationSessionFingerprint(
    IHttpContextAccessor httpContextAccessor,
    IOptionsMonitor<CookieAuthenticationOptions> cookieOptions)
    : IAuthenticationSessionFingerprint
{
    public string GetCurrent()
    {
        var context = httpContextAccessor.HttpContext
            ?? throw new UnauthorizedAccessException("An authenticated HTTP session is required.");
        var options = cookieOptions.Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var cookieName = options.Cookie.Name
            ?? CookieAuthenticationDefaults.CookiePrefix + CookieAuthenticationDefaults.AuthenticationScheme;
        if (!context.Request.Cookies.TryGetValue(cookieName, out var cookie)
            || string.IsNullOrEmpty(cookie))
        {
            throw new UnauthorizedAccessException("An authenticated HTTP session is required.");
        }

        var input = Encoding.UTF8.GetBytes($"webpass-reauth-v1\0{cookie}");
        try
        {
            return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(input))}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }
}
