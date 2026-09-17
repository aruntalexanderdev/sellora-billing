using System.Text.RegularExpressions;

namespace Sellora.Api.Validation;

public static partial class PasswordRules
{
    public const string RequirementText = "Password must be at least 8 characters and include a letter and a number.";

    public static bool IsValid(string password) =>
        password.Length >= 8 && LetterRegex().IsMatch(password) && DigitRegex().IsMatch(password);

    [GeneratedRegex("[A-Za-z]")]
    private static partial Regex LetterRegex();

    [GeneratedRegex("[0-9]")]
    private static partial Regex DigitRegex();
}
