using Microsoft.Extensions.Caching.Memory;

namespace WristOTP.Server.Services;

// MemoryCache removes expired entries lazily: only when the cache is used,
// and at most once per ExpirationScanFrequency. With no traffic an expired
// entry can stay in memory for hours — unreadable (TryGetValue skips it),
// but still there and still counted on the landing page. The relay promises
// that data is gone within a minute, so this sweeper removes expired
// entries every few seconds regardless of traffic.
//
// MemoryCache удаляет просроченные записи лениво: только при обращении к
// кэшу и не чаще ExpirationScanFrequency. Без запросов просроченная запись
// может висеть в памяти часами — прочитать её нельзя (TryGetValue её
// пропускает), но она всё ещё там и учитывается на стартовой странице. Релей
// обещает, что данные исчезают в течение минуты, поэтому этот фоновый сервис
// удаляет просроченные записи каждые несколько секунд, независимо от запросов.
public sealed class ExpirySweeper : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly IMemoryCache _cache;

    public ExpirySweeper(IMemoryCache cache) => _cache = cache;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_cache is not MemoryCache mc)
            return;

        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            // Compact(0): remove expired entries only, keep all live ones.
            // Compact(0): удалить только просроченные записи, живые не трогать.
            mc.Compact(0);
        }
    }
}
