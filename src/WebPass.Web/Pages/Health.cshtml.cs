using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebPass.Web.Data;

namespace WebPass.Web.Pages;

[AllowAnonymous]
public sealed class HealthModel(WebPassDbContext db) : PageModel
{
    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var databaseAvailable = await db.Database.CanConnectAsync(ct);
        return new JsonResult(new
        {
            application = "available",
            database = databaseAvailable ? "available" : "unavailable",
        })
        {
            StatusCode = databaseAvailable
                ? StatusCodes.Status200OK
                : StatusCodes.Status503ServiceUnavailable,
        };
    }
}
