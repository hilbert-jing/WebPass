using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebPass.Web.Application.Authorization;
using WebPass.Web.Application.Ping;

namespace WebPass.Web.Pages.Servers;

[Authorize(Policy = PermissionCode.PingExecute)]
public sealed class PingModel(PingService pingService) : PageModel
{
    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken ct)
    {
        try
        {
            var result = await pingService.ExecuteAsync(id, Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!), ct);
            TempData["PingResult"] = $"{result.Outcome}; latency: {(result.LatencyMilliseconds is null ? "n/a" : result.LatencyMilliseconds + " ms")}; executed: {result.ExecutedAt:O}";
            return RedirectToPage("/Servers/Index");
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (InvalidOperationException exception)
        {
            if (exception.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
                return new ObjectResult("Ping rate limit exceeded.") { StatusCode = StatusCodes.Status429TooManyRequests };
            return BadRequest(exception.Message);
        }
        catch (KeyNotFoundException exception) { return NotFound(exception.Message); }
    }
}
