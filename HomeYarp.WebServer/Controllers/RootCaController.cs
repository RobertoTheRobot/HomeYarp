using System.Text;
using HomeYarp.Application.RootCa;
using HomeYarp.WebServer.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace HomeYarp.WebServer.Controllers;

/// <summary>
/// The instance's private root CA. Only the PUBLIC certificate is ever served; install it once per device and
/// every Internal certificate HomeYarp issues (now or later) is trusted.
/// </summary>
[ApiController]
[Route("api/ca")]
public sealed class RootCaController : ControllerBase
{
    private readonly IRootCaService _rootCa;
    private readonly ILogger<RootCaController> _logger;

    public RootCaController(IRootCaService rootCa, ILogger<RootCaController> logger)
    {
        _rootCa = rootCa;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<RootCaResponse>> Info(CancellationToken cancellationToken)
    {
        var ca = await _rootCa.EnsureInitializedAsync(cancellationToken);
        return Ok(RootCaResponse.From(ca));
    }

    [HttpGet("root")]
    public async Task<IActionResult> Download(CancellationToken cancellationToken)
    {
        var pem = await _rootCa.GetRootCertificatePemAsync(cancellationToken);
        _logger.LogInformation("API root CA certificate downloaded");
        return File(Encoding.UTF8.GetBytes(pem), "application/x-pem-file", "homeyarp-root-ca.crt");
    }
}
