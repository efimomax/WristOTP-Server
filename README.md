# WristOTP Relay

[English](#english) · [Русский](#русский)

## English

The server part of **WristOTP**, a TOTP authenticator for Huawei watches
(HarmonyOS). It is a tiny, purely **ephemeral relay** that hands an encrypted
account bundle from the phone app to the watch app.

- An entry lives in memory for **at most one minute** or until it is read
  once — whichever comes first. Nothing is written to disk; there is no
  database, no user accounts, no backups.
- The server **never sees plaintext**. The phone encrypts the bundle with
  AES-256-GCM; the key and the entry ID (`LookupID`) are both derived with
  HKDF-SHA256 from the watch's device ID and a 6-digit code the user types
  into the watch. The server only stores an opaque blob under an opaque ID.

### API

| Method | Path | Description |
|---|---|---|
| `PUT` | `/api/v1/tokens/{id}` | Store `{ iv, authTag, ciphertext }` (Base64) for 1 minute. Bodies over 10 KB → `413`, invalid JSON → `400`. |
| `GET` | `/api/v1/tokens/{id}` | Return the entry and **delete it immediately** (one-time read; a repeat → `404`). Rate limit: 5 requests/min/IP (sliding window) → `429`. |
| `GET` | `/` | Public landing page with live, anonymous counters. |
| `GET` | `/api/v1/stats` | Aggregated counters only — no IDs, no payloads. |
| `GET` | `/api/v1/config` | Page settings (link to the source repository). |
| `GET` | `/debug` | Diagnostics page — **Debug build only**; the code is not compiled into Release at all. Shows payload sizes, never contents. |

### Run locally

```
cd WristOTP.Server
dotnet run
```

Requires the .NET 10 SDK. The port is **8443**, set in `Program.cs`: the Debug
build listens on all interfaces (to test with real devices on your LAN), the
Release build on `127.0.0.1` only (put a TLS reverse proxy in front).

```
curl -X PUT http://localhost:8443/api/v1/tokens/abc123 \
  -H "Content-Type: application/json" \
  -d '{"iv":"aXY=","authTag":"dGFn","ciphertext":"Y2lwaGVy"}'

curl http://localhost:8443/api/v1/tokens/abc123
```

### Run your own relay

See [deploy/README.md](deploy/README.md): systemd unit, nginx example and a
one-command deploy script. Then set your relay's address in the watch and
phone apps.

### Configuration

`appsettings.json` → `Links:GithubRepoUrl` — the repository link shown on the
landing page (leave empty to hide it).

## Русский

Серверная часть **WristOTP** — TOTP-аутентификатора для часов Huawei
(HarmonyOS). Это маленький, полностью **эфемерный релей**: он передаёт
зашифрованный пакет аккаунтов из приложения на телефоне в приложение на часах.

- Запись живёт в памяти **не дольше минуты** или до первого чтения — что
  наступит раньше. На диск ничего не пишется; нет базы данных, учётных
  записей пользователей и бэкапов.
- Сервер **никогда не видит открытых данных**. Телефон шифрует пакет
  AES-256-GCM; и ключ, и идентификатор записи (`LookupID`) выводятся через
  HKDF-SHA256 из идентификатора часов и 6-значного кода, который
  пользователь вводит на часах. Сервер хранит лишь непрозрачный блоб под
  непрозрачным ID.

### API

| Метод | Путь | Описание |
|---|---|---|
| `PUT` | `/api/v1/tokens/{id}` | Сохранить `{ iv, authTag, ciphertext }` (Base64) на 1 минуту. Тело больше 10 КБ → `413`, некорректный JSON → `400`. |
| `GET` | `/api/v1/tokens/{id}` | Отдать запись и **сразу удалить** (одноразовое чтение; повтор → `404`). Лимит: 5 запросов/мин/IP (скользящее окно) → `429`. |
| `GET` | `/` | Публичная стартовая страница с живыми анонимными счётчиками. |
| `GET` | `/api/v1/stats` | Только агрегированные счётчики — без ID и содержимого. |
| `GET` | `/api/v1/config` | Настройки страницы (ссылка на репозиторий). |
| `GET` | `/debug` | Диагностика — **только Debug-сборка**; в Release этот код не компилируется вовсе. Показывает размеры пакетов, но не содержимое. |

### Запуск локально

```
cd WristOTP.Server
dotnet run
```

Нужен .NET 10 SDK. Порт **8443** задан в `Program.cs`: Debug-сборка слушает
все интерфейсы (чтобы тестировать с реальных устройств в локальной сети),
Release — только `127.0.0.1` (перед ним ставится reverse proxy с TLS).

### Свой релей

См. [deploy/README.md](deploy/README.md): unit systemd, пример nginx и скрипт
деплоя одной командой. Затем укажите адрес своего релея в приложениях на
часах и телефоне.

### Настройки

`appsettings.json` → `Links:GithubRepoUrl` — ссылка на репозиторий на
стартовой странице (пустое значение скрывает её).
