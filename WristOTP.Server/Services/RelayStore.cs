using Microsoft.Extensions.Caching.Memory;
using WristOTP.Server.Models;

namespace WristOTP.Server.Services;

// Purely ephemeral relay: an entry lives in IMemoryCache for at most one
// minute OR until the first successful read, whichever comes first. No
// persistent storage, profiles or backups. Entries are addressed by a
// LookupID that the phone and the watch derive independently via
// HKDF(ODID, code, "lookup"); this class knows nothing about it and treats
// it as an opaque key.
//
// Чисто эфемерный релей: запись живёт в IMemoryCache не дольше минуты ИЛИ
// до первого успешного чтения — что наступит раньше. Никакого постоянного
// хранения, профилей и бэкапов. Записи адресуются по LookupID, который
// телефон и часы вычисляют независимо через HKDF(ODID, code, "lookup");
// этот класс о нём ничего не знает и хранит как непрозрачный ключ.
public sealed class RelayStore
{
    // internal rather than private: the Debug diagnostics use it to expire
    // their snapshot without duplicating the constant.
    //
    // internal, а не private: Debug-диагностика использует его для очистки
    // своего снимка, не дублируя константу.
    internal static readonly TimeSpan Ttl = TimeSpan.FromMinutes(1);

    private readonly IMemoryCache _cache;
    private readonly RelayStats _stats;

    public RelayStore(IMemoryCache cache, RelayStats stats)
    {
        _cache = cache;
        _stats = stats;
    }

    // Entry count of MemoryCache. Expired entries are removed by
    // ExpirySweeper within a few seconds, so this is "how many unclaimed
    // entries are stored right now". Safe to show publicly — unlike
    // RelayDiagnostics, there is no LookupID here.
    //
    // Число записей в MemoryCache. Просроченные записи удаляет ExpirySweeper
    // в течение нескольких секунд, поэтому это "сколько невостребованных
    // записей хранится сейчас". Безопасно показывать публично — в отличие от
    // RelayDiagnostics, LookupID тут нет.
    public int ActiveCount => _cache is MemoryCache mc ? mc.Count : 0;

    public void Put(string lookupId, TokenPayload payload)
    {
        _cache.Set(lookupId, payload, Ttl);
        _stats.RecordPut();
    }

    // One-time read: a found entry is removed immediately regardless of its
    // remaining TTL, so a repeated GET with the same LookupID always returns
    // "not found". Same behaviour in Debug and Release.
    //
    // Одноразовое чтение: найденная запись сразу удаляется независимо от
    // оставшегося TTL, поэтому повторный GET с тем же LookupID всегда
    // возвращает "не найдено". Поведение одинаково в Debug и Release.
    public bool TryTakeOnce(string lookupId, out TokenPayload? payload)
    {
        if (_cache.TryGetValue(lookupId, out payload))
        {
            _cache.Remove(lookupId);
            _stats.RecordClaimed();
            return true;
        }

        payload = null;
        return false;
    }

#if DEBUG
    // Manual deletion — only for the Delete button on /debug. Does not exist
    // in Release: a regular client must never be able to wipe someone else's
    // entry knowing only its LookupID.
    //
    // Ручное удаление — только для кнопки Delete на /debug. В Release его
    // нет: обычный клиент не должен иметь возможности стереть чужую запись,
    // зная лишь её LookupID.
    public void Delete(string lookupId) => _cache.Remove(lookupId);
#endif
}
