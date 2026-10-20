using System.Text;
using HomeYarp.Application.RootCa;
using HomeYarp.WebServer.Dtos;
using HomeYarp.WebServer.RootCa;
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

    // Self-contained installers (embed the public cert, verify its SHA-256, then trust it). Linked from the /trust page.
    [HttpGet("install/windows.ps1")]
    public async Task<IActionResult> WindowsPowerShell(CancellationToken cancellationToken)
        => Text(RootCaInstallScripts.WindowsPowerShell(await GetCaAsync(cancellationToken)), "install-homeyarp-ca.ps1");

    [HttpGet("install/windows.cmd")]
    public IActionResult WindowsCmd()
        => Text(RootCaInstallScripts.WindowsCmd(RootCaInstallScripts.SafeBaseUrl(Request.Scheme, Request.Host.Value)), "install-homeyarp-ca.cmd");

    [HttpGet("install/linux.sh")]
    public async Task<IActionResult> LinuxMacSh(CancellationToken cancellationToken)
        => Text(RootCaInstallScripts.LinuxMacSh(await GetCaAsync(cancellationToken)), "install-homeyarp-ca.sh");

    private async Task<RootCaResponse> GetCaAsync(CancellationToken cancellationToken)
        => RootCaResponse.From(await _rootCa.EnsureInitializedAsync(cancellationToken));

    private FileContentResult Text(string content, string fileName)
    {
        _logger.LogInformation("API root CA installer served: {FileName}", fileName);
        return File(new UTF8Encoding(false).GetBytes(content), "text/plain; charset=utf-8", fileName);
    }
}
