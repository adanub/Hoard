using Hoard.Core.Connectors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hoard.Ingest.GalleryDl;

public static class GalleryDlServiceCollectionExtensions
{
    /// <summary>
    /// Register the gallery-dl-backed connectors (desktop/server only). Call alongside
    /// <c>AddHoardCore</c> from a host that can run subprocesses.
    /// </summary>
    public static IServiceCollection AddGalleryDlConnectors(this IServiceCollection services, string galleryDlPath)
    {
        services.AddSingleton<ISourceConnector>(sp =>
            new PinterestConnector(galleryDlPath, sp.GetService<ILogger<PinterestConnector>>()));
        return services;
    }
}
