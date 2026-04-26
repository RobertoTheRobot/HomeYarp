using HomeYarp.Application.Services;
using HomeYarp.WebServer.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace HomeYarp.WebServer.Controllers;

[ApiController]
[Route("api/applications")]
public sealed class ApplicationsController : ControllerBase
{
    private readonly IApplicationService _service;

    public ApplicationsController(IApplicationService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ApplicationResponse>>> List(CancellationToken cancellationToken)
    {
        var apps = await _service.ListAsync(cancellationToken);
        return Ok(apps.Select(ApplicationDtoMapper.ToResponse).ToList());
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApplicationResponse>> Get(Guid id, CancellationToken cancellationToken)
    {
        var app = await _service.GetAsync(id, cancellationToken);
        return app is null ? NotFound() : Ok(ApplicationDtoMapper.ToResponse(app));
    }

    [HttpPost]
    public async Task<ActionResult<ApplicationResponse>> Create([FromBody] ApplicationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var created = await _service.CreateAsync(ApplicationDtoMapper.ToDomain(request), cancellationToken);
            return CreatedAtAction(nameof(Get), new { id = created.Id }, ApplicationDtoMapper.ToResponse(created));
        }
        catch (ArgumentException ex)
        {
            return ValidationProblem(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ApplicationResponse>> Update(Guid id, [FromBody] ApplicationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var updated = await _service.UpdateAsync(id, ApplicationDtoMapper.ToDomain(request), cancellationToken);
            return Ok(ApplicationDtoMapper.ToResponse(updated));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException ex)
        {
            return ValidationProblem(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var removed = await _service.DeleteAsync(id, cancellationToken);
        return removed ? NoContent() : NotFound();
    }
}
