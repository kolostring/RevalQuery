using System.Collections.Concurrent;

namespace CachingDemo.Client.Services;

/// <summary>
/// A handler that fails a fixed number of times before it succeeds, and counts every call.
/// </summary>
/// <remarks>
/// Exists so the retry demo can show what "Retry(n)" actually buys: the count below is the
/// number of times the handler ran, which is the number RevalQuery decides.
/// </remarks>
public static class FlakyService
{
    private static readonly ConcurrentDictionary<string, int> Attempts = new();

    /// <summary>
    /// Raised every time the handler runs, so a page can show the attempt count climbing
    /// during a retry rather than only once the query settles.
    /// </summary>
    public static event Action? OnAttempt;

    /// <summary>
    /// How many times the handler has run for this run id.
    /// </summary>
    public static int AttemptsFor(string runId) => Attempts.GetValueOrDefault(runId);

    /// <summary>
    /// Fails until it has been called <paramref name="failuresBeforeSuccess"/> times, then
    /// returns the attempt number that finally worked.
    /// </summary>
    public static async Task<string> LoadAsync(
        string runId,
        int failuresBeforeSuccess,
        CancellationToken ct = default)
    {
        var attempt = Attempts.AddOrUpdate(runId, 1, static (_, current) => current + 1);

        OnAttempt?.Invoke();

        await Task.Delay(200, ct);

        if (attempt <= failuresBeforeSuccess)
        {
            throw new InvalidOperationException(
                $"Attempt {attempt} failed. This handler fails its first {failuresBeforeSuccess}.");
        }

        return $"Succeeded on attempt {attempt}.";
    }
}
