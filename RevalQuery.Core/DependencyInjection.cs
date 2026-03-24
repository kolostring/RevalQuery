using System;
using Microsoft.Extensions.DependencyInjection;

namespace RevalQuery.Core;

public static class DependencyInjection
{
    public static IServiceCollection AddRevalQuery(this IServiceCollection services,
        Action<RevalQueryOptions>? configure = null)
    {
        var options = new RevalQueryOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddScoped<QueryClient>();
        
        return services;
    }
}