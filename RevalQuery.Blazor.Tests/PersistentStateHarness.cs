using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Infrastructure;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.Logging;

namespace RevalQuery.Blazor.Tests;

/// <summary>
/// One side of a prerender: the framework's public <see cref="ComponentStatePersistenceManager"/>,
/// restored from and persisted into an in-memory store, persisting on a bUnit renderer.
/// </summary>
internal sealed class PersistentStateHarness : IDisposable
{
    private readonly ComponentStatePersistenceManager _manager;
    private readonly FailureLog _log;
    private readonly Bunit.TestContext _context = new();

    private PersistentStateHarness(ComponentStatePersistenceManager manager, FailureLog log)
    {
        _manager = manager;
        _log = log;
    }

    public PersistentComponentState State => _manager.State;

    public static Task<PersistentStateHarness> CreateEmptyAsync() =>
        CreateFromAsync(new Dictionary<string, byte[]>());

    public static async Task<PersistentStateHarness> CreateFromAsync(IDictionary<string, byte[]> persisted)
    {
        var log = new FailureLog();
        var manager = new ComponentStatePersistenceManager(log);

        await manager.RestoreStateAsync(new MemoryStore(persisted));

        return new PersistentStateHarness(manager, log);
    }

    /// <summary>
    /// Runs every persisting callback once and returns what they wrote.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Nothing was registered to persist, or a callback threw.
    /// </exception>
    public async Task<Dictionary<string, byte[]>> PersistAsync()
    {
        var store = new MemoryStore(new Dictionary<string, byte[]>());

        await _manager.PersistStateAsync(store, (Renderer)_context.Renderer);

        if (_log.Failure is { } failure)
            throw new InvalidOperationException("A persisting callback threw.", failure);

        if (store.Registrations == 0)
            throw new InvalidOperationException("Nothing was registered to persist.");

        return store.Written;
    }

    public void Dispose() => _context.Dispose();

    private sealed class MemoryStore(IDictionary<string, byte[]> persisted) : IPersistentComponentStateStore
    {
        public int Registrations { get; private set; }

        public Dictionary<string, byte[]> Written { get; } = new(StringComparer.Ordinal);

        public Task<IDictionary<string, byte[]>> GetPersistedStateAsync() =>
            Task.FromResult<IDictionary<string, byte[]>>(new Dictionary<string, byte[]>(persisted));

        public Task PersistStateAsync(IReadOnlyDictionary<string, byte[]> state)
        {
            foreach (var (key, value) in state) Written[key] = value;
            return Task.CompletedTask;
        }

        // The manager asks once per registered callback, on every supported framework.
        public bool SupportsRenderMode(IComponentRenderMode renderMode)
        {
            Registrations++;
            return true;
        }
    }

    private sealed class FailureLog : ILogger<ComponentStatePersistenceManager>
    {
        public Exception? Failure { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error) Failure ??= exception ?? new Exception(formatter(state, exception));
        }
    }
}
