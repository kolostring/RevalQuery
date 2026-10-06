using System.Runtime.CompilerServices;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Mutation;
using RevalQuery.Core.Mutation.Options;
using RevalQuery.Core.Query;
using RevalQuery.Core.Query.Options;
using RevalQuery.Core.Registry;

namespace RevalQuery.Core.Hooks;

/// <summary>
/// Everything one component reads from a client, owned in one place. The component reads
/// through the hooks on every render and never holds an observer; the hooks subscribe what
/// was read, re-applies options on every read, and releases what a render stopped reading.
/// </summary>
/// <remarks>
/// <para>Created by <see cref="RevalClient.CreateHooks"/>, or injected when
/// <c>AddRevalQuery</c> registered it, which gives every consumer its own. Not tied to any UI
/// framework: a renderer for the framework attaches with <see cref="Attach"/>, reports each
/// finished render with <see cref="RenderCompleted"/>, and disposes the handle <see cref="Attach"/>
/// returned when the component goes away.</para>
/// <para><b>Lifetime.</b> Hooks live until the handle from <see cref="Attach"/> is disposed.
/// Disposing it releases the hooks: every query and mutation observer is disposed, and the
/// hooks cannot be used again, so create or inject new ones. They are not <see cref="IDisposable"/>,
/// which keeps a dependency injection container from holding on to one per component.</para>
/// <para><b>Identity.</b> A read belongs to a <i>call site</i>: the file and line of the
/// <c>Query</c> call, or the explicit slot passed in. Each site holds a set of query keys, so a
/// call site that runs in a loop or from a helper method holds one query per key it was given.
/// Two reads of one key share one observer for the whole hooks instance, whichever sites read it. The
/// observer is released only when no site holds the key any longer. Options are last read wins,
/// so two sites reading one key with different options will overwrite each other every render;
/// give them the same options.</para>
/// <para><b>Release.</b> A <see cref="RenderCompleted"/> sweep looks at every call site that was
/// read since the previous sweep, and releases the keys at that site that were not. A site that
/// was not read at all releases nothing. Reads between sweeps, from event handlers or from
/// renders that happen in later batches, count towards the next one. Nothing in the rule
/// consults a clock, so what is released is a function of what was rendered and nothing else.</para>
/// <para><b>Mutations</b> are held by call site or explicit key and are never released by a sweep.
/// They live until the hooks are released, because a running mutation must outlast the render that
/// started it.</para>
/// <para><b>Known limitations.</b> The sweep cannot tell a branch that is hidden from a branch
/// that has not rendered yet, so (1) a hidden branch keeps its queries, and any polling they
/// do, until the hooks are released or the branch renders again; put <c>Enabled(isVisible)</c>
/// in the options to pause them meanwhile. And (2) a call site that is read both on the page and
/// inside a child that loads asynchronously, with different keys, can release and recreate the
/// query each time the other one renders; read it through a separate getter or pass an explicit
/// slot. These stand until an alternative is found.</para>
/// <para>Safe to call from any thread. Observer and renderer callbacks are never invoked while the
/// lock of the hooks is held.</para>
/// </remarks>
public sealed class RevalHooks
{
    private abstract class Entry
    {
        public HashSet<Site> Holders { get; } = [];
        public abstract void Dispose();
    }

    private sealed class QueryEntry<TKey, TRes>(QueryObserver<TKey, TRes> observer) : Entry
        where TKey : ITuple
    {
        public QueryObserver<TKey, TRes> Observer { get; } = observer;
        public override void Dispose() => Observer.Dispose();
    }

    private sealed class MutationEntry<TParams, TRes>(MutationObserver<TParams, TRes> observer) : Entry
        where TParams : class
    {
        public MutationObserver<TParams, TRes> Observer { get; } = observer;
        public override void Dispose() => Observer.Dispose();
    }

    private sealed class Site(object id)
    {
        public object Id { get; } = id;
        public HashSet<ITuple> Held { get; } = new(QueryKeyComparer.Instance);
        public HashSet<ITuple> Read { get; } = new(QueryKeyComparer.Instance);
    }

