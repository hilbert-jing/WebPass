using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using WebPass.Web.Infrastructure.Identity;
using WebPass.Web.Infrastructure.Security;

namespace WebPass.Web.Pages.Account;

[Authorize]
[EnableRateLimiting(SecretRateLimitPolicies.Reauthentication)]
public sealed class ChangePasswordModel(
    PasswordChangeService passwordChanges) : PageModel
{
    private static readonly string[] PasswordFieldKeys =
    [
        $"{nameof(Input)}.{nameof(InputModel.CurrentPassword)}",
        $"{nameof(Input)}.{nameof(InputModel.NewPassword)}",
        $"{nameof(Input)}.{nameof(InputModel.NewPasswordConfirmation)}",
    ];

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!TryUserId(out var userId)
            || !await passwordChanges.CanChangeAsync(userId, ct))
        {
            return Forbid();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!TryUserId(out var userId))
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            ClearPasswordValues();
            return Page();
        }

        var result = await passwordChanges.ChangeAsync(
            userId,
            Input.CurrentPassword,
            Input.NewPassword,
            ct);
        switch (result.Kind)
        {
            case PasswordChangeResultKind.Success:
                TempData["StatusMessage"] = "登录密码已修改。";
                return RedirectToPage();
            case PasswordChangeResultKind.UserUnavailable:
                return Forbid();
            case PasswordChangeResultKind.IncorrectCurrentPassword:
                return PageWithError(string.Empty, "当前密码不正确。");
            case PasswordChangeResultKind.InvalidNewPassword:
                return PageWithError(
                    $"{nameof(Input)}.{nameof(InputModel.NewPassword)}",
                    "请输入新密码。");
            case PasswordChangeResultKind.ConcurrencyConflict:
                return PageWithError(
                    string.Empty,
                    "账号信息已发生变化，请刷新后重试。");
            default:
                throw new InvalidOperationException(
                    "Unknown password change result.");
        }
    }

    private IActionResult PageWithError(string key, string message)
    {
        ModelState.AddModelError(key, message);
        ClearPasswordValues();
        return Page();
    }

    private void ClearPasswordValues()
    {
        Input = new();
        foreach (var key in PasswordFieldKeys)
        {
            if (!ModelState.TryGetValue(key, out var entry))
            {
                continue;
            }

            var messages = entry.Errors
                .Select(error => error.ErrorMessage)
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .ToArray();
            ModelState.Remove(key);
            foreach (var message in messages)
            {
                ModelState.AddModelError(key, message);
            }
        }
    }

    private bool TryUserId(out Guid userId) => Guid.TryParse(
        User.FindFirstValue(ClaimTypes.NameIdentifier),
        out userId);

    public sealed class InputModel
    {
        [Required(ErrorMessage = "请输入当前密码。")]
        [DataType(DataType.Password)]
        public string CurrentPassword { get; set; } = string.Empty;

        [Required(ErrorMessage = "请输入新密码。")]
        [DataType(DataType.Password)]
        public string NewPassword { get; set; } = string.Empty;

        [Required(ErrorMessage = "请再次输入新密码。")]
        [Compare(
            nameof(NewPassword),
            ErrorMessage = "两次输入的新密码不一致。")]
        [DataType(DataType.Password)]
        public string NewPasswordConfirmation { get; set; } = string.Empty;
    }
}
