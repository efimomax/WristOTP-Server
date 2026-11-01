#if DEBUG
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using WristOTP.Server.Services;

namespace WristOTP.Server.Diagnostics;

// Live snapshot of the cache plus a log of recent requests — Debug build
// only. The whole file is wrapped in #if DEBUG, so in Release the class does
// not exist at all: even a misconfigured production server cannot use this
// logging path. Payload contents (iv/authTag/ciphertext) are never stored or
// logged here or by callers — only their size in bytes.
//
// RelayStore applies TTL and one-time reads identically in Debug and
// Release; this class only mirrors the real cache state and never weakens it.
//
// Живой снимок кэша и лог последних запросов — только для Debug-сборки.
// Весь файл обёрнут в #if DEBUG, поэтому в Release класса нет вовсе: даже
// неверно настроенный прод-сервер не может задействовать этот путь
// логирования. Содержимое payload (iv/authTag/ciphertext) нигде не
// сохраняется и не логируется — только его размер в байтах.
//
// RelayStore применяет TTL и одноразовое чтение одинаково в Debug и
// Release; этот класс лишь отражает реальное состояние кэша и не ослабляет его.
public sealed record CacheEntrySnapshot(
    string LookupId,
    int PayloadSizeBytes,
    DateTimeOffset StoredAtUtc);

public sealed record RequestLogEntry(
    DateTimeOffset TimestampUtc,
    string Method,
    string LookupId,
    int StatusCode,
    string Detail);

public sealed class RelayDiagnostics
{
    // Ring buffer size for the request log — no unbounded memory growth on a
    // long-running dev instance.
    //
    // Размер кольцевого буфера лога — без бесконечного роста памяти на долго
    // работающем dev-экземпляре.
    private const int MaxLogEntries = 200;

    private readonly ConcurrentDictionary<string, CacheEntrySnapshot> _entries = new();
    private readonly ConcurrentQueue<RequestLogEntry> _log = new();

    public void RecordPut(string lookupId, int payloadSizeBytes, DateTimeOffset storedAtUtc)
    {
        _entries[lookupId] = new CacheEntrySnapshot(lookupId, payloadSizeBytes, storedAtUtc);
        AppendLog(new RequestLogEntry(DateTimeOffset.UtcNow, "PUT", lookupId, StatusCodes.Status204NoContent, "stored"));
    }

    public void RecordGet(string lookupId, bool found)
    {
        // RelayStore.TryTakeOnce has already removed the entry from the real
        // cache (one-time read) — remove it from the snapshot too.
        //
        // RelayStore.TryTakeOnce уже удалил запись из реального кэша
        // (одноразовое чтение) — убираем её и из снимка.
        if (found)
        {
            _entries.TryRemove(lookupId, out _);
        }
        AppendLog(new RequestLogEntry(
            DateTimeOffset.UtcNow,
            "GET",
            lookupId,
            found ? StatusCodes.Status200OK : StatusCodes.Status404NotFound,
            found ? "downloaded (deleted)" : "not found"));
    }

    public void RecordDelete(string lookupId)
    {
        _entries.TryRemove(lookupId, out _);
        AppendLog(new RequestLogEntry(DateTimeOffset.UtcNow, "DELETE", lookupId, StatusCodes.Status204NoContent, "manually deleted"));
    }

    public void RecordRateLimited(string lookupId)
    {
        AppendLog(new RequestLogEntry(DateTimeOffset.UtcNow, "GET", lookupId, StatusCodes.Status429TooManyRequests, "rate limited"));
    }

    // Entries expire in the real cache silently, without any PUT/GET/Delete
    // event to react to. Without this cleanup an entry nobody claimed would
    // stay in the "Active cache entries" table forever. On every read the
    // snapshot drops everything older than the TTL, just like the real cache.
    //
    // В реальном кэше записи истекают молча, без события PUT/GET/Delete, на
    // которое можно было бы среагировать. Без этой очистки невостребованная
    // запись навсегда оставалась бы в таблице "Active cache entries". При
    // каждом чтении снимок отбрасывает всё старше TTL — как и реальный кэш.
    public IReadOnlyList<CacheEntrySnapshot> GetActiveEntries()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _entries.Values)
        {
            if (now - entry.StoredAtUtc >= RelayStore.Ttl)
            {
                _entries.TryRemove(entry.LookupId, out _);
            }
        }

        return _entries.Values
            .OrderByDescending(e => e.StoredAtUtc)
            .ToList();
    }

    public IReadOnlyList<RequestLogEntry> GetRecentLog()
    {
        return _log.ToArray()
            .OrderByDescending(e => e.TimestampUtc)
            .ToList();
    }

    private void AppendLog(RequestLogEntry entry)
    {
        _log.Enqueue(entry);
        while (_log.Count > MaxLogEntries && _log.TryDequeue(out _))
        {
        }
    }
}
#endif
