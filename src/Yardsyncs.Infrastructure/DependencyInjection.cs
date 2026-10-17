using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yardsyncs.Domain;
using Yardsyncs.Application.Tos;
using Yardsyncs.Infrastructure.Auth;
using Yardsyncs.Infrastructure.Booking;
using Yardsyncs.Infrastructure.Capacity;
using Yardsyncs.Infrastructure.Gate;
using Yardsyncs.Infrastructure.MasterData;
using Yardsyncs.Infrastructure.Notifications;
using Yardsyncs.Infrastructure.Persistence;
using Yardsyncs.Infrastructure.Tos;
using Yardsyncs.Infrastructure.Tos.N4;

namespace Yardsyncs.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddYardsyncsInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        // Read when the context is created, so hosts and tests can supply it late.
        services.AddDbContext<YardsyncsDbContext>((sp, o) => o.UseNpgsql(
            DatabaseMigrator.ConnectionStringFrom(sp.GetRequiredService<IConfiguration>())));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<DatabaseMigrator>();
        services.AddScoped<Yardsyncs.Infrastructure.Ops.OpsStatusService>();
        services.Configure<Yardsyncs.Infrastructure.Ops.PrivacyOptions>(config.GetSection("Privacy"));
        services.AddScoped<Yardsyncs.Infrastructure.Ops.RetentionService>();
        services.AddScoped<Yardsyncs.Infrastructure.Ops.PersonalDataService>();
        services.AddHostedService<Yardsyncs.Infrastructure.Ops.RetentionWorker>();
        services.AddYardsyncsAuthentication(config);

        // Capacity
        services.AddScoped<SlotGenerationService>();
        services.AddScoped<AvailabilityQueryService>();
        services.AddScoped<BookingCounterStore>();
        services.AddScoped<CapacityAdminService>();

        // Master data
        services.AddScoped<FleetService>();
        services.AddScoped<TerminalDataService>();
        services.AddScoped<OperatorService>();
        services.AddScoped<ChargeService>();
        services.AddScoped<Yardsyncs.Infrastructure.Brands.BrandingService>();
        services.AddScoped<Yardsyncs.Infrastructure.Onboarding.OnboardingService>();
        services.AddHostedService<SlotGenerationWorker>();

        // Booking
        services.AddScoped<AppointmentQueryService>();
        services.AddScoped<BookingService>();
        services.AddScoped<BulkBookingService>();
        services.AddScoped<ContainerLookupService>();
        services.Configure<TrackingOptions>(config.GetSection("Tracking"));
        services.AddScoped<ContainerTrackingService>();
        services.AddHostedService<TrackingWorker>();
        services.Configure<Yardsyncs.Infrastructure.DriverApp.DriverLoginOptions>(config.GetSection("DriverLogin"));
        services.AddScoped<Yardsyncs.Infrastructure.DriverApp.DriverLoginService>();
        services.AddScoped<Yardsyncs.Infrastructure.DriverApp.DriverAppService>();
        services.AddScoped<Yardsyncs.Infrastructure.DriverApp.GatePassService>();
        services.AddScoped<Yardsyncs.Infrastructure.DriverApp.TripTrackingService>();
        services.AddScoped<Yardsyncs.Infrastructure.DriverApp.YardGuidanceService>();
        services.AddScoped<Yardsyncs.Infrastructure.ControlRoom.ControlRoomService>();
        services.AddScoped<Yardsyncs.Infrastructure.Reports.KpiService>();
        services.AddScoped<Yardsyncs.Infrastructure.Reports.CompanyReportService>();
        services.AddScoped<Yardsyncs.Infrastructure.Reports.ReportExportService>();
        services.AddScoped<NoShowService>();
        services.AddScoped<RevalidationService>();
        services.AddHostedService<RevalidationWorker>();
        services.AddHostedService<NoShowWorker>();
        services.AddScoped<DemoDataSeeder>();
        services.AddScoped<LoadTestSeeder>();

        // Gate
        services.AddScoped<GateService>();
        services.AddScoped<Yardsyncs.Infrastructure.Messages.MessageService>();
        services.AddScoped<LaneService>();
        services.AddScoped<GateSystemService>();
        services.AddScoped<Yardsyncs.Infrastructure.Auth.CompanyApiKeyService>();
        services.AddSingleton<Modules>(); // which modules this installation and each terminal have

        // Notifications: outbox delivery; provider per channel from "Notifications" (default: Log = not sent).
        services.Configure<NotificationOptions>(config.GetSection("Notifications"));
        services.AddHttpClient("notifications", c => c.Timeout = TimeSpan.FromSeconds(20));
        foreach (var channel in new[] { NotificationChannel.Sms, NotificationChannel.Whatsapp, NotificationChannel.Email, NotificationChannel.Push })
            services.AddSingleton<INotificationSender>(sp => CreateSender(sp, channel, sp.GetRequiredService<IOptions<NotificationOptions>>().Value));
        services.AddSingleton(sp => new NotificationSenders(
            sp.GetServices<INotificationSender>(),
            (code, channel) =>
            {
                var profiles = sp.GetRequiredService<TerminalProfiles>();
                return profiles.HasOwnNotifications(code) ? CreateSender(sp, channel, profiles.NotificationsFor(code)) : null;
            }));
        services.AddScoped<NotificationDispatcher>();
        services.AddHostedService<NotificationWorker>();

        // Events for the terminal's own systems (truck at block…): outbox delivered to each terminal's webhook.
        services.Configure<Yardsyncs.Infrastructure.Integration.IntegrationOptions>(config.GetSection("Integration"));
        services.AddHttpClient("integration");
        services.AddScoped<Yardsyncs.Infrastructure.Integration.IntegrationDispatcher>();
        services.AddHostedService<Yardsyncs.Infrastructure.Integration.IntegrationWorker>();

        // AI assistant: off until Assistant:ApiKey is set; read-only look-ups with the user's own permissions.
        services.Configure<Yardsyncs.Infrastructure.Assistant.AssistantOptions>(config.GetSection("Assistant"));
        services.AddHttpClient<Yardsyncs.Infrastructure.Assistant.IAssistantModel, Yardsyncs.Infrastructure.Assistant.AnthropicModel>(c => c.Timeout = Timeout.InfiniteTimeSpan);
        services.AddScoped<Yardsyncs.Infrastructure.Assistant.AssistantTools>();
        services.AddScoped<Yardsyncs.Infrastructure.Assistant.AssistantService>();

        // TOS per terminal (Terminals:<code>:Tos, default Tos:Adapter): Fake (development / demos) or N4.
        // "Tos:OnUnavailable" = Warn (default) or Reject.
        services.AddSingleton(new TosSettings(config.GetValue("Tos:OnUnavailable", TosUnavailablePolicy.Warn)));
        services.AddSingleton<TerminalProfiles>();
        var terminalProfiles = new TerminalProfiles(config);
        terminalProfiles.Validate();
        // Mobile numbers and plates are read with the installation's country everywhere (companies and drivers are shared).
        Yardsyncs.Application.Countries.CountryPacks.UseForInstallation(terminalProfiles.InstallationCountry);
        services.Configure<FakeTosOptions>(config.GetSection("FakeTos"));
        services.AddSingleton<FakeTosAdapter>();
        services.Configure<N4Options>(config.GetSection("N4"));
        services.AddHttpClient<IN4Client, ArgoServiceClient>(c => c.Timeout = Timeout.InfiniteTimeSpan); // per-terminal timeout in the client
        services.AddScoped<N4TosAdapter>();
        services.AddScoped<TosRouter>();
        services.AddScoped<ITosAdapter>(sp => sp.GetRequiredService<TosRouter>());

        return services;
    }

    private static INotificationSender CreateSender(IServiceProvider sp, NotificationChannel channel, NotificationOptions o)
    {
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
            (_, "log" or "") => new LogSender(channel, sp.GetRequiredService<ILogger<LogSender>>(),
                showBody: Ops.SecurityChecks.IsTestHost(sp.GetRequiredService<Microsoft.Extensions.Hosting.IHostEnvironment>())
                          || sp.GetRequiredService<IConfiguration>().GetValue("Notifications:LogMessageText", false)),
            (NotificationChannel.Sms, "unifonic") => new UnifonicSmsSender(Http(), o.Unifonic),
            (NotificationChannel.Sms or NotificationChannel.Whatsapp, "twilio") => new TwilioSender(Http(), o.Twilio, channel),
            (NotificationChannel.Email, "smtp") => new SmtpEmailSender(o.Smtp),
            (NotificationChannel.Push, "fcm") => new FcmPushSender(Http(), o.Fcm, sp.GetRequiredService<TimeProvider>()),
            _ => throw new InvalidOperationException($"Notification provider '{provider}' is not available for {channel}."),
        };
    }
}
