using System.Reflection;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Query;

namespace RevalQuery.Core.Tests;

public class ReadOnlyQueryStateTests
{
    public static TheoryData<Type> QueryStateTypes =>
    [
        typeof(IObservableQueryState),
        typeof(IQueryState),
        typeof(IQueryState<>),
        typeof(QueryState<,>),
    ];

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
