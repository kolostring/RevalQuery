using Microsoft.Extensions.DependencyInjection;

namespace QueryRevalR.Core;

public static class DependencyInjection
{
    public static IServiceCollection AddQueryRevalR(this IServiceCollection services,
        Action<QueryRevalROptions>? configure = null)
    {
        var options = new QueryRevalROptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddScoped<QueryClient>();
        
        return services;
    }
}