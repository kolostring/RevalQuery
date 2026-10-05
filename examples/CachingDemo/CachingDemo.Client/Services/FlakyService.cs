using System.Collections.Concurrent;

namespace CachingDemo.Client.Services;

public static class FlakyService
{
    private static readonly ConcurrentDictionary<string, int> Attempts = new();

    public static event Action? OnAttempt;

    public static int AttemptsFor(string runId) => Attempts.GetValueOrDefault(runId);

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
