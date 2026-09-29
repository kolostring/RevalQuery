namespace CachingDemo.Client.Services;

public static class SearchService
{
    public static async Task<List<string>> SearchAsync(string term, CancellationToken? ct = null)
    {
        Console.WriteLine($"Request searching for \"{term}\"");
        string[] data = ["Apple", "Apricot", "Artichoke", "Asparagus", "Avocado", 
            "Banana", "Basil", "Beetroot", "Blackberry", "Blueberry", 
            "Broccoli", "Brussels Sprout", "Cabbage", "Cantaloupe", "Carrot", 
            "Cashew", "Cauliflower", "Celery", "Cherry", "Coconut", 
            "Corn", "Cucumber", "Date", "Dragonfruit", "Eggplant", 
            "Elderberry", "Endive", "Fig", "Garlic", "Ginger", 
            "Gooseberry", "Grape", "Grapefruit", "Guava", "Honeydew", 
            "Jackfruit", "Jalapeño", "Kale", "Kiwi", "Kumquat", 
            "Lemon", "Lettuce", "Lime", "Lychee", "Mandarin", 
            "Mango", "Mushroom", "Nectarine", "Okra", "Olive", 
            "Onion", "Orange", "Papaya", "Parsnip", "Peach", 
            "Pear", "Peas", "Pecan", "Pepper", "Persimmon", 
            "Pineapple", "Pistachio", "Plum", "Pomegranate", "Potato", 
            "Pumpkin", "Quince", "Radish", "Raspberry", "Rhubarb", 
            "Spinach", "Strawberry", "Tomato", "Turnip", "Watermelon"];
        // Simulate Network Latency
        await Task.Delay(1000, ct ?? new());

        // Simulate a random API Failure (10% chance)
        if (Random.Shared.Next(1, 11) == 1)
            throw new Exception("API is currently unavailable.");

        if (string.IsNullOrWhiteSpace(term)) return data.ToList();

        var res = data
            .Where(x => x.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Take(5)
            .ToList();

        Console.WriteLine($"Response for '{term}':\n" + string.Join(", ", res));
        return res;
    }
}