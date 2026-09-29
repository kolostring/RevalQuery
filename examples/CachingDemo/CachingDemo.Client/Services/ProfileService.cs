namespace CachingDemo.Client.Services;

/// <summary>
/// A user directory and its orders, standing in for two endpoints where the second needs an
/// id the first has to supply.
/// </summary>
public static class ProfileService
{
    private static readonly string[] Users = ["ada", "grace", "linus"];

    private static readonly Dictionary<string, string[]> Orders = new()
    {
        ["ada"] = ["Analytical engine, 1 unit", "Punch cards, 500 units"],
        ["grace"] = ["Compiler licence, 3 seats", "Nanoseconds, 1 bundle"],
        ["linus"] = ["Penguin plushie, 2 units"],
    };

    /// <summary>Lists the users. Slow on purpose, so the dependent query is visibly blocked.</summary>
    public static async Task<List<string>> GetUsersAsync(CancellationToken ct = default)
    {
        await Task.Delay(800, ct);
        return [.. Users];
    }

    /// <summary>Lists one user's orders.</summary>
    public static async Task<List<string>> GetOrdersAsync(string user, CancellationToken ct = default)
    {
        await Task.Delay(600, ct);
        return Orders.TryGetValue(user, out var orders) ? [.. orders] : [];
    }
}
