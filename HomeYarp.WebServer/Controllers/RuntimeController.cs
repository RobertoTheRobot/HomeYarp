using HomeYarp.Application.Services;
using Microsoft.AspNetCore.Mvc;

namespace HomeYarp.WebServer.Controllers;

[ApiController]
[Route("api/runtime")]
public sealed class RuntimeController : ControllerBase
{
    private readonly IRuntimeReloadService _reload;
    private readonly ILogger<RuntimeController> _logger;

    public RuntimeController(IRuntimeReloadService reload, ILogger<RuntimeController> logger)
    {
        _reload = reload;
        _logger = logger;
    }

    /// <summary>
    /// Re-applies on-disk applications + certificates to the live YARP route table and SNI cert map.
    /// Saves no longer trigger reload automatically — call this after a batch of edits.
    /// </summary>
    [HttpPost("reload")]
    public async Task<ActionResult<RuntimeReloadResponse>> Reload(CancellationToken cancellationToken)
    {
        _logger.LogInformation("API runtime reload requested");
        await _reload.ReloadAsync(progress: null, cancellationToken);
        return Ok(new RuntimeReloadResponse(_reload.LastReloadedAt));
    }

    [HttpGet]
    public ActionResult<RuntimeReloadResponse> Status()
        => Ok(new RuntimeReloadResponse(_reload.LastReloadedAt));
}

public sealed record RuntimeReloadResponse(DateTimeOffset? LastReloadedAt);
