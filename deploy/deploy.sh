#!/usr/bin/env bash
# One-command deployment of WristOTP.Server to a Linux server over SSH:
#
#   bash deploy/deploy.sh
#
# Builds a self-contained linux-x64 Release (no .NET needed on the server),
# uploads it into a new /opt/wristotp-server/releases/<timestamp>, switches
# the `current` symlink, restarts the systemd service and health-checks
# /api/v1/stats. If the check fails, it rolls back to the previous release.
# The last 3 releases are kept. Settings: deploy/deploy.env (see
# deploy.env.example). One-time server setup: deploy/README.md.
#
# Деплой WristOTP.Server на Linux-сервер по SSH одной командой:
#
#   bash deploy/deploy.sh
#
# Собирает self-contained Release под linux-x64 (.NET на сервере не нужен),
# заливает в новый /opt/wristotp-server/releases/<timestamp>, переключает
# симлинк `current`, перезапускает systemd-сервис и проверяет
# /api/v1/stats. Если проверка не прошла — откатывается на предыдущий релиз.
# Хранятся 3 последних релиза. Настройки — deploy/deploy.env (образец —
# deploy.env.example). Первичная настройка сервера — deploy/README.md.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ENV_FILE="$REPO_ROOT/deploy/deploy.env"
if [ -f "$ENV_FILE" ]; then
  # shellcheck source=/dev/null
  source "$ENV_FILE"
fi
HOST="${DEPLOY_HOST:?Set DEPLOY_HOST in deploy/deploy.env (see deploy.env.example)}"
PUBLIC_URL="${PUBLIC_URL:-}"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

echo "==> publish (Release, linux-x64, self-contained)"
dotnet publish "$REPO_ROOT/WristOTP.Server/WristOTP.Server.csproj" \
  -c Release -r linux-x64 --self-contained true \
  -o "$WORK/publish" -nologo -v q

# Commit hash of the deployed build; "-dirty" if WristOTP.Server/ has
# uncommitted changes. / Хэш коммита сборки; "-dirty" при незакоммиченных правках.
COMMIT="$(git -C "$REPO_ROOT" rev-parse --short HEAD)"
if [ -n "$(git -C "$REPO_ROOT" status --porcelain --untracked-files=no -- WristOTP.Server)" ]; then
  COMMIT="$COMMIT-dirty"
fi
echo "$COMMIT" > "$WORK/publish/DEPLOYED_COMMIT"

echo "==> pack + upload ($COMMIT)"
tar -czf "$WORK/wristotp-server.tar.gz" -C "$WORK/publish" .
scp -q "$WORK/wristotp-server.tar.gz" "$HOST:/tmp/wristotp-server.tar.gz"

echo "==> install + restart"
ssh "$HOST" 'bash -s' <<'REMOTE'
set -euo pipefail
BASE=/opt/wristotp-server
NEW="$BASE/releases/$(date +%Y%m%d-%H%M%S)"
PREV="$(readlink -f "$BASE/current" 2>/dev/null || true)"

sudo mkdir -p "$NEW"
sudo tar -xzf /tmp/wristotp-server.tar.gz -C "$NEW"
rm -f /tmp/wristotp-server.tar.gz
sudo chmod +x "$NEW/WristOTP.Server"
sudo chown -R root:root "$NEW"
sudo ln -sfn "$NEW" "$BASE/current"
sudo systemctl restart wristotp-server

ok=0
for _ in $(seq 1 20); do
  if curl -fs -o /dev/null http://127.0.0.1:8443/api/v1/stats; then ok=1; break; fi
  sleep 1
done

if [ "$ok" != 1 ]; then
  echo "!! health check failed — rolling back to $PREV / проверка не прошла — откат" >&2
  sudo journalctl -u wristotp-server -n 30 --no-pager >&2 || true
  if [ -n "$PREV" ] && [ -d "$PREV" ]; then
    sudo ln -sfn "$PREV" "$BASE/current"
    sudo systemctl restart wristotp-server
  fi
  exit 1
fi

# Keep the last 3 releases / Оставляем 3 последних релиза
ls -1dt "$BASE"/releases/* | tail -n +4 | xargs -r sudo rm -rf
echo "release: $NEW ($(cat "$NEW/DEPLOYED_COMMIT"))"
REMOTE

if [ -n "$PUBLIC_URL" ]; then
  echo "==> public check"
  # Not fatal: the service is already verified on the server itself; from
  # here a stale local DNS cache may get in the way.
  # Не фатально: сервис уже проверен на самом сервере, а отсюда может
  # мешать устаревший локальный DNS-кэш.
  if curl -fsS --max-time 10 "$PUBLIC_URL/api/v1/stats"; then
    echo; echo "OK: $PUBLIC_URL"
  else
    echo "?? $PUBLIC_URL did not respond from here — the service is up on the server, check DNS/proxy" >&2
  fi
fi
