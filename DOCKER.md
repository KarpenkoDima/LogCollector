# LogCollector: Docker без магии

Этот комплект соответствует **фактической конфигурации текущего проекта**:

- `SyslogListener` — UDP listener;
- `BatchWriter` — размер и таймаут batch;
- `LogSinks` — массив sink-конфигураций;
- SQLite обязан быть ровно одним primary sink;
- Loki является необязательным best-effort secondary sink.

## Файлы и сценарии

```text
Dockerfile                    production + development stages
docker-compose.yml            production: LogCollector + SQLite
docker-compose.dev.yml        самостоятельный dev: dotnet watch
docker-compose.monitoring.yml override: Loki + Grafana
.env.example                  порты, images, limits, volume names
deploy/loki/                  Loki single-node configuration
deploy/grafana/               datasource и dashboard provisioning
secrets/                      только пример; реальный пароль не коммитится
```

Главное правило: monitoring-файл расширяет **тот же** сервис `logcollector`. Второго UDP listener и второго SQLite writer нет.

---

## 1. Почему переменные называются именно так

Текущий код читает:

```csharp
configuration.GetSection("SyslogListener")
configuration.GetSection("BatchWriter")
configuration.GetSection("LogSinks")
```

Поэтому Docker использует:

```dotenv
SyslogListener__Port=5140
BatchWriter__BatchSize=500
LogSinks__0__Type=Sqlite
LogSinks__0__ConnectionString=Data Source=/app/data/logs.db
```

Двойное подчёркивание заменяет `:` в .NET Configuration. Индекс `0` означает первый элемент массива `LogSinks`.

Для monitoring environment provider добавляет второй элемент:

```dotenv
LogSinks__1__Type=Loki
LogSinks__1__Endpoint=http://loki:3100
LogSinks__1__Labels__app=logcollector
```

SQLite остаётся primary под индексом `0`; Loki становится secondary под индексом `1`.

---

## 2. Подготовка

```bash
cp .env.example .env
```

На Linux заполните UID/GID для development-контейнера:

```bash
sed -i "s/^LOCAL_UID=.*/LOCAL_UID=$(id -u)/" .env
sed -i "s/^LOCAL_GID=.*/LOCAL_GID=$(id -g)/" .env
```

Проверка production merge:

```bash
docker compose -f docker-compose.yml config
```

Для проверки monitoring сначала создайте secret-файл, потому что Compose должен разрешить путь из секции `secrets`:

```bash
mkdir -p secrets
printf '%s\n' 'temporary-config-check-password' > secrets/grafana_admin_password.txt
chmod 600 secrets/grafana_admin_password.txt

docker compose \
  -f docker-compose.yml \
  -f docker-compose.monitoring.yml \
  config
```

Перед реальным запуском замените временное значение случайным паролем. В monitoring-результате должен присутствовать **один** `logcollector`; environment сервиса должен содержать SQLite index `0` и Loki index `1`.

---

## 3. Production: SQLite

```bash
docker compose build --pull logcollector
docker compose up -d
docker compose ps
docker compose logs -f --tail=100 logcollector
```

Схема портов:

```text
MikroTik -> host:514/udp -> Docker NAT -> container:5140/udp
```

Процесс внутри контейнера открывает порт выше `1024`, поэтому ему не нужны root и `CAP_NET_BIND_SERVICE`.

Тестовый datagram:

```bash
printf '%s\n' '<30>Jun  4 18:00:00 mtk-router : firewall,info docker test' \
  | nc -u -w1 127.0.0.1 514
```

### Rootless Docker

Rootless Docker может запрещать публикацию host-портов ниже `1024`. Тогда в `.env`:

```dotenv
SYSLOG_HOST_PORT=5140
```

И MikroTik должен отправлять на `5140/udp`, либо host firewall должен перенаправлять `514 -> 5140`.

---

## 4. Development: dotnet watch

Dev Compose самостоятельный. Он специально не наследует production `read_only`, named volume и host port `514`.

