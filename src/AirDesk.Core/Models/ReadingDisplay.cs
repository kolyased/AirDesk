using System.Globalization;

namespace AirDesk;

internal static class ReadingDisplay
{
    public static string Value(float? value, string unit, CultureInfo? culture = null)
    {
        if (value is not float number || !float.IsFinite(number)) return "—";
        string formatted = number.ToString("0.#", culture ?? CultureInfo.CurrentCulture);
        return unit.Length > 0 ? $"{formatted} {unit}" : formatted;
    }
}
