using System.Text;

namespace DemoTracer;

internal static class DemoTracerCrosshairCode
{
    private const int MaxPublishedBytes = 63;

    public static string? Normalize(string? code)
    {
        var trimmed = code?.Trim();
        return string.IsNullOrEmpty(trimmed) ||
               Encoding.UTF8.GetByteCount(trimmed) > MaxPublishedBytes
            ? null
            : trimmed;
    }
}
