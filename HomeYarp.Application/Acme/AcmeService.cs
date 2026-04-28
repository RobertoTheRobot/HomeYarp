using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using Certes;
using Certes.Acme;
using Certes.Acme.Resource;
using HomeYarp.Application.Abstractions;
using HomeYarp.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HomeYarp.Application.Acme;

public sealed class AcmeService : IAcmeService
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> CertGates = new();

    private readonly ICertificateRepository _certificates;
    private readonly IAcmeChallengeStore _challengeStore;
    private readonly IAcmeAccountStore _accountStore;
    private readonly IOptionsMonitor<AcmeOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AcmeService> _logger;

    public AcmeService(
        ICertificateRepository certificates,
        IAcmeChallengeStore challengeStore,
        IAcmeAccountStore accountStore,
        IOptionsMonitor<AcmeOptions> options,
        TimeProvider timeProvider,
        ILogger<AcmeService>? logger = null)
    {
        _certificates = certificates;
        _challengeStore = challengeStore;
        _accountStore = accountStore;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger ?? NullLogger<AcmeService>.Instance;
    }

    public Task<Certificate> IssueAsync(
        string name,
        string? friendlyName,
        IReadOnlyList<string> hostnames,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Certificate name is required.", nameof(name));
        }
        if (hostnames is null || hostnames.Count == 0)
        {
            throw new ArgumentException("At least one hostname is required.", nameof(hostnames));
        }
        foreach (var host in hostnames)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                throw new ArgumentException("Hostnames cannot be empty.", nameof(hostnames));
            }
            if (host.StartsWith("*.", StringComparison.Ordinal))
            {
                throw new ArgumentException("Wildcard hostnames require DNS-01, which is not supported.", nameof(hostnames));
            }
        }

        return IssueInternalAsync(name, friendlyName, hostnames, cancellationToken, progress);
    }

    public async Task<Certificate> RenewAsync(Guid certificateId, CancellationToken cancellationToken = default)
    {
        var existing = await _certificates.GetByIdAsync(certificateId, cancellationToken)
            ?? throw new ArgumentException($"Certificate '{certificateId}' not found.", nameof(certificateId));

        if (existing.Acme is null)
        {
            throw new InvalidOperationException($"Certificate '{existing.Name}' is not ACME-managed.");
        }

        return await OrchestrateOrderAsync(
            existing.Id,
            existing.Name,
            existing.FriendlyName,
            existing.Acme.Hostnames.ToList(),
            renewing: true,
            cancellationToken,
            progress: null);
    }

    private async Task<Certificate> IssueInternalAsync(
        string name,
        string? friendlyName,
        IReadOnlyList<string> hostnames,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null)
    {
        progress?.Report($"Checking certificate name uniqueness ('{name}')");
        var existing = await _certificates.GetByNameAsync(name, cancellationToken);
        if (existing is not null)
        {
            throw new InvalidOperationException($"A certificate named '{name}' already exists.");
        }

        return await OrchestrateOrderAsync(
            Guid.NewGuid(),
            name,
            friendlyName,
            hostnames.ToList(),
            renewing: false,
            cancellationToken,
            progress);
    }

    private async Task<Certificate> OrchestrateOrderAsync(
        Guid certificateId,
        string name,
        string? friendlyName,
        List<string> hostnames,
        bool renewing,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null)
    {
        var options = _options.CurrentValue;
        EnsureConfigured(options);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var gate = CertGates.GetOrAdd(certificateId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            _logger.LogInformation(
                "ACME {Action} starting for cert '{Name}' ({Id}) with hosts [{Hosts}] via {Directory}",
                renewing ? "renew" : "issue",
                name,
                certificateId,
                string.Join(",", hostnames),
                options.DirectoryUrl);

            progress?.Report($"Loading or registering ACME account at {options.DirectoryUrl}");
            var (ctx, accountKey, registrationLocation) = await EnsureAccountAsync(options, cancellationToken);

            progress?.Report($"Placing order with {hostnames.Count} authorization(s) for [{string.Join(", ", hostnames)}]");
            _logger.LogDebug(
                "ACME {Action}: placing order for cert '{Name}' ({Id}) — {HostCount} authorization(s) expected",
                renewing ? "renew" : "issue",
                name,
                certificateId,
                hostnames.Count);
            var order = await ctx.NewOrder(hostnames);

            await ValidateChallengesAsync(order, cancellationToken, progress);

            progress?.Report("Generating fresh keypair and CSR");
            var certKey = KeyFactory.NewKey(MapKeyAlgorithm(options.KeyType));

            var csr = new CsrInfo
            {
                CommonName = hostnames[0]
            };

            progress?.Report("Downloading signed certificate chain from CA");
            var chain = await order.Generate(csr, certKey);
            var certificatePem = chain.ToPem();
            var privateKeyPem = certKey.ToPem();

            var metadata = ParseMetadata(certificatePem);
            var now = _timeProvider.GetUtcNow();
            var existing = renewing ? await _certificates.GetByIdAsync(certificateId, cancellationToken) : null;

            var certificate = new Certificate
            {
                Id = certificateId,
                Name = name,
                FriendlyName = friendlyName,
                Subject = metadata.Subject,
                Issuer = metadata.Issuer,
                Thumbprint = metadata.Thumbprint,
                NotBefore = metadata.NotBefore,
                NotAfter = metadata.NotAfter,
                SubjectAlternativeNames = metadata.SubjectAlternativeNames,
                CreatedAt = existing?.CreatedAt ?? now,
                Acme = new AcmeMetadata
                {
                    Hostnames = hostnames,
                    AccountEmail = options.AccountEmail,
                    DirectoryUrl = options.DirectoryUrl,
                    KeyType = options.KeyType,
                    IssuedAt = existing?.Acme?.IssuedAt ?? now,
                    RenewedAt = renewing ? now : (DateTimeOffset?)null
                }
            };

            progress?.Report($"Persisting certificate (PEM + key); valid until {certificate.NotAfter:yyyy-MM-dd}");
            await _certificates.SaveAsync(
                certificate,
                new CertificateMaterial(certificatePem, privateKeyPem),
                cancellationToken);

            stopwatch.Stop();
            progress?.Report($"Done in {stopwatch.Elapsed.TotalSeconds:F1}s");
            _logger.LogInformation(
                "ACME {Action} succeeded for cert '{Name}' ({Id}); thumbprint={Thumbprint} valid until {NotAfter:o} (took {ElapsedMs} ms)",
                renewing ? "renew" : "issue",
                name,
                certificateId,
                certificate.Thumbprint,
                certificate.NotAfter,
                stopwatch.ElapsedMilliseconds);

            return certificate;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(
                ex,
                "ACME {Action} failed for cert '{Name}' ({Id}) after {ElapsedMs} ms",
                renewing ? "renew" : "issue",
                name,
                certificateId,
                stopwatch.ElapsedMilliseconds);
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<(AcmeContext Context, IKey AccountKey, string? RegistrationLocation)> EnsureAccountAsync(
        AcmeOptions options,
        CancellationToken cancellationToken)
    {
        var record = await _accountStore.LoadAsync(options.DirectoryUrl, cancellationToken);
        if (record is not null)
        {
            _logger.LogDebug(
                "ACME account loaded from store for directory {Directory} (email {Email})",
                options.DirectoryUrl,
                record.Email);
            var existingKey = KeyFactory.FromPem(record.KeyPem);
            var ctx = new AcmeContext(new Uri(options.DirectoryUrl), existingKey);
            return (ctx, existingKey, record.RegistrationLocation);
        }

        _logger.LogInformation(
            "ACME account not found for directory {Directory}; registering new account for {Email}",
            options.DirectoryUrl,
            options.AccountEmail);

        var accountKey = KeyFactory.NewKey(KeyAlgorithm.ES256);
        var newCtx = new AcmeContext(new Uri(options.DirectoryUrl), accountKey);
        var account = await newCtx.NewAccount(options.AccountEmail, termsOfServiceAgreed: true);
        var location = account.Location?.ToString();

        await _accountStore.SaveAsync(new AcmeAccountRecord(
            options.DirectoryUrl,
            options.AccountEmail,
            accountKey.ToPem(),
            location,
            _timeProvider.GetUtcNow()), cancellationToken);

        _logger.LogInformation("Registered new ACME account at {Directory} as {Email}", options.DirectoryUrl, options.AccountEmail);
        return (newCtx, accountKey, location);
    }

    private async Task ValidateChallengesAsync(IOrderContext order, CancellationToken cancellationToken, IProgress<string>? progress = null)
    {
        var authzs = await order.Authorizations();

        var publishedTokens = new List<string>();
        try
        {
            // Publish all challenges first, then validate, so the ACME server can poll any of them.
            progress?.Report("Publishing HTTP-01 challenge tokens");
            var challenges = new List<(IChallengeContext Challenge, string Token)>();
            foreach (var authz in authzs)
            {
                var http = await authz.Http()
                    ?? throw new InvalidOperationException("ACME authorization did not offer an HTTP-01 challenge.");
                _challengeStore.Publish(http.Token, http.KeyAuthz);
                publishedTokens.Add(http.Token);
                challenges.Add((http, http.Token));
                _logger.LogDebug("ACME HTTP-01 challenge published (token '{Token}')", http.Token);
            }

            progress?.Report($"Asking Let's Encrypt to validate {challenges.Count} challenge(s)");
            foreach (var (challenge, token) in challenges)
            {
                _logger.LogDebug("ACME asking server to validate challenge token '{Token}'", token);
                await challenge.Validate();
            }

            // Poll each challenge until valid/invalid, with capped backoff.
            for (var i = 0; i < challenges.Count; i++)
            {
                var (challenge, token) = challenges[i];
                progress?.Report($"Polling challenge {i + 1}/{challenges.Count} until validated");
                Challenge resource = await challenge.Resource();
                var delay = TimeSpan.FromSeconds(2);
                var ceiling = TimeSpan.FromSeconds(8);
                var deadline = _timeProvider.GetUtcNow() + TimeSpan.FromMinutes(2);
                while (resource.Status is ChallengeStatus.Pending or ChallengeStatus.Processing)
                {
                    if (_timeProvider.GetUtcNow() > deadline)
                    {
                        throw new TimeoutException($"ACME HTTP-01 challenge for token '{token}' timed out.");
                    }
                    await Task.Delay(delay, cancellationToken);
                    delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, ceiling.Ticks));
                    resource = await challenge.Resource();
                }

                if (resource.Status != ChallengeStatus.Valid)
                {
                    var detail = resource.Error?.Detail ?? "unknown";
                    _logger.LogError("ACME HTTP-01 challenge token '{Token}' failed: {Detail}", token, detail);
                    progress?.Report($"Challenge {i + 1} failed: {detail}");
                    throw new InvalidOperationException($"ACME HTTP-01 challenge failed: {detail}");
                }

                progress?.Report($"Challenge {i + 1}/{challenges.Count} validated");
                _logger.LogDebug("ACME HTTP-01 challenge token '{Token}' validated", token);
            }

            // Wait for the order itself to become ready/valid before generating.
            progress?.Report("Waiting for order to become ready");
            Order orderResource = await order.Resource();
            var orderDelay = TimeSpan.FromSeconds(1);
            var orderCeiling = TimeSpan.FromSeconds(8);
            var orderDeadline = _timeProvider.GetUtcNow() + TimeSpan.FromMinutes(1);
            while (orderResource.Status is OrderStatus.Pending or OrderStatus.Processing)
            {
                if (_timeProvider.GetUtcNow() > orderDeadline)
                {
                    throw new TimeoutException("ACME order did not become ready in time.");
                }
                await Task.Delay(orderDelay, cancellationToken);
                orderDelay = TimeSpan.FromTicks(Math.Min(orderDelay.Ticks * 2, orderCeiling.Ticks));
                orderResource = await order.Resource();
            }

            if (orderResource.Status is OrderStatus.Invalid)
            {
                throw new InvalidOperationException("ACME order entered Invalid state after challenge validation.");
            }
        }
        finally
        {
            foreach (var token in publishedTokens)
            {
                _challengeStore.Remove(token);
            }
        }
    }

    private static CertificateMetadataSnapshot ParseMetadata(string certificatePem)
    {
        using var x509 = X509Certificate2.CreateFromPem(certificatePem);
        var sans = x509.Extensions
            .OfType<X509SubjectAlternativeNameExtension>()
            .SelectMany(e => e.EnumerateDnsNames())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new CertificateMetadataSnapshot(
            x509.Subject,
            x509.Issuer,
            x509.Thumbprint,
            new DateTimeOffset(x509.NotBefore.ToUniversalTime(), TimeSpan.Zero),
            new DateTimeOffset(x509.NotAfter.ToUniversalTime(), TimeSpan.Zero),
            sans);
    }

    private static KeyAlgorithm MapKeyAlgorithm(AcmeKeyType type) => type switch
    {
        AcmeKeyType.Rsa2048 => KeyAlgorithm.RS256,
        _ => KeyAlgorithm.ES256
    };

    private static void EnsureConfigured(AcmeOptions options) => AcmeOptionsValidator.EnsureConfigured(options);

    private sealed record CertificateMetadataSnapshot(
        string Subject,
        string Issuer,
        string Thumbprint,
        DateTimeOffset NotBefore,
        DateTimeOffset NotAfter,
        List<string> SubjectAlternativeNames);
}