    private readonly RevalClient _client;

    private readonly object _gate = new();
    private readonly Dictionary<ITuple, Entry> _queries = new(QueryKeyComparer.Instance);
    private readonly Dictionary<object, Site> _sites = [];
    private readonly Dictionary<(object Id, Type Params, Type Res), Entry> _mutations = [];

    private HostLink? _host;
    private bool _pending;
    private bool _isReleased;

    internal RevalHooks(RevalClient client) => _client = client;

    private sealed class HostLink(Action onChanged) : IDisposable
    {
        public Action OnChanged { get; } = onChanged;
        public RevalHooks? Hooks { get; set; }

        public void Dispose() => Hooks?.Release();
    }

    /// <summary>
    /// Reads a query, keyed by the call site. Subscribes on the first read of a key, re-applies
    /// <paramref name="options"/> on every later one, and marks the key as read for the next
    /// sweep.
    /// </summary>
    /// <remarks>
    /// Call it from the render, on every render, and read the returned state afterwards: it is
    /// the observer's own, so it follows the key. Two reads on one line share a site, which is
    /// harmless, because a site holds a set of keys.
    /// </remarks>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TRes">The response type.</typeparam>
    /// <param name="options">Query configuration, rebuilt by this render.</param>
    /// <param name="file">Filled in by the compiler.</param>
    /// <param name="line">Filled in by the compiler.</param>
    /// <exception cref="InvalidOperationException">The key is registered with another result type, or the hooks were released.</exception>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public IQueryState<TRes> Query<TKey, TRes>(
        QueryOptions<TKey, TRes> options,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
        where TKey : ITuple =>
        Query((file, line), options);

    /// <summary>Reads a query, keyed by the call site, from a builder.</summary>
    /// <inheritdoc cref="Query{TKey, TRes}(QueryOptions{TKey, TRes}, string, int)"/>
    public IQueryState<TRes> Query<TKey, TRes>(
        QueryOptionsBuilder<TKey, TRes> builder,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
        where TKey : ITuple =>
        Query((file, line), builder.Build());

    /// <summary>
    /// Reads a query under an explicit slot instead of the call site. The workaround for a call
    /// site that two components read with different keys, and for code the line number cannot
    /// distinguish.
    /// </summary>
    /// <remarks>
    /// Slots compare by value, so <c>"rows"</c> and a tuple such as <c>("rows", page)</c> both work.
    /// </remarks>
    /// <param name="slot">Identity of the read, in place of the call site.</param>
    /// <param name="options">Query configuration, rebuilt by this render.</param>
    /// <inheritdoc cref="Query{TKey, TRes}(QueryOptions{TKey, TRes}, string, int)" path="/typeparam|/exception"/>
    public IQueryState<TRes> Query<TKey, TRes>(object slot, QueryOptions<TKey, TRes> options)
        where TKey : ITuple
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(slot);

        QueryEntry<TKey, TRes>? existing;

        lock (_gate)
        {
            ThrowIfReleasedLocked();
            existing = Find<TKey, TRes>(options.Key);
            if (existing is not null) MarkRead(slot, options.Key, existing);
        }

        if (existing is not null)
        {
            try
            {
                existing.Observer.SetOptions(options);
            }
            catch (ObjectDisposedException) when (IsReleased())
            {
                ThrowReleased();
            }

            return existing.Observer.Query;
        }

        var observer = _client.Subscribe(options, OnObserverChanged);
        QueryEntry<TKey, TRes>? loser = null;
        QueryEntry<TKey, TRes>? created = null;

        lock (_gate)
        {
            if (_isReleased)
            {
                loser = new QueryEntry<TKey, TRes>(observer);
            }
            else if (Find<TKey, TRes>(options.Key) is { } raced)
            {
                loser = new QueryEntry<TKey, TRes>(observer);
                MarkRead(slot, options.Key, raced);
                existing = raced;
            }
            else
            {
                created = new QueryEntry<TKey, TRes>(observer);
                _queries[options.Key] = created;
                MarkRead(slot, options.Key, created);
            }
        }

        if (loser is not null)
        {
            loser.Dispose();
            if (existing is null) ThrowReleased();
            return existing!.Observer.Query;
        }

        return observer.Query;
    }

