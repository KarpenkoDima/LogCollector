# Changelog

Все значимые изменения проекта документируются в этом файле.

Формат основан на [Keep a Changelog](https://keepachangelog.com/ru/1.1.0/),
проект придерживается [семантического версионирования](https://semver.org/lang/ru/).

## [Unreleased]

### Добавлено

- Три согласованных Docker-сценария:
  - `docker-compose.yml` — production collector с обязательным SQLite primary;
  - `docker-compose.dev.yml` — SDK-контейнер с `dotnet watch`;
  - `docker-compose.monitoring.yml` — Loki и Grafana поверх того же collector.
- `.env.example` с параметрами портов, ресурсов и Grafana.
- Loki single-node configuration с persistent storage.
- Автоматический provisioning Loki datasource и dashboard в Grafana.
- Пример файлового секрета `secrets/grafana_admin_password.txt.example`.
- `.gitattributes` с LF для исходников и конфигурации и CRLF для Windows-скриптов.
- Отдельные руководства `DOCKER.md` и `DOCKER-CHANGES.md`.

### Изменено

- Dockerfile приведён к фактической структуре solution и разделён на
  `build`, `development`, `publish` и chiseled `runtime` stages.
- Compose использует реальные секции конфигурации приложения:
  `SyslogListener`, `BatchWriter` и `LogSinks`.
- Production-конфигурация больше не включает Loki автоматически:
  SQLite остаётся обязательным primary, Loki подключается monitoring override-файлом.
- `README.md` и `DESIGN_GUIDE.md` синхронизированы с фактическим конвейером:
  `UdpSyslogListener → CompositeLogParser → OwnedIngress → BatchWriterService →
  FanOutLogRepository`.
- systemd unit хранит SQLite в управляемом каталоге
  `/var/lib/logcollector/logs.db`, использует настоящие ключи `LogSinks__0__*`
  и даёт 45 секунд на graceful shutdown.
- Production containers запускаются non-root с read-only root filesystem,
  сброшенными capabilities, ограничениями ресурсов и ротацией Docker logs.

### Исправлено

- Dockerfile больше не ссылается на отсутствующие `Directory.Build.props`
  и `global.json`.
- Исправлено несоответствие Docker environment variables текущим Options-классам.
- Исправлен systemd-путь SQLite, ранее конфликтовавший с `ProtectSystem=strict`.
- Удалён фиктивный healthcheck collector, который проверял бы только наличие процесса,
  но не готовность UDP listener, SQLite и batch writer.

### Удалено

- Устаревшие Grafana-файлы `deploy/grafana/dashboard-provider.yaml`
  и `deploy/grafana/dashboard.json`; используется единая структура
  `deploy/grafana/provisioning` и `deploy/grafana/dashboards`.
- Временные файлы применения Docker-патча `APPLY.md` и `.delete-files.txt`.

## [2.2.0] - 2026-08-03

Production-readiness релиз по результатам аудита конвейера, ownership,
изоляции хранилищ, HTTP lifecycle, конфигурации и парсера.

### Добавлено

- `IOwnedIngress` / `OwnedIngress` — owning-очередь с явной политикой
  drop-oldest и освобождением `RawBuffer` вытесненной записи (P0.1).
- Windowed batching в `BatchWriterService`: batch закрывается по первому из
  `BatchSize`, `BatchTimeout`, completion или shutdown (P0.2).
- Regression-тесты, доказывающие сохранение хвоста при graceful shutdown,
  отсутствие outstanding owners и double-dispose (P0.3).
- `DroppedCount` в ingress как основа будущей метрики потерь.
- Primary/secondary модель в `FanOutLogRepository`:
  SQLite — обязательный primary, Loki и Console — best-effort secondaries (P1.1).
- `ILogSink.Name` для диагностического логирования secondary sinks.
- HTTP lifetime discipline для `LokiLogSink` (P1.2):
  - один `HttpClient` на lifetime sink;
  - обязательный `Dispose` каждого `HttpResponseMessage`;
  - timeout отдельной попытки 2 секунды;
  - не более двух повторов transient-ошибок;
  - отсутствие retry для HTTP 4xx;
  - корректное распространение cancellation вызывающей стороны.
- Startup validation через `ValidateOnStart` (P1.3):
  - диапазоны порта и размера UDP datagram;
  - диапазоны размера и таймаута batch;
  - наличие ровно одного SQLite primary в `LogSinks`.
- Parser hardening для повреждённых datagram (P1.4):
  - PRI должен целиком состоять из цифр и находиться в диапазоне `[0, 191]`;
  - проверяются границы cursor, длина timestamp и разделители;
  - пустой hostname отклоняется;
  - обрезанные и случайные байты не приводят к исключению;
  - permissive-поведение для неизвестных MikroTik topic/severity сохранено.

### Исправлено

- P0.1: `Channel` с автоматическим `DropOldest` вытеснял записи без вызова
  `RawBuffer.Dispose()`, что приводило к утечке pool-буферов при перегрузке.
- P0.2: snapshot-drain формировал batch размером около одной записи на трафике
  с небольшими паузами и создавал чрезмерное число SQLite-транзакций.
- P0.3: graceful shutdown теперь доказан тестами для хвоста очереди и ownership.
- P1.1: зависший или упавший Loki больше не отменяет успешную запись batch в SQLite
  и не маскируется как потеря основного хранилища.
- P1.2: устранены утечки HTTP response, неограниченное ожидание и смешивание
  shutdown cancellation с временной недоступностью Loki.
- P1.3: некорректные настройки теперь останавливают host до начала приёма трафика
  с понятным сообщением об ошибке.
- P1.4: парсер больше не принимает частично разобранный PRI и не выходит за границы
  буфера на обрезанных сообщениях.

## [2.1.0] - 2025-12-22

### Добавлено

- Persistent SQLite connection: соединение открывается один раз в `InitializeAsync`
  и живёт всё время работы sink вместо открытия на каждый batch.
- `busy_timeout=5000` для SQLite — предотвращает `SQLITE_BUSY` при чтении из Grafana.
- Переиспользование `SocketAddress` в `UdpSyslogListener` через перегрузку
  `ReceiveFromAsync(Memory, SocketFlags, SocketAddress, CancellationToken)`.
- Grafana dashboard зафиксирован в `deploy/grafana/dashboard.json` и монтируется
  в контейнер — переживает удаление `grafana-data` volume.
- `RUNBOOK.md` — операционное руководство по запуску и диагностике.

### Изменено

- `ReceivedAt` в схеме SQLite: `TEXT` → `INTEGER` (Unix milliseconds).
  Индекс теперь работает для диапазонных запросов `BETWEEN $__from AND $__to`.
  Устранено полное сканирование таблицы при каждом запросе из Grafana.

### Исправлено

- Инвертированное guard-условие в `LokiLogSink.SaveBatchAsync`, из-за которого
  метод выходил на каждом непустом batch и данные не попадали в Loki.
- Отсутствующий `streams.Add()` в цикле группировки — Loki получал пустой массив.
- Label Loki `APP` → `app` — регистр environment variable на Linux ломал dashboard.
- Datasource UID зафиксирован (`loki-logcollector`) — устранена ошибка
  «Templating Failed to upgrade legacy queries».

### Удалено

- Мёртвый класс `SqliteLogRepository` — дубликат схемы из старой архитектуры.
  `SqliteLogSink` — единственный владелец схемы и пути записи.

## [2.0.0] - 2025-12-19

Полное переписывание архитектуры. Несовместимо с 1.x.

### Добавлено

- Clean Architecture: пять проектов (Core, Application, Infrastructure, Host, Tests)
  со строгим направлением зависимостей.
- Zero-allocation `SyslogParser` на `ReadOnlySpan<byte>` — без Regex и `string.Split`.
- Двойной формат MikroTik: `bsd-syslog=no` (extended) и `bsd-syslog=yes` (RFC 3164)
  обрабатываются в одном проходе.
- Двухбуферный `UdpSyslogListener`: pinned receive buffer для системного приёма
  и `MemoryPool<byte>` для ownership каждой принятой datagram.
- `BatchWriterService` с пакетной записью в SQLite.
- Fan-out архитектура sinks: `ILogSink` + `ISinkFactory` + `FanOutLogRepository`.
- `LokiLogSink` с manual partition по hostname.
- Docker Compose с профилями `sqlite` и `monitoring` (Loki + Grafana).
- Multi-stage Dockerfile с non-root пользователем.
- BenchmarkDotNet проект с воспроизводимыми замерами.
- Unit и integration тесты, включая zero-copy архитектурную проверку.

### Изменено

- `LogEntry`: `class` → `readonly struct`.
- Поля `LogEntry`: `string` → `ReadOnlyMemory<byte>` — строки создаются только
  на границе внешнего хранилища.
- Подготовлено хранение времени как Unix milliseconds.

### Удалено

- Плоская структура `src/` и `test/` из версии 1.x.
- `TcpLogListener` и `JsonLogParser`, не используемые текущей задачей.

## [1.0.0] - 2025-05-25

Первая работающая версия.

### Добавлено

- UDP-приём syslog от MikroTik.
- Парсинг через `string.Split`.
- Запись в SQLite через `BatchWriteService`.
- `LogChannel` на `System.Threading.Channels`.
- `IOptions` для конфигурации интервала и размера batch.
- Первые unit-тесты с `TaskCompletionSource`.

### Известные проблемы, устранённые в 2.0

- `LogEntry` как class — Gen0-давление под нагрузкой.
- Парсинг через `string.Split` — множественные аллокации на datagram.
- Плоская структура без разделения слоёв.

[Unreleased]: https://github.com/KarpenkoDima/LogCollector/compare/v2.2.0...HEAD
[2.2.0]: https://github.com/KarpenkoDima/LogCollector/compare/v2.1.0...v2.2.0
[2.1.0]: https://github.com/KarpenkoDima/LogCollector/compare/v2.0.0...v2.1.0
[2.0.0]: https://github.com/KarpenkoDima/LogCollector/compare/v1.0...v2.0.0
[1.0.0]: https://github.com/KarpenkoDima/LogCollector/releases/tag/v1.0
