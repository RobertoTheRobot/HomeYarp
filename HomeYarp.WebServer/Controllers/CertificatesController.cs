using HomeYarp.Application.Abstractions;
using HomeYarp.Application.Acme;
using HomeYarp.Application.Services;
using HomeYarp.WebServer.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace HomeYarp.WebServer.Controllers;

[ApiController]
[Route("api/certificates")]
public sealed class CertificatesController : ControllerBase
{
    private readonly ICertificateService _service;
    private readonly IAcmeService _acme;

    public CertificatesController(ICertificateService service, IAcmeService acme)
    {
        _service = service;
        _acme = acme;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<CertificateResponse>>> List(CancellationToken cancellationToken)
    {
        var certs = await _service.ListAsync(cancellationToken);
        return Ok(certs.Select(CertificateDtoMapper.ToResponse).ToList());
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<CertificateResponse>> Get(Guid id, CancellationToken cancellationToken)
    {
        var cert = await _service.GetAsync(id, cancellationToken);
        return cert is null ? NotFound() : Ok(CertificateDtoMapper.ToResponse(cert));
    }

    [HttpPost]
    public async Task<ActionResult<CertificateResponse>> Upload([FromBody] CertificateUploadRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var material = new CertificateMaterial(request.CertificatePem, request.PrivateKeyPem);
            var created = await _service.UploadAsync(request.Name, request.FriendlyName, material, cancellationToken);
            return CreatedAtAction(nameof(Get), new { id = created.Id }, CertificateDtoMapper.ToResponse(created));
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

    [HttpPost("acme")]
    public async Task<ActionResult<CertificateResponse>> IssueViaAcme([FromBody] AcmeIssueRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var created = await _acme.IssueAsync(request.Name, request.FriendlyName, request.Hostnames ?? Array.Empty<string>(), cancellationToken);
            return CreatedAtAction(nameof(Get), new { id = created.Id }, CertificateDtoMapper.ToResponse(created));
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

    [HttpPost("{id:guid}/renew")]
    public async Task<ActionResult<CertificateResponse>> Renew(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var renewed = await _acme.RenewAsync(id, cancellationToken);
            return Ok(CertificateDtoMapper.ToResponse(renewed));
        }
        catch (ArgumentException ex)
        {
            return NotFound(new { message = ex.Message });
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