    /// <summary>Reads a query under an explicit slot, from a builder.</summary>
    /// <inheritdoc cref="Query{TKey, TRes}(object, QueryOptions{TKey, TRes})"/>
    public IQueryState<TRes> Query<TKey, TRes>(object slot, QueryOptionsBuilder<TKey, TRes> builder)
        where TKey : ITuple =>
        Query(slot, builder.Build());

    /// <summary>
    /// Reads a mutation, keyed by the call site. Creates it on the first read and re-applies
    /// <paramref name="options"/> on every later one.
    /// </summary>
    /// <remarks>
    /// A mutation is never released by a sweep. It lives until the hooks are released. Two reads on
    /// one line, or one in a loop, share one mutation: use <see cref="Mutation{TParams, TRes}(object, MutationOptions{TParams, TRes})"/>
    /// when each needs its own.
    /// </remarks>
    /// <typeparam name="TParams">The parameters type.</typeparam>
    /// <typeparam name="TRes">The response type.</typeparam>
    /// <param name="options">Mutation configuration, rebuilt by this render.</param>
    /// <param name="file">Filled in by the compiler.</param>
    /// <param name="line">Filled in by the compiler.</param>
    /// <exception cref="InvalidOperationException">The hooks were released.</exception>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public MutationState<TParams, TRes> Mutation<TParams, TRes>(
        MutationOptions<TParams, TRes> options,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
        where TParams : class =>
        Mutation((file, line), options);

    /// <summary>Reads a mutation, keyed by the call site, from a builder.</summary>
    /// <inheritdoc cref="Mutation{TParams, TRes}(MutationOptions{TParams, TRes}, string, int)"/>
    public MutationState<TParams, TRes> Mutation<TParams, TRes>(
        MutationOptionsBuilder<TParams, TRes> builder,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
        where TParams : class =>
        Mutation((file, line), builder.Build());

    /// <summary>Reads a mutation under an explicit key instead of the call site.</summary>
    /// <param name="key">Identity of the mutation, compared by value.</param>
    /// <param name="options">Mutation configuration, rebuilt by this render.</param>
    /// <inheritdoc cref="Mutation{TParams, TRes}(MutationOptions{TParams, TRes}, string, int)" path="/typeparam|/exception"/>
    public MutationState<TParams, TRes> Mutation<TParams, TRes>(object key, MutationOptions<TParams, TRes> options)
        where TParams : class
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(options);

        var id = (key, typeof(TParams), typeof(TRes));
        MutationEntry<TParams, TRes>? existing;

        lock (_gate)
        {
            ThrowIfReleasedLocked();
            existing = _mutations.TryGetValue(id, out var found) ? (MutationEntry<TParams, TRes>)found : null;
        }

        if (existing is not null)
        {
            existing.Observer.SetOptions(options);
            return existing.Observer.State;
        }

        var observer = _client.CreateMutation(options, OnObserverChanged);
        MutationEntry<TParams, TRes>? raced = null;
        var disposed = false;

        lock (_gate)
        {
            if (_isReleased) disposed = true;
            else if (_mutations.TryGetValue(id, out var found)) raced = (MutationEntry<TParams, TRes>)found;
            else _mutations[id] = new MutationEntry<TParams, TRes>(observer);
        }

        if (disposed || raced is not null)
        {
            observer.Dispose();
            if (disposed) ThrowReleased();
            return raced!.Observer.State;
        }

        return observer.State;
    }

    /// <summary>Reads a mutation under an explicit key, from a builder.</summary>
    /// <inheritdoc cref="Mutation{TParams, TRes}(object, MutationOptions{TParams, TRes})"/>
    public MutationState<TParams, TRes> Mutation<TParams, TRes>(object key, MutationOptionsBuilder<TParams, TRes> builder)
        where TParams : class =>
        Mutation(key, builder.Build());

