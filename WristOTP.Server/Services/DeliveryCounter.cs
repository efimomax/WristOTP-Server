namespace WristOTP.Server.Services;

// All-time number of successful deliveries to watches (successful one-time
// GETs), shown on the landing page. The ONLY thing the relay writes to disk:
// a single anonymous number in a text file — no IDs, times or IP addresses,
// nothing that relates to a user or a device. Survives restarts and deploys.
//
// File location: config "Stats:DeliveredCounterFile"; otherwise systemd's
// $STATE_DIRECTORY (StateDirectory= in the unit); otherwise App_Data/ in the
// content root (local runs). A broken or unwritable file never affects the
// relay itself — the error is logged and the counter keeps working in memory.
//
// Общее число успешных доставок на часы за всё время (успешные одноразовые
// GET) для стартовой страницы. ЕДИНСТВЕННОЕ, что релей пишет на диск: одно
// анонимное число в текстовом файле — без ID, времени и IP-адресов, ничего,
// что относится к пользователю или устройству. Переживает перезапуски и деплой.
//
// Расположение файла: настройка "Stats:DeliveredCounterFile"; иначе
// $STATE_DIRECTORY от systemd (StateDirectory= в unit); иначе App_Data/ в
// корне контента (локальный запуск). Повреждённый или недоступный для записи
// файл никак не влияет на сам релей — ошибка пишется в лог, счётчик
// продолжает работать в памяти.
public sealed class DeliveryCounter
{
    private const string FileName = "delivered-total.txt";

    private readonly string _path;
    private readonly ILogger<DeliveryCounter> _logger;
    private readonly object _lock = new();
    private long _value;

    public DeliveryCounter(IConfiguration configuration, IHostEnvironment environment, ILogger<DeliveryCounter> logger)
    {
        _logger = logger;
        _path = ResolvePath(configuration, environment);

        try
        {
            if (File.Exists(_path)
                && long.TryParse(File.ReadAllText(_path).Trim(), out var stored)
                && stored >= 0)
            {
                _value = stored;
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not read delivery counter from {Path}", _path);
        }
    }

    public long Value => Interlocked.Read(ref _value);

    public void Increment()
    {
        lock (_lock)
        {
            _value++;
            Save(_value);
        }
    }

    // Write to a temporary file and move it over the old one, so a crash in
    // the middle never leaves a half-written number.
    // Пишем во временный файл и переносим поверх старого, чтобы сбой
    // посередине не оставил недописанное число.
    private void Save(long value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not save delivery counter to {Path}", _path);
        }
    }

    private static string ResolvePath(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration["Stats:DeliveredCounterFile"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        // systemd may pass several directories separated by ':'.
        // systemd может передать несколько каталогов через ':'.
        var stateDirectory = Environment.GetEnvironmentVariable("STATE_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(stateDirectory))
        {
            return Path.Combine(stateDirectory.Split(':')[0], FileName);
        }

        return Path.Combine(environment.ContentRootPath, "App_Data", FileName);
    }
}
