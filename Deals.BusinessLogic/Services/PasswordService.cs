using System.Security.Cryptography;
using Deals.BusinessLogic.Interfaces;

namespace Deals.BusinessLogic.Services;

public class PasswordService : IPasswordService
{
    private const string Scheme = "pbkdf2";
    private const string Version = "v1";
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 120_000;
    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;

    public string HashPassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("Password is required", nameof(password));
        }

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, Algorithm, HashSize);

        return string.Join('$', Scheme, Version, Iterations, Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    public bool VerifyPassword(string password, string hashedPassword)
    {
        try
        {
            if (string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(hashedPassword))
            {
                return false;
            }

            var parts = hashedPassword.Split('$');
            if (parts.Length != 5)
            {
                return false;
            }

            if (!string.Equals(parts[0], Scheme, StringComparison.Ordinal) ||
                !string.Equals(parts[1], Version, StringComparison.Ordinal) ||
                !int.TryParse(parts[2], out var iterations) ||
                iterations <= 0)
            {
                return false;
            }

            var salt = Convert.FromBase64String(parts[3]);
            var expectedHash = Convert.FromBase64String(parts[4]);
            var computedHash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, Algorithm, expectedHash.Length);

            return CryptographicOperations.FixedTimeEquals(computedHash, expectedHash);
            }
        catch
        {
            return false;
        }
    }
}
