using System.Runtime.CompilerServices;
using RevalQuery.Core.Abstractions.Query;
using RevalQuery.Core.Mutation;
using RevalQuery.Core.Mutation.Options;
using RevalQuery.Core.Query;
using RevalQuery.Core.Query.Options;
using RevalQuery.Core.Registry;

namespace RevalQuery.Core.Scope;

/// <summary>
/// Everything one component reads from a client, owned in one place. The component reads
/// through the scope on every render and never holds an observer; the scope subscribes what
/// was read, re-applies options on every read, and releases what a render stopped reading.
/// </summary>
/// <remarks>
/// <para>Created by <see cref="QueryClient.CreateScope"/>. Not tied to any UI framework: a host
/// for the framework attaches with <see cref="Attach"/>, reports each finished render with
/// <see cref="RenderCompleted"/>, and disposes the scope with the component.</para>
/// <para><b>Identity.</b> A read belongs to a <i>call site</i>: the file and line of the
/// <c>Query</c> call, or the explicit slot passed in. Each site holds a set of query keys, so a
/// call site that runs in a loop or from a helper method holds one query per key it was given.
/// Two reads of one key share one observer for the whole scope, whichever sites read it. The
/// observer is released only when no site holds the key any longer. Options are last read wins,
/// so two sites reading one key with different options will overwrite each other every render;
/// give them the same options.</para>
/// <para><b>Release.</b> A <see cref="RenderCompleted"/> sweep looks at every call site that was
/// read since the previous sweep, and releases the keys at that site that were not. A site that
/// was not read at all releases nothing. Reads between sweeps, from event handlers or from
/// renders that happen in later batches, count towards the next one. Nothing in the rule
/// consults a clock, so what is released is a function of what was rendered and nothing else.</para>
/// <para><b>Mutations</b> are held by call site or explicit key and are never released by a sweep.
/// They live until the scope is disposed, because a running mutation must outlast the render that
/// started it.</para>
/// <para><b>Known limitations.</b> The sweep cannot tell a branch that is hidden from a branch
/// that has not rendered yet, so (1) a hidden branch keeps its queries, and any polling they
/// do, until the scope is disposed or the branch renders again; put <c>Enabled(isVisible)</c>
/// in the options to pause them meanwhile. And (2) a call site that is read both on the page and
/// inside a child that loads asynchronously, with different keys, can release and recreate the
/// query each time the other one renders; read it through a separate getter or pass an explicit
/// slot. These stand until an alternative is found.</para>
/// <para>Safe to call from any thread. Observer and host callbacks are never invoked while the
/// scope's lock is held.</para>
/// </remarks>
public sealed class QueryScope : IDisposable
{
    private abstract class Entry
    {
        // The call sites holding this entry. It is released when the last one lets go.
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

    private readonly QueryClient _client;

    // Guards the entries, the sites and the host link. Never held across a call into an
    // observer, the client or the host, because any of them may call back into the scope.
    private readonly object _gate = new();
    private readonly Dictionary<ITuple, Entry> _queries = new(QueryKeyComparer.Instance);
    private readonly Dictionary<object, Site> _sites = [];
    private readonly Dictionary<(object Id, Type Params, Type Res), Entry> _mutations = [];

    private HostLink? _host;
    private bool _pending;
    private bool _isDisposed;

    // Set on a thread while it is inside Query() or Mutation(). A notification raised there
    // is an observer reporting what the render is about to read anyway, so it is not forwarded.
    [ThreadStatic] private static QueryScope? _reading;

    internal QueryScope(QueryClient client) => _client = client;

    private sealed class HostLink(Action onChanged) : IDisposable
    {
        public Action OnChanged { get; } = onChanged;
        public QueryScope? Scope { get; set; }

        public void Dispose() => Scope?.Detach(this);
    }