```bash
mkdir -p data

docker compose \
  -f docker-compose.dev.yml \
  up --build
```

Исходники подключаются в `/src`. `dotnet watch` перезапускает приложение при изменении `.cs`.

```text
host 127.0.0.1:5140/udp -> container 5140/udp
host ./data              -> container /app/data
```

Тест:

```bash
printf '%s\n' '<30>Jun  4 18:00:00 dev-router : system,info dev test' \
  | nc -u -w1 127.0.0.1 5140
```

База появится в `./data/dev-logs.db`.

Остановка:

```bash
docker compose -f docker-compose.dev.yml down
```

---

## 5. Monitoring: SQLite + Loki + Grafana

Создайте реальный secret:

```bash
mkdir -p secrets
openssl rand -base64 36 > secrets/grafana_admin_password.txt
chmod 600 secrets/grafana_admin_password.txt
```

Запуск:

```bash
docker compose \
  -f docker-compose.yml \
  -f docker-compose.monitoring.yml \
  up -d --build
```

Grafana:

```text
http://127.0.0.1:3000
```

Логин по умолчанию: `admin`. Пароль находится в `secrets/grafana_admin_password.txt`.

Loki не публикует `3100` на host. Он работает с `auth_enabled: false`, поэтому доступен только во внутренней Docker network.

Проверка:

```bash
docker compose \
  -f docker-compose.yml \
  -f docker-compose.monitoring.yml \
  ps

docker compose \
  -f docker-compose.yml \
  -f docker-compose.monitoring.yml \
  logs -f logcollector loki grafana
```

Datasource `Loki` и dashboard `MikroTik Syslog` создаются автоматически.

---

## 6. Что защищено

### LogCollector

- официальный non-root пользователь `app`;
- внутри только `5140/udp`;
- `cap_drop: ALL`;
- `no-new-privileges`;
- read-only root filesystem;
- writable только `/app/data` и `/tmp`;
- ограничение PID, CPU, memory и Docker logs;
- 30 секунд на graceful shutdown.

### Loki

- UID/GID `10001`;
- порт не публикуется;
- root filesystem read-only;
- chunks, index, compactor и WAL находятся в persistent volume;
- root используется только одноразовым init-контейнером для `chown` volume.

### Grafana

- UID `472`;
- пароль читается из secret-файла;
- anonymous access и регистрацию пользователей отключены;
- UI по умолчанию слушает только loopback host;
- datasource и dashboard provisioned из read-only файлов.

---

## 7. Почему у collector нет healthcheck

Проверка `pgrep dotnet` повторяет то, что Docker уже знает: жив ли PID 1. Она не подтверждает:

1. успешный bind UDP socket;
2. открытие SQLite и создание schema;
3. работоспособность background writer;
4. отсутствие fatal error в очереди.

Поэтому ложной зелёной галочки нет. Настоящий readiness потребует небольшой поддержки приложения: HTTP endpoint либо health-файл, выставляемый после инициализации listener и storage.

---

## 8. Данные и backup SQLite

Обычный `docker compose down` named volume не удаляет.

```bash
docker volume ls | grep logcollector
```

SQLite работает в WAL mode. Во время активной записи нельзя считать backup простым копированием только `logs.db`: актуальные данные могут находиться в `logs.db-wal`.

Простой cold backup:

```bash
docker compose stop logcollector
# скопировать volume целиком
docker compose start logcollector
```

Полное удаление контейнеров и данных:

```bash
docker compose down --volumes
```

Это необратимая команда.

---

## 9. Ограничения

- UDP не гарантирует доставку; при переполнении socket buffer datagram теряются.
- SQLite рассчитана на один writer и небольшой внутренний deployment.
- Loki здесь single-node + filesystem storage, без HA.
- Loki secondary best-effort: при его недоступности данные сохраняются в SQLite, но автоматически не переигрываются в Loki после восстановления.
- Resource limits являются стартовыми guardrails, а не доказанной оптимальной конфигурацией.
