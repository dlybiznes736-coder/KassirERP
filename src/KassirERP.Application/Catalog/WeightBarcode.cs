namespace KassirERP.Application.Catalog;

/// <summary>
/// Весовой штрихкод EAN-13: PP CCCCC WWWWW K (DESIGN_GROCERY.md, п. 1).
/// PP — префикс 20–29 (диапазон GS1 для внутренних кодов магазина),
/// CCCCC — код товара на весах (PLU), WWWWW — вес в граммах, K — контрольная цифра.
/// Пример: 22 01234 01250 K → PLU 01234, 1.250 кг.
/// </summary>
public sealed record WeightBarcode(string Prefix, string Plu, int Grams)
{
    public decimal Kilograms => Grams / 1000m;

    /// <summary>
    /// Разбирает код. Не 13 цифр, префикс не 20–29, неверная контрольная цифра
    /// или нулевой вес — не весовой (null): опечатка сканера не должна стать весом.
    /// </summary>
    public static WeightBarcode? TryParse(string code)
    {
        if (code.Length != 13 || !code.All(char.IsAsciiDigit) || code[0] != '2' || !HasValidCheckDigit(code))
        {
            return null;
        }

        var grams = int.Parse(code.AsSpan(7, 5), System.Globalization.CultureInfo.InvariantCulture);
        return grams == 0 ? null : new WeightBarcode(code[..2], code.Substring(2, 5), grams);
    }

    /// <summary>Контрольная цифра EAN-13: веса 1 и 3 по очереди слева, дополнение до 10.</summary>
    public static bool HasValidCheckDigit(string code) => code[12] - '0' == CheckDigit(code.AsSpan(0, 12));

    public static int CheckDigit(ReadOnlySpan<char> first12)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++)
        {
            sum += (first12[i] - '0') * (i % 2 == 0 ? 1 : 3);
        }

        return (10 - sum % 10) % 10;
    }
}
