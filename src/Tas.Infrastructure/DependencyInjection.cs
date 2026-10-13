using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tas.Application.Tos;
using Tas.Infrastructure.Auth;
using Tas.Infrastructure.Booking;
using Tas.Infrastructure.Capacity;
using Tas.Infrastructure.Persistence;
using Tas.Infrastructure.Tos;

namespace Tas.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddTasInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        // Read when the context is created, so hosts and tests can supply it late.
        services.AddDbContext<TasDbContext>((sp, o) => o.UseNpgsql(
            sp.GetRequiredService<IConfiguration>().GetConnectionString("Tas")
            ?? throw new InvalidOperationException("Connection string 'Tas' is not configured.")));
        services.AddSingleton(TimeProvider.System);
        services.AddTasAuthentication(config);

        // Capacity
        services.AddScoped<SlotGenerationService>();
        services.AddScoped<AvailabilityQueryService>();
        services.AddScoped<BookingCounterStore>();
        services.AddScoped<CapacityAdminService>();
        services.AddHostedService<SlotGenerationWorker>();

        // Booking
        services.AddScoped<AppointmentQueryService>();
        services.AddScoped<BookingService>();
        services.AddScoped<NoShowService>();
        services.AddHostedService<NoShowWorker>();
        services.AddScoped<DemoDataSeeder>();

        // TOS: the fake until the N4 adapter exists ("Tos:Adapter": "Fake").
        var adapter = config.GetValue("Tos:Adapter", "Fake");
        if (!string.Equals(adapter, "Fake", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"TOS adapter '{adapter}' is not available yet. Use 'Fake'.");
        services.Configure<FakeTosOptions>(config.GetSection("FakeTos"));
        services.AddSingleton<ITosAdapter, FakeTosAdapter>();

        return services;
    }
}
