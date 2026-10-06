using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using RevalQuery.Core;
using RevalQuery.Core.Abstractions.Caching;
using RevalQuery.Core.Configuration;
using RevalQuery.Core.Configuration.Options;
using RevalQuery.Core.Query;
using RevalQuery.Core.Query.Options;

namespace RevalQuery.Blazor.Tests;

public sealed class RecordingEviction : ICacheEvictionPolicy
{
    public ConcurrentQueue<ITuple> Registered { get; } = new();
    public ConcurrentQueue<ITuple> Cancelled { get; } = new();
#pragma warning disable CS0067
    public event Action<ITuple>? OnEvictionRequired;
#pragma warning restore CS0067
    public void RegisterForEviction(ITuple key, CacheOptions? cacheOptions) => Registered.Enqueue(key);
    public void CancelEviction(ITuple key) => Cancelled.Enqueue(key);
    public Task StopAsync() => Task.CompletedTask;
    public int RegisteredCount(int id) => Registered.Count(k => (int)k[1]! == id);
}

public sealed class Fx : IDisposable
{
    public RecordingEviction Eviction { get; } = new();
    public RevalClient Client { get; }
    public TimeSpan Stale { get; set; } = TimeSpan.Zero;
    public ConcurrentDictionary<int, int> Calls { get; } = new();
    public ConcurrentDictionary<int, TaskCompletionSource> Gates { get; } = new();
    public int TotalCalls => Calls.Values.Sum();

    public Fx(IServiceProvider sp) => Client = new RevalClient(sp, new RevalQueryOptions(), Eviction);

    public TaskCompletionSource Gate(int id) =>
        Gates.GetOrAdd(id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    public QueryOptionsBuilder<ValueTuple<string, int>, string> Detail(int id) =>
        QueryOptions.Create<ValueTuple<string, int>, string>(("detail", id), async _ =>
        {
            Calls.AddOrUpdate(id, 1, (_, n) => n + 1);
            if (Gates.TryGetValue(id, out var gate)) await gate.Task;
            return $"item{id}";
        }).ConfigureFetch(f => f.StaleTime(Stale));

    public QueryState<ValueTuple<string, int>, string>? State(int id) =>
        Client.FindQuery<string, ValueTuple<string, int>>(("detail", id));

    public bool Observed(int id) => State(id)?.HasObservers == true;

    public int CallsFor(int id) => Calls.TryGetValue(id, out var n) ? n : 0;

    public void Dispose() => Client.Dispose();
}
