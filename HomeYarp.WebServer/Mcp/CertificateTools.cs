using System.ComponentModel;
using HomeYarp.Application.Abstractions;
using HomeYarp.Application.Acme;
using HomeYarp.Application.SelfSigned;
using HomeYarp.Application.Services;
using HomeYarp.Domain;
using HomeYarp.WebServer.Dtos;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace HomeYarp.WebServer.Mcp;

[McpServerToolType]
public sealed class CertificateTools
{
    private readonly ICertificateService _service;
    private readonly IAcmeService _acme;
    private readonly ISelfSignedCertificateService _selfSigned;
    private readonly ILogger<CertificateTools> _logger;

    public CertificateTools(
        ICertificateService service,
        IAcmeService acme,
        ISelfSignedCertificateService selfSigned,
        ILogger<CertificateTools> logger)
    {
        _service = service;
        _acme = acme;
        _selfSigned = selfSigned;
        _logger = logger;
    }

    [McpServerTool(Name = "list_certificates")]
    [Description("List all TLS certificates stored in HomeYarp.")]
    public async Task<IReadOnlyList<CertificateResponse>> ListCertificates(
        CancellationToken cancellationToken = default)
    {
        var certs = await _service.ListAsync(cancellationToken);
        _logger.LogDebug("MCP list certificates → {Count} result(s)", certs.Count);
        return certs.Select(CertificateDtoMapper.ToResponse).ToList();
    }

    [McpServerTool(Name = "get_certificate")]
    [Description("Get a single TLS certificate by its GUID identifier.")]
    public async Task<CertificateResponse> GetCertificate(
        [Description("The GUID of the certificate to retrieve.")] Guid id,
        CancellationToken cancellationToken = default)
    {
        var cert = await _service.GetAsync(id, cancellationToken);
        if (cert is null)
        {
            _logger.LogDebug("MCP get certificate {CertId} → not found", id);
            throw new McpException($"Certificate '{id}' was not found.");
        }
        return CertificateDtoMapper.ToResponse(cert);
    }

    [McpServerTool(Name = "download_certificate_pem")]
    [Description(
        "Download the public certificate chain PEM for a certificate (no private key). " +
        "Useful for trusting a self-signed certificate in a browser or OS trust store.")]
    public async Task<string> DownloadCertificatePem(
        [Description("The GUID of the certificate whose PEM to download.")] Guid id,
        CancellationToken cancellationToken = default)
    {
        var cert = await _service.GetAsync(id, cancellationToken);
        if (cert is null)
        {
            _logger.LogDebug("MCP download certificate {CertId} → not found", id);
            throw new McpException($"Certificate '{id}' was not found.");
        }
        var pem = await _service.GetCertificatePemAsync(id, cancellationToken);
        if (pem is null)
        {
            _logger.LogWarning("MCP download certificate {CertId}: metadata exists but PEM material missing", id);
            throw new McpException($"Certificate '{id}' metadata exists but PEM material is missing.");
        }
        return pem;
    }

