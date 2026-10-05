using System.Collections;
using System.Reflection;
using Microsoft.AspNetCore.Components;

namespace RevalQuery.Tests;

internal static class PersistentStateHarness
{
    private const BindingFlags Any =
        BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

    public static PersistentComponentState CreateEmpty(out IDictionary<string, byte[]> written)
    {
        var store = new Dictionary<string, byte[]>();
        written = store;

        var constructor = typeof(PersistentComponentState).GetConstructors(Any).Single();
        var arguments = constructor.GetParameters()
            .Select(p => p.Position == 0 ? store : NewInstance(p.ParameterType))
            .ToArray();

        return (PersistentComponentState)constructor.Invoke(arguments);
    }

    public static PersistentComponentState CreateFrom(IDictionary<string, byte[]> persisted)
    {
        var state = CreateEmpty(out _);

        var initialize = typeof(PersistentComponentState)
            .GetMethods(Any)
            .Single(m => m.Name == "InitializeExistingState");

        var arguments = initialize.GetParameters()
            .Select(p => p.Position == 0 ? persisted : DefaultOf(p.ParameterType))
            .ToArray();

        initialize.Invoke(state, arguments);

        return state;
    }

    public static async Task PersistAsync(PersistentComponentState state)
    {
        typeof(PersistentComponentState).GetProperty("PersistingState", Any)!.SetValue(state, true);

        var registered = (IEnumerable)typeof(PersistentComponentState)
            .GetField("_registeredCallbacks", Any)!.GetValue(state)!;

        foreach (var registration in registered)
        {
            var callback = registration.GetType()
                .GetProperties(Any)
                .Select(p => p.GetValue(registration))
                .OfType<Func<Task>>()
                .Single();

            await callback();
        }

        typeof(PersistentComponentState).GetProperty("PersistingState", Any)!.SetValue(state, false);
    }

    private static object NewInstance(Type type) => Activator.CreateInstance(type)!;

    private static object? DefaultOf(Type type) => type.IsValueType ? Activator.CreateInstance(type) : null;
}