    /// <summary>
    /// Reports that a render has finished. Releases, at every call site read since the previous
    /// call, the queries that were not read, and then starts counting reads afresh.
    /// </summary>
    /// <remarks>
    /// A site that was not read since the previous call releases nothing, and an observer another
    /// site still holds stays. Does nothing once the hooks are released.
    /// </remarks>
    public void RenderCompleted()
    {
        List<Entry>? released = null;

        lock (_gate)
        {
            if (_isReleased) return;

            foreach (var site in _sites.Values.ToArray())
            {
                if (site.Read.Count > 0)
                {
                    foreach (var key in site.Held.Where(k => !site.Read.Contains(k)).ToArray())
                    {
                        site.Held.Remove(key);
                        var entry = _queries[key];
                        entry.Holders.Remove(site);

                        if (entry.Holders.Count > 0) continue;

                        _queries.Remove(key);
                        (released ??= []).Add(entry);
                    }

                    site.Read.Clear();
                }

                if (site.Held.Count == 0) _sites.Remove(site.Id);
            }
        }

        if (released is null) return;
        foreach (var entry in released) entry.Dispose();
    }

    /// <summary>
    /// Attaches the component's renderer, which is told whenever an observer reports a change,
    /// including one raised by a read.
    /// </summary>
    /// <remarks>
    /// <para>The returned handle owns the hooks: disposing it releases them, disposing
    /// every query and mutation observer, and the hooks cannot be attached or read again.
    /// Disposing it more than once does nothing. One renderer at a time: attaching while another
    /// is attached throws. A change reported before any renderer attached is kept and delivered
    /// once, now. A read that starts a fetch reports a change, so the owner renders once more,
    /// and that render's reads report nothing new. <paramref name="onChanged"/> may be called
    /// from any thread.</para>
    /// </remarks>
    /// <param name="onChanged">Asks the component to render again.</param>
    /// <returns>A handle that releases the hooks when disposed.</returns>
    /// <exception cref="InvalidOperationException">A renderer is already attached, or the hooks were released.</exception>
    public IDisposable Attach(Action onChanged)
    {
        ArgumentNullException.ThrowIfNull(onChanged);

        var link = new HostLink(onChanged) { Hooks = this };
        bool flush;

        lock (_gate)
        {
            ThrowIfReleasedLocked();

            if (_host is not null)
                throw new InvalidOperationException("This RevalHooks instance already has a renderer attached.");

            _host = link;
            flush = _pending;
            _pending = false;
        }

        if (flush) onChanged();

        return link;
    }

    private void Release()
    {
        Entry[] all;

        lock (_gate)
        {
            if (_isReleased) return;
            _isReleased = true;
            all = [.. _queries.Values, .. _mutations.Values];
            _queries.Clear();
            _mutations.Clear();
            _sites.Clear();
            _host = null;
        }

        foreach (var entry in all) entry.Dispose();
    }

    private void OnObserverChanged()
    {
        HostLink? host;

        lock (_gate)
        {
            if (_isReleased) return;
            host = _host;
            if (host is null) _pending = true;
        }

        host?.OnChanged();
    }

    private void ThrowIfReleasedLocked()
    {
        if (_isReleased) ThrowReleased();
    }

    private bool IsReleased()
    {
        lock (_gate) return _isReleased;
    }

    private static void ThrowReleased() =>
        throw new InvalidOperationException(
            "This RevalHooks instance was released when its Attach handle was disposed. Inject or create a new one.");

    private QueryEntry<TKey, TRes>? Find<TKey, TRes>(TKey key) where TKey : ITuple
    {
        if (!_queries.TryGetValue(key, out var entry)) return null;

        return entry as QueryEntry<TKey, TRes> ?? throw new InvalidOperationException(
            $"Query key {key} is already read in these hooks with a different key or result type.");
    }

    private void MarkRead(object slot, ITuple key, Entry entry)
    {
        if (!_sites.TryGetValue(slot, out var site)) _sites[slot] = site = new Site(slot);

        site.Held.Add(key);
        site.Read.Add(key);
        entry.Holders.Add(site);
    }
}
