namespace MudBlazorDemo.Services;

public sealed record Product(int Id, string Name, string Category, decimal Price);

public sealed record ProductDetail(Product Product, string Description, int Stock);

public sealed record Review(string Author, int Rating, string Comment);

public static class CatalogService
{
    private static readonly Product[] Products =
    [
        new(1, "Espresso machine", "Kitchen", 349.00m),
        new(2, "Burr grinder", "Kitchen", 189.50m),
        new(3, "Cast iron pan", "Kitchen", 64.00m),
        new(4, "Mechanical keyboard", "Desk", 129.99m),
        new(5, "Monitor arm", "Desk", 89.00m),
        new(6, "Desk lamp", "Desk", 45.00m),
        new(7, "Running shoes", "Outdoor", 119.00m),
        new(8, "Rain shell", "Outdoor", 210.00m),
        new(9, "Trail pack", "Outdoor", 95.00m),
    ];

    private static readonly Dictionary<int, List<Review>> Reviews = new()
    {
        [1] = [new("ada", 5, "Pulls a better shot than the café downstairs.")],
        [4] = [new("linus", 4, "Loud, in the way I wanted.")],
    };

    private static readonly Lock Gate = new();

    public static async Task<List<Product>> GetProductsAsync(CancellationToken ct = default)
    {
        await Task.Delay(900, ct);
        return [.. Products];
    }

    public static async Task<ProductDetail> GetProductAsync(int id, CancellationToken ct = default)
    {
        await Task.Delay(700, ct);

        var product = Products.FirstOrDefault(p => p.Id == id)
            ?? throw new KeyNotFoundException($"No product {id}.");

        return new ProductDetail(
            product,
            $"A thoroughly average description of the {product.Name.ToLowerInvariant()}, " +
            "written by somebody who has never used one.",
            Stock: 3 + product.Id * 2);
    }

    public static IReadOnlyList<string> SearchRequests
    {
        get { lock (Gate) return [.. SearchLog]; }
    }

    private static readonly List<string> SearchLog = [];

    public static async Task<List<Product>> SearchAsync(string term, CancellationToken ct = default)
    {
        lock (Gate)
        {
            SearchLog.Add($"\"{term}\" at {DateTimeOffset.Now:HH:mm:ss}");
            if (SearchLog.Count > 50) SearchLog.RemoveAt(0);
        }

        await Task.Delay(500, ct);

        return string.IsNullOrWhiteSpace(term)
            ? [.. Products]
            : [.. Products.Where(p => p.Name.Contains(term, StringComparison.OrdinalIgnoreCase))];
    }

    public static async Task<List<Review>> GetReviewsAsync(int productId, CancellationToken ct = default)
    {
        await Task.Delay(600, ct);
        lock (Gate) return Reviews.TryGetValue(productId, out var list) ? [.. list] : [];
    }

    public static async Task<Review> AddReviewAsync(int productId, Review review, CancellationToken ct = default)
    {
        await Task.Delay(700, ct);

        if (!review.Comment.Trim().Contains(' '))
        {
            throw new InvalidOperationException("A review needs more than one word.");
        }

        lock (Gate)
        {
            if (!Reviews.TryGetValue(productId, out var list)) Reviews[productId] = list = [];
            list.Add(review);
        }

        return review;
    }
}
