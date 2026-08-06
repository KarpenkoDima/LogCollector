# Что исправлено в Docker

## 1. Dockerfile приведён к реальной структуре архива

Удалены ссылки на отсутствующие `Directory.Build.props` и `global.json`. Restore теперь копирует существующие `.sln` и `.csproj`.

## 2. Настройки согласованы с кодом

Compose использует секции, которые действительно читает `ServiceCollectionExtensions`:

- `SyslogListener`;
- `BatchWriter`;
- массив `LogSinks`.

Удалены неверные ключи `UdpListener__...`, `Sqlite__...` и `Loki__Enabled`.

## 3. Production больше не включает Loki автоматически

Из базового `appsettings.json` удалён Loki sink. Обычный production запускает только обязательный SQLite primary и не обращается к `localhost:3100`.

## 4. Monitoring добавляет второй элемент LogSinks

`docker-compose.monitoring.yml` добавляет:

```text
LogSinks__1__Type=Loki
LogSinks__1__Endpoint=http://loki:3100
```

Работает один collector; отдельного `logcollector-monitoring` нет.

## 5. Development сделан самостоятельным

`docker-compose.dev.yml` больше не зависит от merge semantics production-файла и не требует YAML-тега `!override`. Он запускает `dotnet watch`, публикует только loopback `5140/udp` и пишет базу в `./data`.

## 6. Production hardening

Добавлены non-root runtime, `read_only`, `cap_drop: ALL`, `no-new-privileges`, tmpfs для `/tmp`, PID/CPU/memory limits, ротация Docker logs и graceful shutdown.

## 7. Monitoring provisioning завершён

Добавлены:

- Loki single-node config с persistent WAL;
- Grafana Loki datasource с фиксированным UID `loki`;
- dashboard без неразрешённой переменной `${DS_LOKI}`;
- Grafana secret вместо пароля в environment;
- loopback-only Grafana по умолчанию.

## 8. Убран фиктивный collector healthcheck

Проверка процесса не доказывает готовность UDP listener, SQLite и writer. Ограничение явно задокументировано.

## 9. Build context очищен

`.dockerignore` исключает `.git`, `bin/obj`, тестовые и benchmark-артефакты, базы, архивы, `.env` и реальные secrets.
