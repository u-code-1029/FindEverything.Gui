using System.Globalization;
using System.Numerics;

namespace FindEverything.Application.Catalog;

/// <summary>Slider coordinates are approximate; exact decimal bounds never round through double.
/// Integer arithmetic also handles decimal.MinValue..MaxValue without overflowing their difference.</summary>
public static class CatalogNumericRangeMath
{
    private static readonly BigInteger Scale = BigInteger.Pow(10, 28);
    public static double Position(CatalogNumericRange range, decimal value)
    {
        var minimum = Integer(range.Minimum); var maximum = Integer(range.Maximum);
        if (maximum <= minimum) return 0;
        return Math.Clamp((double)(Integer(value) - minimum) / (double)(maximum - minimum), 0, 1);
    }

    public static decimal Interpolate(CatalogNumericRange range, int position, int steps = 10000)
    {
        if (steps <= 0) throw new ArgumentOutOfRangeException(nameof(steps));
        position = Math.Clamp(position, 0, steps);
        if (position == 0) return range.Minimum;
        if (position == steps) return range.Maximum;
        var value = (Integer(range.Minimum) * (steps - position) + Integer(range.Maximum) * position) / steps;
        var magnitude = BigInteger.Abs(value);
        var whole = BigInteger.DivRem(magnitude, Scale, out var fraction);
        var text = (value.Sign < 0 ? "-" : "") + whole.ToString(CultureInfo.InvariantCulture) + "."
            + fraction.ToString(CultureInfo.InvariantCulture).PadLeft(28, '0');
        return Math.Clamp(decimal.Parse(text, CultureInfo.InvariantCulture), range.Minimum, range.Maximum);
    }

    private static BigInteger Integer(decimal value)
    {
        var bits = decimal.GetBits(value);
        var magnitude = (new BigInteger((uint)bits[2]) << 64) | (new BigInteger((uint)bits[1]) << 32) | (uint)bits[0];
        magnitude *= BigInteger.Pow(10, 28 - ((bits[3] >> 16) & 0xff));
        return bits[3] < 0 ? -magnitude : magnitude;
    }
}
