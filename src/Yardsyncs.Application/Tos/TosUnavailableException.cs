namespace Yardsyncs.Application.Tos;

/// <summary>The TOS could not be reached or answered with an error (timeout, HTTP error, SOAP fault, bad payload).</summary>
public sealed class TosUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>What a booking does when the TOS can't be reached.</summary>
public enum TosUnavailablePolicy
{
    /// <summary>Book with an amber warning; the scheduled re-check runs the real checks as soon as the TOS answers.</summary>
    Warn,

    /// <summary>Refuse the booking until the TOS answers.</summary>
    Reject,
}

/// <summary>TOS settings shared by the booking flow ("Tos:OnUnavailable": "Warn" | "Reject").</summary>
public sealed record TosSettings(TosUnavailablePolicy OnUnavailable = TosUnavailablePolicy.Warn);
