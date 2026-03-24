using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace RevalQuery.Core;

public class QueryGarbageCollector
{
    private readonly Dictionary<int, (ITuple Key, DateTime Expiry)> _deathRow = new();
    private readonly TimeSpan _gcInterval;

    public event Action<ITuple>? OnEvictionRequired;

    public QueryGarbageCollector(TimeSpan? gcInterval = null)
    {
        _gcInterval = gcInterval ?? TimeSpan.FromMinutes(1);
        _ = StartAsync();
    }

    public void RegisterForEviction(ITuple key, CacheOptions cacheOptions)
    {
        var hash = GetHash(key);
        _deathRow[hash.ToHashCode()] = (key, DateTime.UtcNow.Add(cacheOptions.GcTime!));
    }

    public void CancelEviction(ITuple key)
    {
        var hash = GetHash(key);
        _deathRow.Remove(hash.ToHashCode());
    }

    private async Task StartAsync(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_gcInterval, ct);

            var now = DateTime.UtcNow;
            var expired = _deathRow.Where(x => x.Value.Expiry <= now).ToList();

            foreach (var entry in expired)
            {
                _deathRow.Remove(entry.Key);
                OnEvictionRequired?.Invoke(entry.Value.Key);
            }
        }
    }

    private static HashCode GetHash(ITuple key)
    {
        var hash = new HashCode();

        for (int i = 0; i < key.Length; i++)
        {
            hash.Add(key[i]);
        }

        return hash;
    }
}