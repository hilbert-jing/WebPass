using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebPass.Web.Application.Authorization;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;

namespace WebPass.Web.Pages.Audit;

[Authorize(Policy = PermissionCode.AuditView)]
public sealed class IndexModel(WebPassDbContext db) : PageModel
{
    public IReadOnlyList<AuditLog> Entries { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct) =>
        Entries = await db.AuditLogs.AsNoTracking().OrderByDescending(x => x.OccurredAt).Take(500).ToListAsync(ct);
}
