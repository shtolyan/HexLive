using System;
using System.Text;
using System.Threading;

namespace HexLive.Server.Llm
{

/// <summary>
/// Fixed, low-cardinality failure taxonomy for operator diagnostics. Values are
/// deliberately independent of endpoint, headers and response content.
/// </summary>
public enum LlmProviderFailureCategory
{
    Timeout,
    Transport,
    HttpStatus,
    ResponseMediaType,
    ResponseTooLarge,
    ResponseEncoding,
    ResponseJson,
    ResponseContract,
    Unexpected,
}

/// <summary>
/// Thread-safe warning and counter sink for provider workers. Each category is
/// warned once per provider lifetime; repeated failures are folded into the
/// periodic summary instead of producing one console line per request.
/// </summary>
public sealed class LlmProviderDiagnostics
{
    public const int MaxDiagnosticLineLength = 512;

    private static readonly string[] CategoryNames =
    {
        "timeout",
        "transport",
        "http-status",
        "response-media-type",
        "response-too-large",
        "response-encoding",
        "response-json",
        "response-contract",
        "unexpected",
    };

    private readonly Action<string> _writeWarning;
    private readonly long[] _pendingCounts = new long[CategoryNames.Length];
    private readonly int[] _warned = new int[CategoryNames.Length];

    public LlmProviderDiagnostics(Action<string> writeWarning)
    {
        _writeWarning = writeWarning ?? throw new ArgumentNullException(nameof(writeWarning));
    }

    public void RecordFailure(LlmProviderFailureCategory category)
    {
        var index = IndexOf(category);
        Interlocked.Increment(ref _pendingCounts[index]);
        if (Interlocked.Exchange(ref _warned[index], 1) == 0)
        {
            _writeWarning(
                $"[llm] provider failure category={CategoryNames[index]}; " +
                "repeats are counted in the periodic server status");
        }
    }

    /// <summary>
    /// Drains counts accumulated since the previous call. Returns null when
    /// there is nothing to report, so the one-minute status path stays quiet.
    /// </summary>
    public string? DrainSummary()
    {
        var summary = new StringBuilder("[llm] provider failures");
        var hasFailures = false;
        for (var i = 0; i < _pendingCounts.Length; i++)
        {
            var count = Interlocked.Exchange(ref _pendingCounts[i], 0);
            if (count <= 0)
            {
                continue;
            }

            hasFailures = true;
            summary.Append(' ')
                .Append(CategoryNames[i])
                .Append('=')
                .Append(count);
        }

        var text = hasFailures ? summary.ToString() : null;
        if (text is not null && text.Length > MaxDiagnosticLineLength)
        {
            throw new InvalidOperationException("LLM diagnostic summary exceeded its fixed bound.");
        }

        return text;
    }

    internal static string SafeMessage(LlmProviderFailureCategory category) =>
        "LLM provider failure category=" + CategoryNames[IndexOf(category)] + ".";

    private static int IndexOf(LlmProviderFailureCategory category)
    {
        var index = (int)category;
        if (index < 0 || index >= CategoryNames.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(category));
        }

        return index;
    }
}

internal sealed class LlmProviderFailureException : Exception
{
    public LlmProviderFailureException(
        LlmProviderFailureCategory category,
        Exception? innerException = null)
        : base(LlmProviderDiagnostics.SafeMessage(category), innerException)
    {
        Category = category;
    }

    public LlmProviderFailureCategory Category { get; }
}

}
