using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

/// <summary>Read-only — entries are written internally via IAuditService, never by clients.</summary>
[ApiController]
[Route("api/audit-log")]
[Authorize(Policy = Policies.OwnerOrManager)]
public class AuditController(CafePosDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<PagedResult<AuditEntryDto>> List(
        [FromQuery] AuditSeverity? severity, [FromQuery] AuditResource? resource,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var query = db.AuditLog.AsQueryable();
        if (severity is not null) query = query.Where(a => a.Severity == severity);
        if (resource is not null) query = query.Where(a => a.Resource == resource);

        var paged = await query.OrderByDescending(a => a.Timestamp).ToPagedResultAsync(page, pageSize);
        return new PagedResult<AuditEntryDto>(paged.Items.Select(AuditEntryDto.From).ToList(), paged.Page, paged.PageSize, paged.TotalCount);
    }
}
