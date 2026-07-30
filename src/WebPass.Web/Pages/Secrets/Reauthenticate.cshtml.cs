using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using WebPass.Web.Application.Authorization;
using WebPass.Web.Application.Secrets;
using WebPass.Web.Infrastructure.Security;

namespace WebPass.Web.Pages.Secrets;

[Authorize(Policy = PermissionCode.SecretReveal)]
[EnableRateLimiting(SecretRateLimitPolicies.Reauthentication)]
public sealed class ReauthenticateModel(ReauthenticationService reauthentication) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            await reauthentication.VerifyAsync(UserId(), Input.Password, ct);
        }
        catch (UnauthorizedAccessException)
        {
            ModelState.AddModelError(string.Empty, "当前密码验证失败。");
            return Page();
        }

        return LocalRedirect(
            !string.IsNullOrWhiteSpace(ReturnUrl) && Url.IsLocalUrl(ReturnUrl)
                ? ReturnUrl
                : "/servers");
    }

    private Guid UserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public sealed class InputModel
    {
        [Required(ErrorMessage = "请输入当前密码。")]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;
    }
}