    // ---- reads ----

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
    /// <exception cref="InvalidOperationException">The key is registered with another result type.</exception>
    /// <exception cref="ObjectDisposedException">The scope or the client was disposed.</exception>
    public IQueryState<TRes> Query<TKey, TRes>(
        QueryOptions<TKey, TRes> options,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
        where TKey : ITuple =>
        Query(options, (file, line));

    /// <summary>Reads a query, keyed by the call site, from a builder.</summary>
    /// <inheritdoc cref="Query{TKey, TRes}(QueryOptions{TKey, TRes}, string, int)"/>
    public IQueryState<TRes> Query<TKey, TRes>(
        QueryOptionsBuilder<TKey, TRes> builder,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
        where TKey : ITuple =>
        Query(builder.Build(), (file, line));

    /// <summary>
    /// Reads a query under an explicit slot instead of the call site. The workaround for a call
    /// site that two components read with different keys, and for code the line number cannot
    /// distinguish.
    /// </summary>
    /// <remarks>
    /// Slots compare by value, so a tuple such as <c>("rows", page)</c> works. Do not pass a
    /// string: overload resolution prefers the call-site overload, which takes a string as its
    /// file name. Wrap it, for example <c>("rows", 0)</c>.
    /// </remarks>
    /// <param name="options">Query configuration, rebuilt by this render.</param>
    /// <param name="slot">Identity of the read, in place of the call site.</param>
    /// <inheritdoc cref="Query{TKey, TRes}(QueryOptions{TKey, TRes}, string, int)" path="/typeparam|/exception"/>
    public IQueryState<TRes> Query<TKey, TRes>(QueryOptions<TKey, TRes> options, object slot)
        where TKey : ITuple
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(slot);

        QueryEntry<TKey, TRes>? existing;

        lock (_gate)
        {
            ThrowIfDisposedLocked();
            existing = Find<TKey, TRes>(options.Key);
            if (existing is not null) MarkRead(slot, options.Key, existing);
        }

        var prior = _reading;
        _reading = this;

        try
        {
            if (existing is not null)
            {
                existing.Observer.SetOptions(options);
                return existing.Observer.Query;
            }

            var observer = _client.Subscribe(options, OnObserverChanged);
            QueryEntry<TKey, TRes>? loser = null;
            QueryEntry<TKey, TRes>? created = null;

            lock (_gate)
            {
                if (_isDisposed)
                {
                    loser = new QueryEntry<TKey, TRes>(observer);
                }
                else if (Find<TKey, TRes>(options.Key) is { } raced)
                {
                    // Another thread read the same key while this one was subscribing.
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
                ObjectDisposedException.ThrowIf(existing is null, this);
                return existing!.Observer.Query;
            }

            return observer.Query;
        }
        finally
        {
            _reading = prior;
        }
    }

    /// <summary>Reads a query under an explicit slot, from a builder.</summary>
    /// <inheritdoc cref="Query{TKey, TRes}(QueryOptions{TKey, TRes}, object)"/>
    public IQueryState<TRes> Query<TKey, TRes>(QueryOptionsBuilder<TKey, TRes> builder, object slot)
        where TKey : ITuple =>
        Query(builder.Build(), slot);

    /// <summary>
    /// Reads a mutation, keyed by the call site. Creates it on the first read and re-applies
    /// <paramref name="options"/> on every later one.
    /// </summary>
    /// <remarks>
    /// A mutation is never released by a sweep. It lives until the scope is disposed. Two reads on
    /// one line, or one in a loop, share one mutation: use <see cref="Mutation{TParams, TRes}(object, MutationOptions{TParams, TRes})"/>
    /// when each needs its own.
    /// </remarks>
    /// <typeparam name="TParams">The parameters type.</typeparam>
    /// <typeparam name="TRes">The response type.</typeparam>
    /// <param name="options">Mutation configuration, rebuilt by this render.</param>
    /// <param name="file">Filled in by the compiler.</param>
    /// <param name="line">Filled in by the compiler.</param>
    /// <exception cref="ObjectDisposedException">The scope or the client was disposed.</exception>
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
            ThrowIfDisposedLocked();
            existing = _mutations.TryGetValue(id, out var found) ? (MutationEntry<TParams, TRes>)found : null;
        }

        var prior = _reading;
        _reading = this;

        try
        {
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
                if (_isDisposed) disposed = true;
                else if (_mutations.TryGetValue(id, out var found)) raced = (MutationEntry<TParams, TRes>)found;
                else _mutations[id] = new MutationEntry<TParams, TRes>(observer);
            }

            if (disposed || raced is not null)
            {
                observer.Dispose();
                ObjectDisposedException.ThrowIf(disposed, this);
                return raced!.Observer.State;
            }

            return observer.State;
        }
        finally
        {
            _reading = prior;
        }
    }

