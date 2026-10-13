using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tas.Infrastructure.Capacity;
using Tas.Infrastructure.Persistence;

namespace Tas.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddTasInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("Tas")
                               ?? throw new InvalidOperationException("Connection string 'Tas' is not configured.");

        services.AddDbContext<TasDbContext>(o => o.UseNpgsql(connectionString));
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<SlotGenerationService>();
        services.AddScoped<AvailabilityQueryService>();
        services.AddScoped<BookingCounterStore>();
        services.AddHostedService<SlotGenerationWorker>();
        return services;
    }
}
