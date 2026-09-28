using System.Globalization;

namespace Custodian.Shared.Reporting.Models;

/// <summary>
/// CSTD-36-1: the cell/value types a report may carry, and their one text form. PDF and CSV both
/// format through here, so a number reads the same in both and never depends on the server culture.
///
/// Supported: null, string, bool, integer and floating-point numbers, decimal, DateTime,
/// DateTimeOffset, DateOnly, Guid and enums. Round numbers in the builder (e.g. hours to 1 decimal);
/// this class does not round. Anything else (TimeSpan, objects) is rejected when the section is built.
/// </summary>
public static class ReportValue
{
    /// <summary>ISO-8601 UTC, to the second (e.g. 2026-09-28T14:05:00Z).</summary>
    public const string DateTimeFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    public const string DateFormat = "yyyy-MM-dd";

    public static bool IsSupported(object? value) => value switch
    {
        null => true,
        string or bool or Guid => true,
        byte or sbyte or short or ushort or int or uint or long or ulong => true,
        float or double or decimal => true,
        DateTime or DateTimeOffset or DateOnly => true,
        Enum => true,
        _ => false
    };

    public static bool IsNumeric(object? value) => value is
        byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;

    internal static void EnsureSupported(object? value, string location)
    {
        if (!IsSupported(value))
        {
            throw new ArgumentException(
                $"Unsupported report value type '{value!.GetType().Name}' at {location}. Convert it to a number, string or date first.");
        }
    }

    /// <summary>
    /// Culture-invariant text for a value: numbers with '.' decimals and no grouping, dates as
    /// ISO-8601 UTC, booleans as Yes/No, null as an empty string.
    /// </summary>
    public static string Format(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        bool b => b ? "Yes" : "No",
        Guid g => g.ToString("D"),
        DateTime dt => ToUtc(dt).ToString(DateTimeFormat, CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.UtcDateTime.ToString(DateTimeFormat, CultureInfo.InvariantCulture),
        DateOnly d => d.ToString(DateFormat, CultureInfo.InvariantCulture),
        Enum e => e.ToString(),
        IFormattable f when IsNumeric(value) => f.ToString(null, CultureInfo.InvariantCulture),
        _ => throw new ArgumentException($"Unsupported report value type '{value.GetType().Name}'.")
    };

    // MySQL (Pomelo) returns DateTime with Kind=Unspecified; every stored time in Custodian is UTC.
    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
