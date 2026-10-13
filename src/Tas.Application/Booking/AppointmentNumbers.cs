using System.Security.Cryptography;

namespace Tas.Application.Booking;

public static class AppointmentNumbers
{
    // No 0/O, 1/I/L: drivers read these out over the phone.
    private const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";

    /// <summary>e.g. "JED1-261020-7KQ4M": terminal code, local slot date, 5 random characters. At most 20 characters.</summary>
    public static string New(string terminalCode, DateOnly localSlotDate)
    {
        var code = new string(terminalCode.Where(char.IsLetterOrDigit).Take(6).ToArray()).ToUpperInvariant();
        var random = new char[5];
        for (var i = 0; i < random.Length; i++)
            random[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return $"{code}-{localSlotDate:yyMMdd}-{new string(random)}";
    }

    /// <summary>Unguessable token encoded in the appointment's QR code.</summary>
    public static string NewQrToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
}
