using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebPass.Web.Infrastructure.Auditing;

namespace WebPass.Web.Pages;

[Authorize]
public sealed class LogoutModel(AuditWriter auditWriter) : PageModel
{
    public IActionResult OnGet() =>
        StatusCode(StatusCodes.Status405MethodNotAllowed);

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var userId = Guid.Parse(
            User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await auditWriter.WriteAsync(
            new AuditEntry(
                userId,
                "Logout",
                "User",
                userId.ToString(),
                "Success",
                null),
            ct);
        await HttpContext.SignOutAsync(
            CookieAuthenticationDefaults.AuthenticationScheme);
        return Redirect("/login");
    }
}
