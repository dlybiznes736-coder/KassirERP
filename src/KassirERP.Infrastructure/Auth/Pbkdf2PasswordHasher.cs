using System.Security.Cryptography;
using KassirERP.Application.Auth;

namespace KassirERP.Infrastructure.Auth;

/// <summary>
/// PBKDF2-HMAC-SHA512, 210 000 итераций (рекомендация OWASP), соль 16 байт на
/// каждый хеш. Формат строки: pbkdf2-sha512$&lt;итерации&gt;$&lt;соль b64&gt;$&lt;хеш b64&gt; —
/// число итераций хранится в хеше, его можно поднять без миграции.
/// Один и тот же хешер для паролей и PIN. PIN из 4–8 цифр перебирается и по
/// хешу, поэтому его главная защита — ограничение попыток (auth.begin_attempt).
/// </summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private const string Prefix = "pbkdf2-sha512";
    private const int Iterations = 210_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public string Hash(string secret)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(secret, salt, Iterations, HashAlgorithmName.SHA512, HashSize);
        return $"{Prefix}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string hash, string secret)
    {
        var parts = hash.Split('$');
        if (parts.Length != 4 || parts[0] != Prefix || !int.TryParse(parts[1], out var iterations) || iterations < 1)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(secret, salt, iterations, HashAlgorithmName.SHA512, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
