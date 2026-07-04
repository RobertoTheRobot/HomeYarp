using System.ComponentModel;
using HomeYarp.Application.Services;
using HomeYarp.WebServer.Dtos;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace HomeYarp.WebServer.Mcp;

[McpServerToolType]
public sealed class ApplicationTools
{
    private readonly IApplicationService _service;
    private readonly ILogger<ApplicationTools> _logger;

    public ApplicationTools(IApplicationService service, ILogger<ApplicationTools> logger)
    {
        _service = service;
        _logger = logger;
    }

    [McpServerTool(Name = "list_applications")]
    [Description("List all proxy applications configured in HomeYarp.")]
    public async Task<IReadOnlyList<ApplicationResponse>> ListApplications(
        CancellationToken cancellationToken = default)
    {
        var apps = await _service.ListAsync(cancellationToken);
        _logger.LogDebug("MCP list applications → {Count} result(s)", apps.Count);
        return apps.Select(ApplicationDtoMapper.ToResponse).ToList();
    }

    [McpServerTool(Name = "get_application")]
    [Description("Get a single proxy application by its GUID identifier.")]
    public async Task<ApplicationResponse> GetApplication(
        [Description("The GUID of the application to retrieve.")] Guid id,
        CancellationToken cancellationToken = default)
    {
        var app = await _service.GetAsync(id, cancellationToken);
        if (app is null)
        {
            _logger.LogDebug("MCP get application {AppId} → not found", id);
            throw new McpException($"Application '{id}' was not found.");
        }
        _logger.LogDebug("MCP get application {AppId} → '{AppName}'", id, app.Name);
        return ApplicationDtoMapper.ToResponse(app);
    }

    [McpServerTool(Name = "create_application")]
    [Description(
        "Create a new proxy application. " +
        "The 'name' field is a unique slug used in YARP route/cluster IDs. " +
        "'routes' is a list of {hosts, path, order} entries (hosts is a list of hostnames). " +
        "'cluster' holds a list of destinations each with a 'name' and absolute-URI 'address'. " +
        "'tls' block controls TLS: mode 0=None, 1=Offload (proxy terminates), 2=Passthrough (raw TCP); " +
        "source 0=Manual (you supply certificateId), 1=Internal (HomeYarp self-signs), 2=External (Let's Encrypt). " +
        "When source is Internal or External, certificateId is populated automatically by the server.")]
    public async Task<ApplicationResponse> CreateApplication(
        [Description("The application definition to create.")] ApplicationRequest request,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("MCP create application: '{AppName}'", request.Name);
        try
        {
            var created = await _service.CreateAsync(ApplicationDtoMapper.ToDomain(request), cancellationToken);
            return ApplicationDtoMapper.ToResponse(created);
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

    [McpServerTool(Name = "update_application")]
    [Description("Update an existing proxy application by its GUID. All fields in the request replace the current state.")]
    public async Task<ApplicationResponse> UpdateApplication(
        [Description("The GUID of the application to update.")] Guid id,
        [Description("The updated application definition.")] ApplicationRequest request,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("MCP update application {AppId}: '{AppName}'", id, request.Name);
        try
        {
            var updated = await _service.UpdateAsync(id, ApplicationDtoMapper.ToDomain(request), cancellationToken);
            return ApplicationDtoMapper.ToResponse(updated);
        }
        catch (KeyNotFoundException)
        {
            _logger.LogWarning("MCP update application {AppId} → not found", id);
            throw new McpException($"Application '{id}' was not found.");
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

    [McpServerTool(Name = "delete_application")]
    [Description(
        "Delete a proxy application by its GUID. " +
        "If the application has an auto-managed certificate (source = Internal or External), " +
        "that certificate is also deleted automatically.")]
    public async Task<string> DeleteApplication(
        [Description("The GUID of the application to delete.")] Guid id,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("MCP delete application {AppId}", id);
        var removed = await _service.DeleteAsync(id, cancellationToken);
        if (!removed)
            throw new McpException($"Application '{id}' was not found.");
        return $"Application '{id}' deleted.";
    }
}