    /// <summary>Reads a mutation under an explicit key, from a builder.</summary>
    /// <inheritdoc cref="Mutation{TParams, TRes}(object, MutationOptions{TParams, TRes})"/>
    public MutationState<TParams, TRes> Mutation<TParams, TRes>(object key, MutationOptionsBuilder<TParams, TRes> builder)
        where TParams : class =>
        Mutation(key, builder.Build());

    // ---- sweep ----

    /// <summary>
    /// Reports that a render has finished. Releases, at every call site read since the previous
    /// call, the queries that were not read, and then starts counting reads afresh.
    /// </summary>
    /// <remarks>
    /// A site that was not read since the previous call releases nothing, and an observer another
    /// site still holds stays. Does nothing once the scope is disposed.
    /// </remarks>
    public void RenderCompleted()
    {
        List<Entry>? released = null;

        lock (_gate)
        {
            if (_isDisposed) return;

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

    // ---- host ----

    /// <summary>
    /// Attaches the component's host, which is told whenever an observer reports a change that
    /// the render in progress was not about to read anyway.
    /// </summary>
    /// <remarks>
    /// One host at a time: attaching while another is attached throws, and disposing the returned
    /// handle frees the scope for the next. A change reported before any host attached is kept
    /// and delivered once, now. <paramref name="onChanged"/> may be called from any thread.
    /// </remarks>
    /// <param name="onChanged">Asks the component to render again.</param>
    /// <returns>A handle that detaches the host.</returns>
    /// <exception cref="InvalidOperationException">A host is already attached.</exception>
    /// <exception cref="ObjectDisposedException">The scope was disposed.</exception>
    public IDisposable Attach(Action onChanged)
    {
        ArgumentNullException.ThrowIfNull(onChanged);

        var link = new HostLink(onChanged) { Scope = this };
        bool flush;

        lock (_gate)
        {
            ThrowIfDisposedLocked();

            if (_host is not null)
                throw new InvalidOperationException("This QueryScope already has a host attached.");

            _host = link;
            flush = _pending;
            _pending = false;
        }

        if (flush) onChanged();

        return link;
    }

    private void Detach(HostLink link)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_host, link)) _host = null;
        }
    }

    private void OnObserverChanged()
    {
        if (ReferenceEquals(_reading, this)) return;

        HostLink? host;

        lock (_gate)
        {
            if (_isDisposed) return;
            host = _host;
            if (host is null) _pending = true;
        }

        host?.OnChanged();
    }

    // ---- teardown ----

    /// <summary>
    /// Disposes every observer, queries and mutations alike, and detaches the host. Safe to call
    /// more than once. Reads afterwards throw; sweeps and notifications are ignored.
    /// </summary>
    public void Dispose()
    {
        Entry[] all;

        lock (_gate)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            all = [.. _queries.Values, .. _mutations.Values];
            _queries.Clear();
            _mutations.Clear();
            _sites.Clear();
            _host = null;
        }

        foreach (var entry in all) entry.Dispose();
    }

    // ---- helpers; callers hold the lock ----

    private void ThrowIfDisposedLocked() => ObjectDisposedException.ThrowIf(_isDisposed, this);

    private QueryEntry<TKey, TRes>? Find<TKey, TRes>(TKey key) where TKey : ITuple
    {
        if (!_queries.TryGetValue(key, out var entry)) return null;

        return entry as QueryEntry<TKey, TRes> ?? throw new InvalidOperationException(
            $"Query key {key} is already read in this scope with a different key or result type.");
    }

    private void MarkRead(object slot, ITuple key, Entry entry)
    {
        if (!_sites.TryGetValue(slot, out var site)) _sites[slot] = site = new Site(slot);

        site.Held.Add(key);
        site.Read.Add(key);
        entry.Holders.Add(site);
    }
}
