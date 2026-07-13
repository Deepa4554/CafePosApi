using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

[ApiController]
[Route("api/tasks")]
[Authorize(Policy = Policies.RequirePlus)]
public class TasksController(CafePosDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<TaskDto>> List([FromQuery] StaffTaskStatus? status, [FromQuery] int? assignedToId)
    {
        var query = db.Tasks.AsQueryable();
        if (status is not null) query = query.Where(t => t.Status == status);
        if (assignedToId is not null) query = query.Where(t => t.AssignedToId == assignedToId);

        var tasks = await query.OrderByDescending(t => t.CreatedAt).ToListAsync();
        return tasks.Select(TaskDto.From);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<TaskDto>> Get(int id)
    {
        var task = await db.Tasks.FindAsync(id);
        return task is null ? NotFound() : TaskDto.From(task);
    }

    [HttpPost]
    public async Task<ActionResult<TaskDto>> Create(CreateTaskRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Title))
            throw new ApiValidationException("Title is required.");

        var task = new StaffTask
        {
            Title = req.Title.Trim(),
            Description = req.Description,
            AssignedToId = req.AssignedToId,
            Priority = req.Priority,
            DueDate = req.DueDate,
            TagsCsv = req.Tags is { Count: > 0 } ? string.Join(',', req.Tags) : null,
        };
        db.Tasks.Add(task);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = task.Id }, TaskDto.From(task));
    }

    [HttpPatch("{id:int}/status")]
    public async Task<ActionResult<TaskDto>> UpdateStatus(int id, UpdateTaskStatusRequest req)
    {
        var task = await db.Tasks.FindAsync(id);
        if (task is null) return NotFound();

        task.Status = req.Status;
        await db.SaveChangesAsync();
        return TaskDto.From(task);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var task = await db.Tasks.FindAsync(id);
        if (task is null) return NotFound();

        db.Tasks.Remove(task);
        await db.SaveChangesAsync();
        return NoContent();
    }
}
