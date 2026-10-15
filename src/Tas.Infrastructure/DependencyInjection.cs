using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
        services.AddScoped<ContainerLookupService>();
        services.Configure<Tas.Infrastructure.DriverApp.DriverLoginOptions>(config.GetSection("DriverLogin"));
        services.AddScoped<Tas.Infrastructure.DriverApp.DriverLoginService>();
        services.AddScoped<Tas.Infrastructure.DriverApp.DriverAppService>();
        services.AddScoped<Tas.Infrastructure.DriverApp.GatePassService>();
        services.AddScoped<Tas.Infrastructure.DriverApp.TripTrackingService>();
        services.AddScoped<Tas.Infrastructure.DriverApp.YardGuidanceService>();
        services.AddScoped<Tas.Infrastructure.Reports.KpiService>();
        services.AddScoped<NoShowService>();
        services.AddScoped<RevalidationService>();
        services.AddHostedService<RevalidationWorker>();
        services.AddHostedService<NoShowWorker>();
        services.AddScoped<DemoDataSeeder>();

        // Gate
        services.AddScoped<GateService>();

        // Notifications: outbox delivery; provider per channel from "Notifications" (default: Log = not sent).
        services.Configure<NotificationOptions>(config.GetSection("Notifications"));
        services.AddHttpClient("notifications", c => c.Timeout = TimeSpan.FromSeconds(20));
        foreach (var channel in new[] { NotificationChannel.Sms, NotificationChannel.Whatsapp, NotificationChannel.Email, NotificationChannel.Push })
            services.AddSingleton<INotificationSender>(sp => CreateSender(sp, channel));
        services.AddSingleton<NotificationSenders>();
        services.AddScoped<NotificationDispatcher>();
        services.AddHostedService<NotificationWorker>();

        // Events for the terminal's own systems (truck at block…): outbox delivered to each terminal's webhook.
        services.Configure<Tas.Infrastructure.Integration.IntegrationOptions>(config.GetSection("Integration"));
        services.AddHttpClient("integration");
        services.AddScoped<Tas.Infrastructure.Integration.IntegrationDispatcher>();
        services.AddHostedService<Tas.Infrastructure.Integration.IntegrationWorker>();

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

    private static INotificationSender CreateSender(IServiceProvider sp, NotificationChannel channel)
    {
        var o = sp.GetRequiredService<IOptions<NotificationOptions>>().Value;
        HttpClient Http() => sp.GetRequiredService<IHttpClientFactory>().CreateClient("notifications");

        var provider = (channel switch
        {
            NotificationChannel.Sms => o.SmsProvider,
            NotificationChannel.Whatsapp => o.WhatsappProvider,
            NotificationChannel.Push => o.PushProvider,
            _ => o.EmailProvider,
        } ?? "Log").Trim().ToLowerInvariant();

        return (channel, provider) switch
        {
            (_, "log" or "") => new LogSender(channel, sp.GetRequiredService<ILogger<LogSender>>()),
            (NotificationChannel.Sms, "unifonic") => new UnifonicSmsSender(Http(), o.Unifonic),
            (NotificationChannel.Sms or NotificationChannel.Whatsapp, "twilio") => new TwilioSender(Http(), o.Twilio, channel),
            (NotificationChannel.Email, "smtp") => new SmtpEmailSender(o.Smtp),
            (NotificationChannel.Push, "fcm") => new FcmPushSender(Http(), o.Fcm, sp.GetRequiredService<TimeProvider>()),
            _ => throw new InvalidOperationException($"Notification provider '{provider}' is not available for {channel}."),
        };
    }
}
