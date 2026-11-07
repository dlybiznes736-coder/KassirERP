using System.Security.Cryptography;
using System.Text;

namespace KassirERP.Application.Devices;

/// <summary>
/// Секрет кассы и код подключения. Оба случайные; в базе — только SHA-256.
/// Секрет — 32 байта: перебрать нельзя, поэтому соль и PBKDF2 не нужны.
/// Код — 8 цифр, но живёт 10 минут, одноразовый и защищён лимитом по IP.
/// </summary>
public static class DeviceSecrets
{
    public static string NewSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string NewEnrollmentCode() =>
        RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8", System.Globalization.CultureInfo.InvariantCulture);

    public static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static bool Matches(string storedHash, string secret)
    {
        var actual = Encoding.ASCII.GetBytes(Hash(secret));
        var expected = Encoding.ASCII.GetBytes(storedHash);
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
