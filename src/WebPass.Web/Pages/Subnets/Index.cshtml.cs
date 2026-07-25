using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebPass.Web.Application.Authorization;
using WebPass.Web.Application.Subnets;
using WebPass.Web.Domain.Entities;

namespace WebPass.Web.Pages.Subnets;

[Authorize(Policy = PermissionCode.SubnetManage)]
public sealed class IndexModel(SubnetService subnetService) : PageModel
{
    public IReadOnlyList<Subnet> Subnets { get; private set; } = [];

    [BindProperty]
    public SubnetForm Input { get; set; } = new();

    public async Task OnGetAsync(CancellationToken ct) => Subnets = await subnetService.ListAsync(UserId(), ct);

    public async Task<IActionResult> OnPostPreviewAsync(CancellationToken ct)
    {
        try
        {
            var preview = await subnetService.PreviewAsync(Input.Cidr, UserId(), ct);
            return new JsonResult(preview);
        }
        catch (Exception exception) when (exception is ArgumentException or UnauthorizedAccessException)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            await LoadAsync(ct);
            return Page();
        }

        try
        {
            await subnetService.CreateAsync(Input.ToInput(), UserId(), ct);
            return RedirectToPage();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await LoadAsync(ct);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostEditAsync(Guid id, string? rowVersion, CancellationToken ct) =>
        await ExecuteMutationAsync(() => subnetService.UpdateAsync(id, Input.ToInput(), DecodeRowVersion(rowVersion), UserId(), ct), ct);

    public async Task<IActionResult> OnPostSetEnabledAsync(Guid id, bool isEnabled, string? rowVersion, CancellationToken ct) =>
        await ExecuteMutationAsync(() => subnetService.SetEnabledAsync(id, isEnabled, DecodeRowVersion(rowVersion), UserId(), ct), ct);

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, string? rowVersion, CancellationToken ct) =>
        await ExecuteMutationAsync(() => subnetService.DeleteAsync(id, DecodeRowVersion(rowVersion), UserId(), ct), ct);

    private async Task<IActionResult> ExecuteMutationAsync(Func<Task> command, CancellationToken ct)
    {
        try
        {
            await command();
            return RedirectToPage();
        }
        catch (ArgumentException exception)
        {
            return BadRequest(exception.Message);
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await LoadAsync(ct);
            return Page();
        }
    }

    private async Task LoadAsync(CancellationToken ct) => Subnets = await subnetService.ListAsync(UserId(), ct);
    private Guid UserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private static byte[] DecodeRowVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A row version is required.", nameof(value));
        }
        try
        {
            var rowVersion = Convert.FromBase64String(value);
            return rowVersion.Length == 0 ? throw new ArgumentException("A row version is required.", nameof(value)) : rowVersion;
        }
        catch (FormatException)
        {
            throw new ArgumentException("The row version is invalid.", nameof(value));
        }
    }

    public sealed class SubnetForm
    {
        [Required]
        public string Name { get; set; } = string.Empty;
        [Required]
        public string Cidr { get; set; } = string.Empty;
        [Required]
        public string Location { get; set; } = string.Empty;
        public string? Notes { get; set; }
        public bool IsEnabled { get; set; } = true;
        public SubnetInput ToInput() => new(Name, Cidr, Location, Notes, IsEnabled);
    }
}
