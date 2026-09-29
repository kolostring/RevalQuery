using System.Collections;
using System.Reflection;
using Microsoft.AspNetCore.Components;

namespace RevalQuery.Tests;

/// <summary>
/// Drives PersistentComponentState without a renderer, so the prerender transfer can be tested
/// end to end from a plain xunit process.
/// </summary>
/// <remarks>
/// The framework keeps the constructor and the persisting phase internal, and their shape has
/// changed between .NET 8 and .NET 10, so everything here is resolved by arity rather than by a
/// fixed signature. This is a test harness standing in for bUnit until the Blazor test project
/// arrives; the library itself touches nothing internal.
/// </remarks>
internal static class PersistentStateHarness
{
    private const BindingFlags Any =
        BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

    /// <summary>
    /// Creates a state with nothing in it, as a server has at the start of a prerender.
    /// </summary>
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

    /// <summary>
    /// Creates a state holding what a prerender wrote, as a client has at startup.
    /// </summary>
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

    /// <summary>
    /// Runs every callback registered through RegisterOnPersisting, as the end of a prerender does.
    /// </summary>
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
