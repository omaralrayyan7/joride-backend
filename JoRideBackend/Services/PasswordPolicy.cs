using System.Text.RegularExpressions;

namespace JoRideBackend.Services
{
    /// <summary>
    /// Centralized credential validation. This app does not use ASP.NET Core Identity's
    /// UserManager/IdentityOptions (it stores users in-memory + Firestore and hashes with
    /// IPasswordHasher), so the "Identity password policy" is enforced here instead.
    /// </summary>
    public static class PasswordPolicy
    {
        // Strict, practical email format.
        private static readonly Regex EmailRegex = new(
            @"^[^@\s]+@[^@\s]+\.[^@\s]{2,}$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static bool IsValidEmail(string? email)
            => !string.IsNullOrWhiteSpace(email) && EmailRegex.IsMatch(email.Trim());

        /// <summary>
        /// Returns null when the password satisfies the policy, otherwise an error message.
        /// Policy: min 8 chars, at least one uppercase, one lowercase, one special character.
        /// </summary>
        public static string? Validate(string? password)
        {
            if (string.IsNullOrEmpty(password) || password.Length < 8)
                return "Password must be at least 8 characters long.";
            if (!password.Any(char.IsUpper))
                return "Password must contain at least one uppercase letter.";
            if (!password.Any(char.IsLower))
                return "Password must contain at least one lowercase letter.";
            if (!password.Any(ch => !char.IsLetterOrDigit(ch)))
                return "Password must contain at least one special character (e.g. !, @, #, $).";
            return null;
        }
    }
}
