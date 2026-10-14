using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tas.Domain;
using Tas.Application.Tos;
using Tas.Infrastructure.Auth;
using Tas.Infrastructure.Booking;
using Tas.Infrastructure.Capacity;
using Tas.Infrastructure.Gate;
using Tas.Infrastructure.MasterData;
using Tas.Infrastructure.Notifications;
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

        // Notifications: outbox sender, reminders, providers per channel.
        services.AddTasNotifications(config);

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

    /// <summary>
    /// "Notifications:Sms:Provider" = None (default) / Log / Http; "Notifications:Email:Provider" = None (default) / Log / Smtp.
    /// With None, messages to that channel are marked FAILED ("no provider configured") so they show up in the outbox.
    /// </summary>
    private static void AddTasNotifications(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<NotificationOptions>().Bind(config.GetSection("Notifications"));
        services.AddScoped<NotificationSender>();
        services.AddScoped<SlotReminderService>();
        services.AddScoped<NotificationQueryService>();
        services.AddHostedService<NotificationWorker>();

        var sms = config.GetValue("Notifications:Sms:Provider", "None")!;
        if (Is(sms, "Log"))
            services.AddSingleton<INotificationChannel>(sp => new LogNotificationChannel(NotificationChannel.Sms, sp.GetRequiredService<ILogger<LogNotificationChannel>>()));
        else if (Is(sms, "Http"))
        {
            services.AddHttpClient<HttpSmsChannel>(c => c.Timeout = Timeout.InfiniteTimeSpan); // timeout from the options
            services.AddTransient<INotificationChannel>(sp => sp.GetRequiredService<HttpSmsChannel>());
        }
        else if (!Is(sms, "None"))
            throw new InvalidOperationException($"Unknown SMS provider '{sms}'. Use 'None', 'Log' or 'Http'.");

        var email = config.GetValue("Notifications:Email:Provider", "None")!;
        if (Is(email, "Log"))
            services.AddSingleton<INotificationChannel>(sp => new LogNotificationChannel(NotificationChannel.Email, sp.GetRequiredService<ILogger<LogNotificationChannel>>()));
        else if (Is(email, "Smtp"))
            services.AddSingleton<INotificationChannel, SmtpEmailChannel>();
        else if (!Is(email, "None"))
            throw new InvalidOperationException($"Unknown email provider '{email}'. Use 'None', 'Log' or 'Smtp'.");
    }

    private static bool Is(string value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
