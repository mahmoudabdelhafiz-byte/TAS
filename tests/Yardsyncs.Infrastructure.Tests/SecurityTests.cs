using System.IO.Compression;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yardsyncs.Application.Notifications;
using Yardsyncs.Domain;
using Yardsyncs.Infrastructure.Booking;
using Yardsyncs.Infrastructure.Notifications;
using Yardsyncs.Infrastructure.Ops;

namespace Yardsyncs.Infrastructure.Tests;

/// <summary>Security review: settings checks, what reaches the log, oversized uploads (no database needed).</summary>
public class SecurityTests
{
    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Yardsyncs.Api";
        public string ContentRootPath { get; set; } = "/app";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    [Fact]
    public void Test_switches_are_refused_on_a_real_server_only()
    {
        var config = Config(("DriverLogin:ExposeCodeForTesting", "true"), ("PasswordLinks:ExposeTokenForTesting", "true"));
        Assert.Equal(2, SecurityChecks.Unsafe(config, new Env("Production")).Count);
        Assert.Equal(2, SecurityChecks.Unsafe(config, new Env("Staging")).Count);
        Assert.Empty(SecurityChecks.Unsafe(config, new Env("Development")));
        Assert.Empty(SecurityChecks.Unsafe(config, new Env("Testing")));
        Assert.Empty(SecurityChecks.Unsafe(Config(), new Env("Production")));
    }

    [Fact]
    public void Warnings_name_the_settings_to_look_at()
    {
        var demo = SecurityChecks.Warnings(Config(("Bootstrap:AdminPassword", "x")), new Env("Production"));
        Assert.Contains(demo, w => w.StartsWith("PasswordLinks:PortalUrl"));
        Assert.Contains(demo, w => w.StartsWith("Tos:Adapter is Fake"));
        Assert.Contains(demo, w => w.StartsWith("Notifications:SmsProvider is Log"));
        Assert.Contains(demo, w => w.StartsWith("Notifications:EmailProvider is Log"));
        Assert.Contains(demo, w => w.StartsWith("Bootstrap:AdminPassword"));

        var configured = SecurityChecks.Warnings(Config(
            ("PasswordLinks:PortalUrl", "https://tas.example.com"), ("Tos:Adapter", "N4"),
            ("Notifications:SmsProvider", "Unifonic"), ("Notifications:EmailProvider", "Smtp")), new Env("Production"));
        Assert.Empty(configured);
    }

    [Fact]
    public void Recipients_are_masked()
    {
        Assert.Equal("+9••••••••001", SecurityChecks.Mask("+966500000001"));
        Assert.Equal("ad•••@example.com", SecurityChecks.Mask("admin@example.com"));
        Assert.Equal("•••", SecurityChecks.Mask("123"));
        Assert.Equal("", SecurityChecks.Mask(null));
    }

    private sealed class CapturingLogger : ILogger<LogSender>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }

    [Fact]
    public async Task Codes_and_links_stay_out_of_the_log_on_a_real_server()
    {
        var message = new RenderedMessage("Yardsyncs", "Yardsyncs: sign-in code 123456.");

        var server = new CapturingLogger();
        await new LogSender(NotificationChannel.Sms, server, showBody: false).SendAsync("+966500000001", message, default);
        var line = Assert.Single(server.Lines);
        Assert.DoesNotContain("123456", line);
        Assert.DoesNotContain("+966500000001", line);

        var developer = new CapturingLogger();
        await new LogSender(NotificationChannel.Sms, developer, showBody: true).SendAsync("+966500000001", message, default);
        Assert.Contains("123456", Assert.Single(developer.Lines));
    }

    [Fact]
    public void A_workbook_that_unpacks_to_too_much_is_refused()
    {
        // 60 MB of zeros packs into well under the 2 MB upload limit.
        var bomb = new MemoryStream();
        using (var zip = new ZipArchive(bomb, ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.SmallestSize).Open())
        {
            var zeros = new byte[1024 * 1024];
            for (var i = 0; i < 60; i++)
                entry.Write(zeros);
        }
        Assert.True(bomb.Length < BulkFiles.MaxUploadBytes);

        bomb.Position = 0;
        var error = Assert.Throws<InvalidDataException>(() => BulkFiles.ReadExcel(bomb));
        Assert.StartsWith("The workbook is too large", error.Message);
    }

    [Fact]
    public void A_normal_workbook_is_read()
    {
        var template = BulkFiles.ExcelTemplate(new DateOnly(2026, 10, 20));
        var table = BulkFiles.ReadExcel(new MemoryStream(template));
        Assert.NotEmpty(table);
    }

    [Fact]
    public void Forwarded_headers_are_believed_only_from_the_proxys_network()
    {
        var defaults = ProxyTrust.Networks(null);
        Assert.Contains(defaults, n => n.Contains(System.Net.IPAddress.Parse("172.18.0.5")));   // a Docker network
        Assert.Contains(defaults, n => n.Contains(System.Net.IPAddress.Loopback));
        Assert.DoesNotContain(defaults, n => n.Contains(System.Net.IPAddress.Parse("203.0.113.9"))); // the internet

        var configured = ProxyTrust.Networks("10.20.0.0/16, 192.0.2.7");
        Assert.Equal(2, configured.Count);
        Assert.True(configured[1].Contains(System.Net.IPAddress.Parse("192.0.2.7")));
        Assert.DoesNotContain(configured, n => n.Contains(System.Net.IPAddress.Parse("172.18.0.5")));
        Assert.Throws<InvalidOperationException>(() => ProxyTrust.Networks("proxy.example.com"));
    }
}
