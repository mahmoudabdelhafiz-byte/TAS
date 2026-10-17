using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Yardsyncs.Infrastructure.Ops;

/// <summary>
/// Settings that are fine on a developer's machine or in the automated tests but dangerous on a real server.
/// Unsafe ones stop the app from starting; risky ones are listed as warnings in /api/ops/status.
/// </summary>
public static class SecurityChecks
{
    /// <summary>A developer's machine or the automated tests: codes and links may be shown, links may use the request's address.</summary>
    public static bool IsTestHost(IHostEnvironment env) => env.IsDevelopment() || env.IsEnvironment("Testing");

    /// <summary>Settings a real server must never run with. Empty = safe to start.</summary>
    public static IReadOnlyList<string> Unsafe(IConfiguration config, IHostEnvironment env)
    {
        if (IsTestHost(env))
            return [];
        var problems = new List<string>();
        if (config.GetValue("DriverLogin:ExposeCodeForTesting", false))
            problems.Add("DriverLogin:ExposeCodeForTesting is on: driver sign-in codes would be returned to anyone who asks.");
        if (config.GetValue("PasswordLinks:ExposeTokenForTesting", false))
            problems.Add("PasswordLinks:ExposeTokenForTesting is on: password links would be returned to anyone who asks.");
        return problems;
    }

    /// <summary>Settings an administrator should look at (shown in /api/ops/status).</summary>
    public static IReadOnlyList<string> Warnings(IConfiguration config, IHostEnvironment env)
    {
        var warnings = new List<string>();
        if (IsTestHost(env))
            warnings.Add($"Running as '{env.EnvironmentName}': for development and tests only.");
        if (string.IsNullOrWhiteSpace(config["PasswordLinks:PortalUrl"]) && !IsTestHost(env))
            warnings.Add("PasswordLinks:PortalUrl is not set: invitation and password emails are not sent.");
        if (string.Equals(config.GetValue("Tos:Adapter", "Fake"), "Fake", StringComparison.OrdinalIgnoreCase))
            warnings.Add("Tos:Adapter is Fake: terminals without their own Terminals:<code>:Tos setting check containers against demo data, not a TOS.");
        foreach (var (key, what) in new[]
                 {
                     ("Notifications:SmsProvider", "SMS (driver sign-in codes, reminders)"),
                     ("Notifications:EmailProvider", "email (invitations, password links, container ready)"),
                 })
        {
            var provider = config[key];
            if (string.IsNullOrWhiteSpace(provider) || provider.Equals("Log", StringComparison.OrdinalIgnoreCase))
                warnings.Add($"{key} is Log: {what} are written to the log, not sent.");
        }
        if (config.GetValue("Notifications:LogMessageText", false) && !IsTestHost(env))
            warnings.Add("Notifications:LogMessageText is on: sign-in codes and password links are written to the log. For demos only.");
        if (!string.IsNullOrWhiteSpace(config["Assistant:ApiKey"]) && config.GetValue("Assistant:LiveData", true))
            warnings.Add("The AI assistant sends questions and the data it looks up (appointments, plates, drivers' names) to Anthropic: name it in the privacy notice, or set Assistant:LiveData=false.");
        if (string.Equals(config["Auth:AdminTwoStep"], "Optional", StringComparison.OrdinalIgnoreCase) && !IsTestHost(env))
            warnings.Add("Auth:AdminTwoStep is Optional: admins can sign in with a password alone. Leave it unset (Required) on a real server.");
        if (!string.IsNullOrEmpty(config["Bootstrap:AdminPassword"]))
            warnings.Add("Bootstrap:AdminPassword is still set: remove it once the first administrator has signed in.");
        return warnings;
    }

    /// <summary>"0551234567" → "05•••••567", "someone@example.com" → "so•••@example.com": enough to tell recipients apart in logs.</summary>
    public static string Mask(string? recipient)
    {
        if (string.IsNullOrEmpty(recipient))
            return "";
        var at = recipient.IndexOf('@');
        if (at > 0)
            return recipient[..Math.Min(2, at)] + "•••" + recipient[at..];
        return recipient.Length <= 5 ? new string('•', recipient.Length) : recipient[..2] + new string('•', recipient.Length - 5) + recipient[^3..];
    }
}
