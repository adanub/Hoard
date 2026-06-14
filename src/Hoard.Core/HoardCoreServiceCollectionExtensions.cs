using Hoard.Core.Ingest;
using Hoard.Core.Library;
using Hoard.Core.Metadata;
using Hoard.Core.Projects;
using Hoard.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hoard.Core;

public static class HoardCoreServiceCollectionExtensions
{
    /// <summary>
    /// Register the platform-neutral Hoard core (storage, DB, ingest + library services). Connectors
    /// are registered separately by the host (e.g. <c>AddGalleryDlConnectors</c> on desktop), keeping
    /// Core free of any subprocess/desktop dependency. Storage is project-scoped: the DB factory and
    /// media store read the open project from <see cref="ProjectManager"/>.
    /// </summary>
    public static IServiceCollection AddHoardCore(this IServiceCollection services, AppPaths appPaths)
    {
        services.AddSingleton(appPaths);
        services.AddSingleton<ProjectManager>();

        services.AddSingleton<ProjectDbContextFactory>();
        services.AddSingleton<IDbContextFactory<HoardDbContext>>(sp => sp.GetRequiredService<ProjectDbContextFactory>());
        services.AddSingleton<IMediaStore, ProjectMediaStore>();

        services.AddSingleton<IngestService>();
        services.AddSingleton<LibraryService>();
        return services;
    }
}
