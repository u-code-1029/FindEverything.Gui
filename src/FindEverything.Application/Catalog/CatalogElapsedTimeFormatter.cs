using System.Globalization;

namespace FindEverything.Application.Catalog;

public static class CatalogElapsedTimeFormatter
{
    public static string Format(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (elapsed.TotalHours >= 1)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{(long)elapsed.TotalHours:0}시간 {elapsed.Minutes:00}분 {elapsed.Seconds:00}.{elapsed.Milliseconds / 10:00}초");
        }

        if (elapsed.TotalMinutes >= 1)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{(long)elapsed.TotalMinutes:0}분 {elapsed.Seconds:00}.{elapsed.Milliseconds / 10:00}초");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{elapsed.TotalSeconds:0.00}초");
    }
}
