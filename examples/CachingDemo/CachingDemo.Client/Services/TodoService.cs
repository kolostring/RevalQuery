namespace CachingDemo.Client.Services;

/// <summary>
/// An in-memory list behind an async façade, so a mutation has something to write to and the
/// query that reads it has something to be invalidated by.
/// </summary>
public static class TodoService
{
    private static readonly List<string> Items = ["Read the ADRs", "Run the examples"];
    private static readonly Lock Gate = new();

    /// <summary>Reads the list.</summary>
    public static async Task<List<string>> GetAsync(CancellationToken ct = default)
    {
        await Task.Delay(400, ct);
        lock (Gate) return [.. Items];
    }

    /// <summary>
    /// Appends an item. Rejects anything containing "fail", which is how the demo shows a
    /// mutation failing without retrying: mutations default to zero retries.
    /// </summary>
    public static async Task<string> AddAsync(string text, CancellationToken ct = default)
    {
        await Task.Delay(400, ct);

        if (text.Contains("fail", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The server refused \"{text}\".");
        }

        lock (Gate) Items.Add(text);
        return text;
    }
}
