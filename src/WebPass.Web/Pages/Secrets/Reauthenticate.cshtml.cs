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

    public string SafeReturnUrl { get; private set; } = "/servers";

    public string ReturnActionLabel =>
        string.Equals(SafeReturnUrl, "/servers", StringComparison.OrdinalIgnoreCase)
            ? "返回服务器资产"
            : "返回原页面";

    public void OnGet()
    {
        SetSafeReturnUrl();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        SetSafeReturnUrl();
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

        TempData["StatusMessage"] =
            "验证已通过，接下来的 5 分钟内可执行敏感操作。";
        return LocalRedirect(SafeReturnUrl);
    }

    private void SetSafeReturnUrl()
    {
        SafeReturnUrl =
            !string.IsNullOrWhiteSpace(ReturnUrl) && Url.IsLocalUrl(ReturnUrl)
                ? ReturnUrl
                : "/servers";
        ReturnUrl = SafeReturnUrl;
        ModelState.Remove(nameof(ReturnUrl));
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
