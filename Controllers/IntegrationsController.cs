using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

[ApiController]
[Route("api/integrations")]
[Authorize(Policy = Policies.OwnerOrManager)]
[Authorize(Policy = Policies.RequirePremium)]
public class IntegrationsController(CafePosDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<IntegrationDto>> List()
    {
        var integrations = await db.Integrations.OrderBy(i => i.Category).ThenBy(i => i.Name).ToListAsync();
        return integrations.Select(IntegrationDto.From);
    }

    [HttpPost("{id:int}/connect")]
    public async Task<ActionResult<IntegrationDto>> Connect(int id)
    {
        var integration = await db.Integrations.FindAsync(id);
        if (integration is null) return NotFound();

        integration.Status = IntegrationStatus.Connected;
        integration.ConnectedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return IntegrationDto.From(integration);
    }

    [HttpPost("{id:int}/disconnect")]
    public async Task<ActionResult<IntegrationDto>> Disconnect(int id)
    {
        var integration = await db.Integrations.FindAsync(id);
        if (integration is null) return NotFound();

        integration.Status = IntegrationStatus.Disconnected;
        integration.ConnectedAt = null;
        await db.SaveChangesAsync();
        return IntegrationDto.From(integration);
    }
}
