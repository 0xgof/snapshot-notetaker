using System.Globalization;
using System.Text;

namespace SnapshotNotetaker.Model;

public sealed record NumberingOptions
{
    public NumberFormat Format { get; init; } = NumberFormat.Decimal;
    public int Start { get; init; } = 1;
    public string Prefix { get; init; } = "";
    public string Suffix { get; init; } = "";

    public string FormatNumber(int value) => Prefix + Numbering.Format(value, Format) + Suffix;
}

public static class Numbering
{
    private static readonly (int Value, string Symbol)[] RomanTable =
    {
        (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"),
        (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I"),
    };

    public static string Format(int value, NumberFormat format) => format switch
    {
        NumberFormat.UpperAlpha when value > 0 => ToAlpha(value),
        NumberFormat.LowerAlpha when value > 0 => ToAlpha(value).ToLowerInvariant(),
        NumberFormat.UpperRoman when value is > 0 and < 4000 => ToRoman(value),
        NumberFormat.LowerRoman when value is > 0 and < 4000 => ToRoman(value).ToLowerInvariant(),
        _ => value.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>Bijective base-26: 1 → A, 26 → Z, 27 → AA.</summary>
    private static string ToAlpha(int value)
    {
        var sb = new StringBuilder();
        while (value > 0)
        {
            value--;
            sb.Insert(0, (char)('A' + value % 26));
            value /= 26;
        }
        return sb.ToString();
    }

    private static string ToRoman(int value)
    {
        var sb = new StringBuilder();
        foreach (var (n, symbol) in RomanTable)
        {
            while (value >= n)
            {
                sb.Append(symbol);
                value -= n;
            }
        }
        return sb.ToString();
    }
}
