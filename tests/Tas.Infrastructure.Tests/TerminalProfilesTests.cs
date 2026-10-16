using Microsoft.Extensions.Configuration;
using Tas.Application.Notifications;
using Tas.Domain;
using Tas.Infrastructure.Notifications;

namespace Tas.Infrastructure.Tests;

/// <summary>Settings per terminal on one installation: TOS, brand, message providers; the installation's as fallback.</summary>
public class TerminalProfilesTests
{
    private static TerminalProfiles Profiles(params (string Key, string Value)[] values) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build());

    [Fact]
    public void A_terminal_uses_its_own_settings_and_falls_back_to_the_installations()
    {
        var p = Profiles(
            ("Tos:Adapter", "n4"), ("Brand", "PortOps"),
            ("Notifications:SmsProvider", "Unifonic"), ("Notifications:Unifonic:AppSid", "app-main"), ("Notifications:Unifonic:SenderId", "PortOps"),
            ("Notifications:EmailProvider", "Smtp"), ("Notifications:Smtp:From", "tas@portops.example"),
            ("Terminals:DEMO:Tos", "Fake"), ("Terminals:DEMO:Brand", "Demo Port"),
            ("Terminals:DEMO:Notifications:Unifonic:SenderId", "DemoPort"),
            ("Terminals:DEMO:Notifications:Smtp:FromName", "Demo Port"));

        Assert.Equal("N4", p.DefaultTos); // names are matched without case
        Assert.Equal("Fake", p.TosFor("DEMO"));
        Assert.Equal("N4", p.TosFor("JED1"));
        Assert.Equal("N4", p.TosFor(null));

        Assert.Equal("Demo Port", p.BrandFor("DEMO"));
        Assert.Equal("PortOps", p.BrandFor("JED1"));
        Assert.Equal("Yardsyncs", Profiles().BrandFor("DEMO"));

        var demo = p.NotificationsFor("DEMO");
        Assert.Equal("Unifonic", demo.SmsProvider);         // from the installation
        Assert.Equal("app-main", demo.Unifonic.AppSid);      // from the installation
        Assert.Equal("DemoPort", demo.Unifonic.SenderId);    // the terminal's own
        Assert.Equal("tas@portops.example", demo.Smtp.From);
        Assert.Equal("Demo Port", demo.Smtp.FromName);
        Assert.Equal("PortOps", p.NotificationsFor("JED1").Unifonic.SenderId);
        Assert.True(p.HasOwnNotifications("DEMO"));
        Assert.False(p.HasOwnNotifications("JED1"));
    }

    [Fact]
    public void An_unknown_TOS_name_stops_the_app()
    {
        Profiles(("Tos:Adapter", "N4"), ("Terminals:DEMO:Tos", "fake")).Validate();
        var error = Assert.Throws<InvalidOperationException>(() => Profiles(("Terminals:JED1:Tos", "Navis")).Validate());
        Assert.Contains("Terminals:JED1:Tos", error.Message);
        Assert.Throws<InvalidOperationException>(() => Profiles(("Tos:Adapter", "SAP")).Validate());
    }

    private sealed class NamedSender(NotificationChannel channel, string name) : INotificationSender
    {
        public NotificationChannel Channel => channel;
        public string Provider => name;
        public Task<string?> SendAsync(string recipient, RenderedMessage message, CancellationToken ct) => Task.FromResult<string?>(name);
    }

    [Fact]
    public void Terminals_with_their_own_message_settings_get_their_own_senders()
    {
        var made = 0;
        var senders = new NotificationSenders(
            [new NamedSender(NotificationChannel.Sms, "installation")],
            (code, channel) =>
            {
                made++;
                return code == "DEMO" ? new NamedSender(channel, "demo") : null;
            });

        Assert.Equal("demo", senders.For(NotificationChannel.Sms, "DEMO")!.Provider);
        Assert.Equal("demo", senders.For(NotificationChannel.Sms, "demo")!.Provider);
        Assert.Equal("installation", senders.For(NotificationChannel.Sms, "JED1")!.Provider);
        Assert.Equal("installation", senders.For(NotificationChannel.Sms, null)!.Provider);
        Assert.Equal(2, made); // made once per terminal and channel
    }

    [Fact]
    public void Messages_are_signed_with_the_terminals_name()
    {
        var values = new Dictionary<string, string> { [NotificationText.Keys.AppointmentNo] = "X-1" };
        var message = NotificationText.Render(NotificationTemplates.StatusRed, "en", values, "Demo Port");
        Assert.StartsWith("Demo Port — ", message.Subject);
        Assert.StartsWith("Demo Port: appointment X-1", message.Body);
        Assert.DoesNotContain("Yardsyncs", message.Body);
        Assert.StartsWith("Yardsyncs — ", NotificationText.Render(NotificationTemplates.StatusRed, "en", values).Subject);
    }
}
