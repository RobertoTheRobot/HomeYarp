using HomeYarp.Application.Services;
using HomeYarp.WebServer.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace HomeYarp.WebServer.Controllers;

[ApiController]
[Route("api/applications")]
public sealed class ApplicationsController : ControllerBase
{
    private readonly IApplicationService _service;
    private readonly ILogger<ApplicationsController> _logger;

    public ApplicationsController(IApplicationService service, ILogger<ApplicationsController> logger)
    {
        _service = service;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ApplicationResponse>>> List(CancellationToken cancellationToken)
    {
        var apps = await _service.ListAsync(cancellationToken);
        _logger.LogDebug("API list applications → {Count} result(s)", apps.Count);
        return Ok(apps.Select(ApplicationDtoMapper.ToResponse).ToList());
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApplicationResponse>> Get(Guid id, CancellationToken cancellationToken)
    {
        var app = await _service.GetAsync(id, cancellationToken);
        if (app is null)
        {
            _logger.LogDebug("API get application {AppId} → 404", id);
            return NotFound();
        }
        _logger.LogDebug("API get application {AppId} → '{AppName}'", id, app.Name);
        return Ok(ApplicationDtoMapper.ToResponse(app));
    }

    [HttpPost]
    public async Task<ActionResult<ApplicationResponse>> Create([FromBody] ApplicationRequest request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("API create application: '{AppName}'", request.Name);
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
        _logger.LogInformation("API update application {AppId}: '{AppName}'", id, request.Name);
        try
        {
            var updated = await _service.UpdateAsync(id, ApplicationDtoMapper.ToDomain(request), cancellationToken);
            return Ok(ApplicationDtoMapper.ToResponse(updated));
        }
        catch (KeyNotFoundException)
        {
            _logger.LogWarning("API update application {AppId} → 404 (not found)", id);
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
        _logger.LogInformation("API delete application {AppId}", id);
        var removed = await _service.DeleteAsync(id, cancellationToken);
        return removed ? NoContent() : NotFound();
    }
}
