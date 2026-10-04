using System.Globalization;
using System.Text.Json;

namespace GraphMcp.Infrastructure;

internal static class GraphInput
{
    internal static string Id(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value is "." or ".." || value.Any(char.IsControl)) throw GraphOperationException.Invalid("An item ID must contain 1 to 2048 valid characters.");
        return Uri.EscapeDataString(value);
    }
    internal static int PageSize(int value, int maximum)
    {
        if (value < 1 || value > maximum) throw GraphOperationException.Invalid($"Page size must be between 1 and {maximum}.");
        return value;
    }
    internal static void Range(DateTimeOffset start, DateTimeOffset end, int maxDays)
    {
        if (start == default || end == default || end <= start || end - start > TimeSpan.FromDays(maxDays)) throw GraphOperationException.Invalid($"The end must follow the start and the range must not exceed {maxDays} days.");
    }
    internal static TimeZoneInfo Zone(string zone)
    {
        if (string.IsNullOrWhiteSpace(zone) || zone.Length > 128) throw GraphOperationException.Invalid("Use UTC or an IANA time zone identifier.");
        try
        {
            var result = TimeZoneInfo.FindSystemTimeZoneById(zone);
            if (zone != "UTC" && !result.HasIanaId) throw GraphOperationException.Invalid("Use UTC or an IANA time zone identifier.");
            return result;
        }
        catch (TimeZoneNotFoundException) { throw GraphOperationException.Invalid("The time zone is not supported."); }
        catch (InvalidTimeZoneException) { throw GraphOperationException.Invalid("The time zone is not supported."); }
    }
    internal static string Date(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    internal static string Query(string path, params (string Name, string? Value)[] values) => path + "?" + string.Join("&", values.Where(x => x.Value is not null).Select(x => Uri.EscapeDataString(x.Name) + "=" + Uri.EscapeDataString(x.Value!)));
    internal static string? Text(this JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    internal static string? BoundedText(this JsonElement value, string name, int limit) => value.Text(name) is { } text ? TextNormalizer.Normalize(text, false, limit).Text : null;
    internal static bool Bool(this JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.True;
    internal static JsonElement Object(this JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Object ? item : EmptyObject;
    internal static JsonElement.ArrayEnumerator Array(this JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Array ? item.EnumerateArray() : EmptyArray.EnumerateArray();
    internal static DateTimeOffset? Timestamp(this JsonElement value, string name) => DateTimeOffset.TryParse(value.Text(name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;
    private static readonly JsonElement EmptyObject = JsonSerializer.SerializeToElement(new { });
    private static readonly JsonElement EmptyArray = JsonSerializer.SerializeToElement(System.Array.Empty<string>());
}
