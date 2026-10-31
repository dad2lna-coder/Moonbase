using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Plugins;

/// <summary>Starts active modules after Jellyfin has finished building the service provider.</summary>
public sealed class MoonfinPluginLifecycle : IHostedService
{
    private readonly MoonbaseModuleCatalog _catalog;
    private readonly MoonfinPluginRegistry _registry;
    private readonly IServiceProvider _services;
    private readonly ILogger<MoonfinPluginLifecycle> _logger;

    public MoonfinPluginLifecycle(
        MoonbaseModuleCatalog catalog,
        MoonfinPluginRegistry registry,
        IServiceProvider services,
        ILogger<MoonfinPluginLifecycle> logger)
    {
        _catalog = catalog;
        _registry = registry;
        _services = services;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _catalog.EnsureExternalLoaded(_registry.Root);
            foreach (var note in _catalog.Notes)
            {
                _logger.LogWarning("Moonbase plugin: {Note}", note);
            }

            _catalog.StartWhere(
                _services,
                id => _registry.IsActive(id),
                message => _logger.LogWarning("Moonbase plugin: {Message}", message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Moonbase plugins did not finish starting");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _catalog.StopAll();
        return Task.CompletedTask;
    }
}