    [McpServerTool(Name = "upload_certificate")]
    [Description("Upload a manually-provided TLS certificate (PEM format). Both the certificate chain and private key are required.")]
    public async Task<CertificateResponse> UploadCertificate(
        [Description("Unique slug name for the certificate.")] string name,
        [Description("Optional human-friendly display name.")] string? friendlyName,
        [Description("The certificate chain in PEM format (BEGIN CERTIFICATE blocks).")] string certificatePem,
        [Description("The private key in PEM format (BEGIN PRIVATE KEY block).")] string privateKeyPem,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("MCP upload certificate: '{CertName}'", name);
        try
        {
            var material = new CertificateMaterial(certificatePem, privateKeyPem);
            var created = await _service.UploadAsync(name, friendlyName, material, cancellationToken);
            return CertificateDtoMapper.ToResponse(created);
        }
        catch (ArgumentException ex)
        {
            throw new McpException($"Validation failed: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "issue_self_signed_certificate")]
    [Description(
        "Generate a new self-signed TLS certificate. " +
        "Hostnames may include DNS names (e.g. 'app.home.lan'), wildcard names (e.g. '*.home.lan'), and IP addresses. " +
        "keyType: 0=Ec256 (default, recommended), 1=Rsa2048. " +
        "validityDays: how long the cert is valid (default 365).")]
    public async Task<CertificateResponse> IssueSelfSignedCertificate(
        [Description("Unique slug name for the certificate.")] string name,
        [Description("Optional human-friendly display name.")] string? friendlyName,
        [Description("List of hostnames/IPs/wildcards to include as Subject Alternative Names.")] IReadOnlyList<string> hostnames,
        [Description("Key type: 0=Ec256 (default), 1=Rsa2048.")] CertificateKeyType? keyType,
        [Description("Validity period in days (default 365).")] int? validityDays,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "MCP self-signed issue: '{CertName}' for [{Hostnames}] keyType={KeyType} validityDays={ValidityDays}",
            name,
            string.Join(",", hostnames ?? []),
            keyType,
            validityDays);
        try
        {
            var created = await _selfSigned.IssueAsync(
                name,
                friendlyName,
                hostnames ?? [],
                keyType ?? CertificateKeyType.Ec256,
                validityDays ?? 365,
                cancellationToken);
            return CertificateDtoMapper.ToResponse(created);
        }
        catch (ArgumentException ex)
        {
            throw new McpException($"Validation failed: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "regenerate_certificate")]
    [Description(
        "Regenerate a self-signed certificate: same GUID and name, fresh key and expiry, same hostnames. " +
        "Only works on HomeYarp-generated (self-signed) certificates.")]
    public async Task<CertificateResponse> RegenerateCertificate(
        [Description("The GUID of the self-signed certificate to regenerate.")] Guid id,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("MCP regenerate self-signed cert {CertId}", id);
        try
        {
            var regenerated = await _selfSigned.RegenerateAsync(id, cancellationToken);
            return CertificateDtoMapper.ToResponse(regenerated);
        }
        catch (ArgumentException ex)
        {
            throw new McpException($"Certificate '{id}' was not found. {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "issue_acme_certificate")]
    [Description(
        "Issue a TLS certificate from Let's Encrypt via ACME HTTP-01 challenge. " +
        "Requires ACME to be configured (AccountEmail, AgreeToTermsOfService = true) in HomeYarp settings. " +
        "Wildcard hostnames are not supported (HTTP-01 limitation). " +
        "Inbound port 80 must reach HomeYarp's HTTP listener for the challenge to succeed.")]
    public async Task<CertificateResponse> IssueAcmeCertificate(
        [Description("Unique slug name for the certificate.")] string name,
        [Description("Optional human-friendly display name.")] string? friendlyName,
        [Description("Hostnames to include. No wildcards. Must be publicly reachable for HTTP-01 challenge.")] IReadOnlyList<string> hostnames,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("MCP ACME issue: '{CertName}' for [{Hostnames}]", name, string.Join(",", hostnames ?? []));
        try
        {
            var created = await _acme.IssueAsync(name, friendlyName, hostnames ?? [], cancellationToken);
            return CertificateDtoMapper.ToResponse(created);
        }
        catch (ArgumentException ex)
        {
            throw new McpException($"Validation failed: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "renew_certificate")]
    [Description("Renew an ACME-managed (Let's Encrypt) certificate. Only works on ACME-issued certificates.")]
    public async Task<CertificateResponse> RenewCertificate(
        [Description("The GUID of the ACME certificate to renew.")] Guid id,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("MCP renew ACME cert {CertId}", id);
        try
        {
            var renewed = await _acme.RenewAsync(id, cancellationToken);
            return CertificateDtoMapper.ToResponse(renewed);
        }
        catch (ArgumentException ex)
        {
            throw new McpException($"Certificate '{id}' was not found. {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "delete_certificate")]
    [Description("Delete a TLS certificate by its GUID.")]
    public async Task<string> DeleteCertificate(
        [Description("The GUID of the certificate to delete.")] Guid id,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("MCP delete certificate {CertId}", id);
        var removed = await _service.DeleteAsync(id, cancellationToken);
        if (!removed)
            throw new McpException($"Certificate '{id}' was not found.");
        return $"Certificate '{id}' deleted.";
    }
}
