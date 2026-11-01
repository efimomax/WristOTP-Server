# Self-hosting the relay / Свой экземпляр релея

[English](#english) · [Русский](#русский)

## English

The relay is a single self-contained binary behind a TLS reverse proxy.
In Release it listens on `127.0.0.1:8443` only, so it is never exposed
directly.

### One-time server setup (Debian/Ubuntu)

1. **A user for deployments** with passwordless `sudo` and SSH key access.
2. **A system user for the service:**
   ```
   sudo useradd --system --no-create-home --shell /usr/sbin/nologin wristotp
   sudo mkdir -p /opt/wristotp-server/releases
   ```
3. **systemd unit:** copy `deploy/wristotp-server.service` to
   `/etc/systemd/system/`, then `sudo systemctl daemon-reload &&
   sudo systemctl enable wristotp-server`.
4. **Reverse proxy with TLS:** start from `deploy/nginx.conf.example`
   (replace `relay.example.com`, get a certificate, e.g. with certbot).
   Keep `X-Forwarded-For $remote_addr` — the app uses the client IP for
   per-IP rate limiting.
5. **Local settings:** copy `deploy/deploy.env.example` to
   `deploy/deploy.env` (git-ignored) and set `DEPLOY_HOST` and `PUBLIC_URL`.

### Deploy / update

```
bash deploy/deploy.sh
```

Builds a self-contained linux-x64 Release (no .NET runtime needed on the
server), uploads it as a new release, switches the `current` symlink,
restarts the service and checks `/api/v1/stats`. On a failed check it rolls
back automatically. The last 3 releases are kept; the deployed commit is in
`/opt/wristotp-server/current/DEPLOYED_COMMIT`.

### Useful commands

```
systemctl status wristotp-server --no-pager
sudo journalctl -u wristotp-server -n 100 --no-pager
cat /opt/wristotp-server/current/DEPLOYED_COMMIT
```

Manual rollback to the previous release:

```
P=$(ls -1dt /opt/wristotp-server/releases/* | sed -n 2p); sudo ln -sfn "$P" /opt/wristotp-server/current && sudo systemctl restart wristotp-server
```

### Pointing the apps at your relay

The watch app has a relay address setting (Settings → Relay server); the
phone app has the same in its Settings.

## Русский

Релей — один self-contained исполняемый файл за reverse proxy с TLS. В
Release он слушает только `127.0.0.1:8443` и напрямую наружу не открыт.

### Первичная настройка сервера (Debian/Ubuntu)

1. **Пользователь для деплоя** с `sudo` без пароля и входом по SSH-ключу.
2. **Системный пользователь для сервиса:**
   ```
   sudo useradd --system --no-create-home --shell /usr/sbin/nologin wristotp
   sudo mkdir -p /opt/wristotp-server/releases
   ```
3. **systemd unit:** скопируйте `deploy/wristotp-server.service` в
   `/etc/systemd/system/`, затем `sudo systemctl daemon-reload &&
   sudo systemctl enable wristotp-server`.
4. **Reverse proxy с TLS:** за основу возьмите `deploy/nginx.conf.example`
   (замените `relay.example.com`, получите сертификат, например certbot).
   Оставьте `X-Forwarded-For $remote_addr` — приложение использует IP
   клиента для лимита запросов.
5. **Локальные настройки:** скопируйте `deploy/deploy.env.example` в
   `deploy/deploy.env` (не попадает в git) и задайте `DEPLOY_HOST` и
   `PUBLIC_URL`.

### Деплой / обновление

```
bash deploy/deploy.sh
```

Собирает self-contained Release под linux-x64 (.NET на сервере не нужен),
заливает новым релизом, переключает симлинк `current`, перезапускает сервис
и проверяет `/api/v1/stats`. Если проверка не прошла — сам откатывается.
Хранятся 3 последних релиза; задеплоенный коммит —
`/opt/wristotp-server/current/DEPLOYED_COMMIT`.

### Полезные команды

```
systemctl status wristotp-server --no-pager
sudo journalctl -u wristotp-server -n 100 --no-pager
cat /opt/wristotp-server/current/DEPLOYED_COMMIT
```

Ручной откат на предыдущий релиз:

```
P=$(ls -1dt /opt/wristotp-server/releases/* | sed -n 2p); sudo ln -sfn "$P" /opt/wristotp-server/current && sudo systemctl restart wristotp-server
```

### Как направить приложения на свой релей

В приложении для часов адрес релея меняется в Settings → Relay server, в
приложении для телефона — в его Settings.
