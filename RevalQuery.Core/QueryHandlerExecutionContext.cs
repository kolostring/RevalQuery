using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace RevalQuery.Core;

public sealed class QueryHandlerExecutionContext<TKey> where TKey : ITuple
{
    public required TKey Key { get; init; }
    public required IServiceProvider ServiceProvider { get; init; }
    public CancellationToken CancellationToken { get; set; }
}