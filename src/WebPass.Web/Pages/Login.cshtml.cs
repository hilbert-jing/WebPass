using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebPass.Web.Infrastructure.Identity;

namespace WebPass.Web.Pages;

public sealed class LoginModel(LoginService loginService) : PageModel
{
    [BindProperty]
    public LoginInput Input { get; set; } = new();

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var sourceIp = HttpContext.Connection.RemoteIpAddress ?? System.Net.IPAddress.Loopback;
        var result = await loginService.LoginAsync(Input.Username, Input.Password, sourceIp, ct);
        if (result.Kind != LoginResultKind.Success || result.UserId is not { } userId)
        {
            ModelState.AddModelError(string.Empty, "Invalid username or password.");
            return Page();
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
            CookieAuthenticationDefaults.AuthenticationScheme));
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        return Redirect("/servers");
    }

    public sealed class LoginInput
    {
        [Required]
        public string Username { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;
    }
}
