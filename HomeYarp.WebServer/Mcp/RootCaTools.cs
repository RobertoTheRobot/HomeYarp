using System.ComponentModel;
using HomeYarp.Application.RootCa;
using HomeYarp.WebServer.Dtos;
using ModelContextProtocol.Server;

namespace HomeYarp.WebServer.Mcp;

[McpServerToolType]
public sealed class RootCaTools
{
    private readonly IRootCaService _rootCa;
    private readonly ILogger<RootCaTools> _logger;

    public RootCaTools(IRootCaService rootCa, ILogger<RootCaTools> logger)
    {
        _rootCa = rootCa;
        _logger = logger;
    }

    [McpServerTool(Name = "get_root_ca")]
    [Description("Get HomeYarp's private root CA (public certificate only, never the key): subject, thumbprint, SHA-256 fingerprint, validity and the PEM. Install this one certificate in a device's trust store and every 'Internal' (HomeYarp-signed) certificate is trusted.")]
    public async Task<RootCaResponse> GetRootCa(CancellationToken cancellationToken = default)
    {
        var ca = await _rootCa.EnsureInitializedAsync(cancellationToken);
        _logger.LogDebug("MCP get root CA → {Thumbprint}", ca.Thumbprint);
        return RootCaResponse.From(ca);
    }
}
