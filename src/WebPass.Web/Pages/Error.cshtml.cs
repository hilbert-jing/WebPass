using Microsoft.AspNetCore.Mvc.RazorPages;

namespace WebPass.Web.Pages;

public sealed class ErrorModel : PageModel
{
    public string CorrelationId { get; private set; } = string.Empty;

    public void OnGet() => CorrelationId = HttpContext.TraceIdentifier;
}
