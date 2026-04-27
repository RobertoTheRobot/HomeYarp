using HomeYarp.Application.Abstractions;
using HomeYarp.Application.Acme;
using HomeYarp.Application.SelfSigned;
using HomeYarp.Application.Services;
using HomeYarp.Domain;
using HomeYarp.WebServer.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace HomeYarp.WebServer.Controllers;

[ApiController]
[Route("api/certificates")]
public sealed class CertificatesController : ControllerBase
{
    private readonly ICertificateService _service;
    private readonly IAcmeService _acme;
    private readonly ISelfSignedCertificateService _selfSigned;
    private readonly ILogger<CertificatesController> _logger;

    public CertificatesController(
        ICertificateService service,
        IAcmeService acme,
        ISelfSignedCertificateService selfSigned,
        ILogger<CertificatesController> logger)
    {
        _service = service;
        _acme = acme;
        _selfSigned = selfSigned;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<CertificateResponse>>> List(CancellationToken cancellationToken)
    {
        var certs = await _service.ListAsync(cancellationToken);
        _logger.LogDebug("API list certificates → {Count} result(s)", certs.Count);
        return Ok(certs.Select(CertificateDtoMapper.ToResponse).ToList());
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<CertificateResponse>> Get(Guid id, CancellationToken cancellationToken)
    {
        var cert = await _service.GetAsync(id, cancellationToken);
        if (cert is null)
        {
            _logger.LogDebug("API get certificate {CertId} → 404", id);
            return NotFound();
        }
        return Ok(CertificateDtoMapper.ToResponse(cert));
    }

    [HttpPost]
    public async Task<ActionResult<CertificateResponse>> Upload([FromBody] CertificateUploadRequest request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("API upload certificate: '{CertName}'", request.Name);
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
        _logger.LogInformation("API ACME issue: '{CertName}' for [{Hostnames}]", request.Name, string.Join(",", request.Hostnames ?? Array.Empty<string>()));
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

    [HttpPost("self-signed")]
    public async Task<ActionResult<CertificateResponse>> IssueSelfSigned([FromBody] SelfSignedIssueRequest request, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "API self-signed issue: '{CertName}' for [{Hostnames}] keyType={KeyType} validityDays={ValidityDays}",
            request.Name,
            string.Join(",", request.Hostnames ?? Array.Empty<string>()),
            request.KeyType,
            request.ValidityDays);
        try
        {
            var created = await _selfSigned.IssueAsync(
                request.Name,
                request.FriendlyName,
                request.Hostnames ?? Array.Empty<string>(),
                request.KeyType ?? CertificateKeyType.Ec256,
                request.ValidityDays ?? 365,
                cancellationToken);
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

    [HttpPost("{id:guid}/regenerate")]
    public async Task<ActionResult<CertificateResponse>> Regenerate(Guid id, CancellationToken cancellationToken)
    {
        _logger.LogInformation("API regenerate self-signed cert {CertId}", id);
        try
        {
            var regenerated = await _selfSigned.RegenerateAsync(id, cancellationToken);
            return Ok(CertificateDtoMapper.ToResponse(regenerated));
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

    [HttpPost("{id:guid}/renew")]
    public async Task<ActionResult<CertificateResponse>> Renew(Guid id, CancellationToken cancellationToken)
    {
        _logger.LogInformation("API renew ACME cert {CertId}", id);
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
        _logger.LogInformation("API delete certificate {CertId}", id);
        var removed = await _service.DeleteAsync(id, cancellationToken);
        return removed ? NoContent() : NotFound();
    }
}
