# Changelog

Все значимые изменения проекта документируются в этом файле.

Формат основан на [Keep a Changelog](https://keepachangelog.com/ru/1.1.0/),
проект придерживается [семантического версионирования](https://semver.org/lang/ru/).

## [Unreleased]

### Планируется
- Независимые воркеры для каждого sink (изоляция отказа Loki от SQLite)
- Стратегия повторных отправок при недоступности Loki (локальный WAL-буфер)
- `SocketAsyncEventArgs` для приёма при нагрузке >100k pps

## [2.1.0] - 2025-12-22

### Добавлено
- Persistent SQLite connection: соединение открывается один раз в `InitializeAsync`
  и живёт всё время работы синка вместо открытия на каждый батч
- `busy_timeout=5000` для SQLite — предотвращает `SQLITE_BUSY` при чтении из Grafana
- Переиспользование `SocketAddress` в `UdpSyslogListener` через перегрузку
  `ReceiveFromAsync(Memory, SocketFlags, SocketAddress, CancellationToken)`
- Grafana dashboard зафиксирован в `deploy/grafana/dashboard.json` и монтируется
  в контейнер — переживает удаление grafana-data volume
- RUNBOOK.md — операционное руководство по запуску и диагностике

### Изменено
- `ReceivedAt` в схеме SQLite: `TEXT` → `INTEGER` (Unix milliseconds).
  Индекс теперь работает для диапазонных запросов `BETWEEN $__from AND $__to`.
  Устранено полное сканирование таблицы при каждом запросе из Grafana

### Исправлено
- Инвертированное guard-условие в `LokiLogSink.SaveBatchAsync`, из-за которого
  метод выходил на каждом непустом батче и данные не попадали в Loki
- Отсутствующий `streams.Add()` в цикле группировки — Loki получал пустой массив
- Лейбл Loki `APP` → `app` (регистр env-переменной на Linux ломал dashboard)
- Datasource UID зафиксирован (`loki-logcollector`) — устранена ошибка
  «Templating Failed to upgrade legacy queries»

### Удалено
- Мёртвый класс `SqliteLogRepository` — дубликат схемы из старой архитектуры.
  `SqliteLogSink` — единственный владелец схемы и пути записи

## [2.0.0] - 2025-12-19

Полное переписывание архитектуры. Несовместимо с 1.x.

### Добавлено
- Clean Architecture: пять проектов (Core, Application, Infrastructure, Host, Tests)
  со строгим направлением зависимостей
- Zero-allocation `SyslogParser` на `ReadOnlySpan<byte>` — без Regex, без `string.Split`
- Двойной формат MikroTik: `bsd-syslog=no` (extended) и `bsd-syslog=yes` (RFC 3164)
  обрабатываются в одном проходе
- Двухбуферный `UdpSyslogListener`: pinned buffer для DMA + `MemoryPool<byte>` на дейтаграмму
- `BatchWriterService` с паттерном Клири (`WaitToReadAsync` + `TryRead` batch-drain)
- FanOut-архитектура синков: `ILogSink` + `ISinkFactory` + `FanOutLogRepository`
- `LokiLogSink` с manual partition по hostname (Gen2=0, в 4.9× быстрее GroupBy)
- Docker Compose с профилями `sqlite` и `monitoring` (Loki + Grafana)
- Multi-stage Dockerfile с non-root пользователем
- BenchmarkDotNet проект с воспроизводимыми замерами
- 23 теста: unit + integration + zero-copy архитектурное доказательство

### Изменено
- `LogEntry`: `class` → `readonly struct` (правило Рихтера — нет Gen0-давления)
- Поля `LogEntry`: `string` → `ReadOnlyMemory<byte>` (строки только на границе БД)
- Хранение времени: `DateTimeOffset` string → готовность к INTEGER

### Удалено
- Плоская структура `src/` и `test/` из версии 1.x
- `TcpLogListener` и `JsonLogParser` — не использовались

## [1.0.0] - 2025-05-25

Первая работающая версия.

### Добавлено
- UDP-приём syslog от MikroTik
- Парсинг через `string.Split`
- Запись в SQLite через `BatchWriteService`
- `LogChannel` на `System.Threading.Channels`
- `IOptions` для конфигурации интервала и размера батча
- Первые unit-тесты с `TaskCompletionSource`

### Известные проблемы (устранены в 2.0)
- `LogEntry` как class — Gen0-давление под нагрузкой
- Парсинг через `string.Split` — множественные аллокации на дейтаграмму
- Плоская структура без разделения слоёв

[Unreleased]: https://github.com/KarpenkoDima/LogCollector/compare/v2.1.0...HEAD
[2.1.0]: https://github.com/KarpenkoDima/LogCollector/compare/v2.0.0...v2.1.0
[2.0.0]: https://github.com/KarpenkoDima/LogCollector/compare/v1.0...v2.0.0
[1.0.0]: https://github.com/KarpenkoDima/LogCollector/releases/tag/v1.0
