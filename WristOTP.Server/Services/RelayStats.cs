namespace WristOTP.Server.Services;

// Aggregated statistics for the public landing page (GET / and
// GET /api/v1/stats) — the only thing exposed in BOTH builds, unlike
// RelayDiagnostics (Debug only, shows real LookupIDs and a request log).
// Counters only: no LookupID, payload size or timestamp of a specific entry
// ever leaves the server, so it is safe to show publicly.
//
// Агрегированная статистика для публичной стартовой страницы (GET / и
// GET /api/v1/stats) — единственное, что видно снаружи в ОБЕИХ сборках, в
// отличие от RelayDiagnostics (только Debug, реальные LookupID и лог).
// Только счётчики: ни LookupID, ни размер payload, ни время конкретной
// записи наружу не попадают, поэтому статистику безопасно показывать публично.
public sealed class RelayStats
{
    // Activity history for the last 60 seconds is kept on the server (not
    // only "since the page was opened" on the client), so the live strip on /
    // survives a page reload. Buckets are addressed by ABSOLUTE time
    // (Unix seconds / 5), and stale ones are cleared lazily on access.
    //
    // История активности за последние 60 секунд хранится на сервере (а не
    // только "с момента открытия страницы" у клиента), поэтому бегущая
    // строка на / переживает перезагрузку страницы. Бакеты адресуются
    // АБСОЛЮТНЫМ временем (Unix-секунды / 5), устаревшие обнуляются лениво.
    private const int BucketSeconds = 5;
    private const int BucketCount = 12; // 12 × 5 s = 60 s, same as RelayStore.Ttl / то же окно, что RelayStore.Ttl

    private readonly object _bucketsLock = new();
    private readonly long[] _buckets = new long[BucketCount];
    private long _currentBucketIndex = -1;

    private long _totalPut;
    private long _totalClaimed;

    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;

    public void RecordPut()
    {
        Interlocked.Increment(ref _totalPut);
        RecordActivity();
    }

    public void RecordClaimed()
    {
        Interlocked.Increment(ref _totalClaimed);
        RecordActivity();
    }

    public long TotalPut => Interlocked.Read(ref _totalPut);

    public long TotalClaimed => Interlocked.Read(ref _totalClaimed);

    // Returns exactly BucketCount numbers, oldest to newest: events (PUT+GET)
    // in each 5-second slice of the last minute. bucketIndex/bucketElapsedMs
    // are the absolute number of the current (still open) bucket and the ms
    // already elapsed in it — the page uses them to detect a bucket change and
    // align its animation to the SERVER's bucket boundaries, not its own clock.
    //
    // Возвращает ровно BucketCount чисел от старых к новым: события (PUT+GET)
    // в каждом 5-секундном отрезке последней минуты. bucketIndex/
    // bucketElapsedMs — абсолютный номер текущего (ещё открытого) бакета и
    // сколько мс в нём прошло: по ним страница видит смену бакета и
    // выравнивает анимацию по границам бакетов СЕРВЕРА, а не по своим часам.
    public long[] GetRecentBuckets(out long bucketIndex, out int bucketElapsedMs)
    {
        lock (_bucketsLock)
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var nowBucket = nowMs / 1000 / BucketSeconds;
            bucketIndex = nowBucket;
            bucketElapsedMs = (int)(nowMs - nowBucket * BucketSeconds * 1000);
            AdvanceTo(nowBucket);

            var result = new long[BucketCount];
            for (var i = 0; i < BucketCount; i++)
            {
                var age = BucketCount - 1 - i;
                var idx = ((nowBucket - age) % BucketCount + BucketCount) % BucketCount;
                result[i] = _buckets[idx];
            }
            return result;
        }
    }

    private void RecordActivity()
    {
        lock (_bucketsLock)
        {
            var nowBucket = CurrentBucketIndex();
            AdvanceTo(nowBucket);
            _buckets[nowBucket % BucketCount]++;
        }
    }

    private static long CurrentBucketIndex() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() / BucketSeconds;

    // Zeroes every bucket time has rolled over since the last access —
    // otherwise a bucket used a minute ago would show its old value instead
    // of 0 on the next lap of the ring.
    //
    // Обнуляет все бакеты, через которые время "перекатилось" с последнего
    // обращения, — иначе бакет, использованный минуту назад, на следующем
    // круге кольца показал бы старое значение вместо 0.
    private void AdvanceTo(long nowBucket)
    {
        if (_currentBucketIndex < 0)
        {
            _currentBucketIndex = nowBucket;
            return;
        }

        var gap = nowBucket - _currentBucketIndex;
        if (gap <= 0) return;

        var toClear = Math.Min(gap, BucketCount);
        for (var i = 1; i <= toClear; i++)
        {
            _buckets[(_currentBucketIndex + i) % BucketCount] = 0;
        }
        _currentBucketIndex = nowBucket;
    }
}
