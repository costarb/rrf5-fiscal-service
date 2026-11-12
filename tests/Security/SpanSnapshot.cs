using System.Diagnostics;

namespace Rrf5.Fiscal.Tests.Security;

/// <summary>
/// Copies the in-memory exporter list. The exporter appends without a lock the test
/// can share, so a copy taken while a span ends is retried instead of failing.
/// </summary>
internal static class SpanSnapshot
{
    public static List<Activity> Of(List<Activity> spans)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return [.. spans];
            }
            catch (InvalidOperationException) when (attempt < 20)
            {
                Thread.Sleep(10);
            }
            catch (ArgumentException) when (attempt < 20)
            {
                Thread.Sleep(10);
            }
        }
    }
}
