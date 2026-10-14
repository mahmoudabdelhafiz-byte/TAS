using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tas.Application.Tos;
using Tas.Infrastructure.Auth;
using Tas.Infrastructure.Booking;
using Tas.Infrastructure.Capacity;
using Tas.Infrastructure.Gate;
using Tas.Infrastructure.MasterData;
using Tas.Infrastructure.Persistence;
using Tas.Infrastructure.Tos;
using Tas.Infrastructure.Tos.N4;

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

        // Master data
        services.AddScoped<FleetService>();
        services.AddScoped<TerminalDataService>();
        services.AddHostedService<SlotGenerationWorker>();

        // Booking
        services.AddScoped<AppointmentQueryService>();
        services.AddScoped<BookingService>();
        services.AddScoped<BulkBookingService>();
        services.AddScoped<NoShowService>();
        services.AddScoped<RevalidationService>();
        services.AddHostedService<RevalidationWorker>();
        services.AddHostedService<NoShowWorker>();
        services.AddScoped<DemoDataSeeder>();

        // Gate
        services.AddScoped<GateService>();

        // TOS: "Tos:Adapter" = Fake (development / demos) or N4; "Tos:OnUnavailable" = Warn (default) or Reject.
        services.AddSingleton(new TosSettings(config.GetValue("Tos:OnUnavailable", TosUnavailablePolicy.Warn)));
        var adapter = config.GetValue("Tos:Adapter", "Fake");
        if (string.Equals(adapter, "Fake", StringComparison.OrdinalIgnoreCase))
        {
            services.Configure<FakeTosOptions>(config.GetSection("FakeTos"));
            services.AddSingleton<ITosAdapter, FakeTosAdapter>();
        }
        else if (string.Equals(adapter, "N4", StringComparison.OrdinalIgnoreCase))
        {
            services.Configure<N4Options>(config.GetSection("N4"));
            services.AddHttpClient<IN4Client, ArgoServiceClient>(c => c.Timeout = Timeout.InfiniteTimeSpan); // per-terminal timeout in the client
            services.AddScoped<ITosAdapter, N4TosAdapter>();
        }
        else
        {
            throw new InvalidOperationException($"Unknown TOS adapter '{adapter}'. Use 'Fake' or 'N4'.");
        }

        return services;
    }
}
