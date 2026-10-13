namespace Tas.Application.Auth;

public static class PasswordPolicy
{
    public const int MinLength = 10;

    /// <summary>Null when acceptable, otherwise the reason. At least 10 characters with a letter and a digit.</summary>
    public static string? Check(string? password, string? email = null)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength)
            return $"Password must be at least {MinLength} characters.";
        if (password.Length > 128)
            return "Password must be at most 128 characters.";
        if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit))
            return "Password must contain at least one letter and one digit.";
        if (email is not null && password.Contains(email.Split('@')[0], StringComparison.OrdinalIgnoreCase) && email.Split('@')[0].Length >= 4)
            return "Password must not contain your email name.";
        return null;
    }
}
