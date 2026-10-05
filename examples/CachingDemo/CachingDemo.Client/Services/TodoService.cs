namespace CachingDemo.Client.Services;

public static class TodoService
{
    private static readonly List<string> Items = ["Read the ADRs", "Run the examples"];
    private static readonly Lock Gate = new();

    public static async Task<List<string>> GetAsync(CancellationToken ct = default)
    {
        await Task.Delay(400, ct);
        lock (Gate) return [.. Items];
    }

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
