using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HomeYarp.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HomeYarp.Application.RootCa;

public sealed class RootCaService : IRootCaService
{
    private readonly IRootCaStore _store;
    private readonly IOptionsMonitor<RootCaOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RootCaService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private X509Certificate2? _caWithKey;
    private ECDsa? _caKey;
    private RootCertificateAuthority? _info;

    public RootCaService(
        IRootCaStore store,
        IOptionsMonitor<RootCaOptions> options,
        TimeProvider timeProvider,
        ILogger<RootCaService>? logger = null)
    {
        _store = store;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger ?? NullLogger<RootCaService>.Instance;
    }

    public async Task<RootCertificateAuthority> EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        await LoadOrCreateAsync(cancellationToken);
        return _info!;
    }

    public async Task<string> GetRootCertificatePemAsync(CancellationToken cancellationToken = default)
        => (await EnsureInitializedAsync(cancellationToken)).CertificatePem;

    public async Task<X509Certificate2> SignLeafAsync(
        CertificateRequest request,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter,
        CancellationToken cancellationToken = default)
    {
        var ca = await LoadOrCreateAsync(cancellationToken);
        var caNotAfter = new DateTimeOffset(ca.NotAfter.ToUniversalTime(), TimeSpan.Zero);
        if (notAfter > caNotAfter)
        {
            _logger.LogWarning("Leaf notAfter {NotAfter:o} exceeds root CA expiry {CaNotAfter:o}; clamping.", notAfter, caNotAfter);
            notAfter = caNotAfter;
        }

        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(
            ca, includeKeyIdentifier: true, includeIssuerAndSerial: false));
        // Explicit generator: a leaf request may be RSA while the CA key is ECDSA, which the plain overload rejects.
        var generator = X509SignatureGenerator.CreateForECDsa(_caKey!);
        return request.Create(ca.SubjectName, generator, notBefore, notAfter, NewSerial());
    }

    private async Task<X509Certificate2> LoadOrCreateAsync(CancellationToken cancellationToken)
    {
        if (_caWithKey is not null)
        {
            return _caWithKey;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_caWithKey is not null)
            {
                return _caWithKey;
            }

            var material = await _store.LoadAsync(cancellationToken);
            if (material is null)
            {
                material = Generate();
                await _store.SaveAsync(material, cancellationToken);
                _logger.LogWarning(
                    "Generated a NEW HomeYarp root CA. Every client must (re)install it: GET /api/ca/root on the management port.");
            }

            var ca = X509Certificate2.CreateFromPem(material.CertificatePem, material.PrivateKeyPem);
            _info = new RootCertificateAuthority(
                ca.ExportCertificatePem(),
                ca.Subject,
                ca.Thumbprint,
                new DateTimeOffset(ca.NotBefore.ToUniversalTime(), TimeSpan.Zero),
                new DateTimeOffset(ca.NotAfter.ToUniversalTime(), TimeSpan.Zero));
            _caKey = ca.GetECDsaPrivateKey() ?? throw new InvalidOperationException("Root CA key is not an ECDSA key.");
            _caWithKey = ca;

            _logger.LogInformation(
                "Root CA ready: subject='{Subject}' thumbprint={Thumbprint} valid {NotBefore:o} → {NotAfter:o}",
                _info.Subject, _info.Thumbprint, _info.NotBefore, _info.NotAfter);
            if (_info.NotAfter - _timeProvider.GetUtcNow() < TimeSpan.FromDays(365))
            {
                _logger.LogWarning("Root CA expires within a year ({NotAfter:o}); plan a rotation.", _info.NotAfter);
            }
            return ca;
        }
        finally
        {
            _gate.Release();
        }
    }

    private RootCaMaterial Generate()
    {
        var options = _options.CurrentValue;
        var now = _timeProvider.GetUtcNow();

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            new X500DistinguishedName($"CN={options.CommonName}"), key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: true, hasPathLengthConstraint: true, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        using var cert = request.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(options.ValidityDays));
        return new RootCaMaterial(cert.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem());
    }

    private static byte[] NewSerial()
    {
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F; // positive
        serial[0] |= 0x40; // never leading-zero, so the encoded length is stable
        return serial;
    }
}
