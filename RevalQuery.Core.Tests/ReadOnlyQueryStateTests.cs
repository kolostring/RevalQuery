using System.Reflection;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Query;

namespace RevalQuery.Core.Tests;

public class ReadOnlyQueryStateTests
{
    // Discovered rather than listed, so a public state type added later is covered without
    // anyone remembering to name it here.
    private static IEnumerable<Type> PublicQueryStateTypes =>
        typeof(IObservableQueryState).Assembly.GetExportedTypes()
            .Where(typeof(IObservableQueryState).IsAssignableFrom);

    public static TheoryData<Type> QueryStateTypes => [.. PublicQueryStateTypes];

    [Fact]
    public void Discovery_Finds_The_Known_Query_State_Types()
    {
        Assert.Superset(
            new HashSet<Type> { typeof(IObservableQueryState), typeof(IQueryState), typeof(IQueryState<>), typeof(QueryState<,>) },
            PublicQueryStateTypes.ToHashSet());
    }

    [Theory]
    [MemberData(nameof(QueryStateTypes))]
    public void No_Query_State_Type_Has_A_Public_Property_Setter(Type type)
    {
        var settable = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(p => p.SetMethod is { IsPublic: true })
            .Select(p => p.Name);

        Assert.Empty(settable);
    }
}
