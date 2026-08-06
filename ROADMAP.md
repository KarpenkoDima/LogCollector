# Roadmap

План развития LogCollector по milestone. Каждый milestone — законченный
инкремент с понятной эксплуатационной целью.

## ✅ Milestone 1 — Работающий конвейер (v1.0)

Первая версия, доказавшая жизнеспособность идеи.

- [x] UDP-приём syslog.
- [x] Запись в SQLite.
- [x] Channel-based конвейер.
- [x] Первые unit-тесты.

## ✅ Milestone 2 — Production-архитектура (v2.0)

Переписывание на Clean Architecture и low-allocation обработку.

- [x] `readonly struct LogEntry`.
- [x] Парсер на `ReadOnlySpan<byte>` без Regex и `string.Split`.
- [x] Ownership datagram через `MemoryPool<byte>`.
- [x] Пакетная запись в SQLite.
- [x] Fan-out архитектура sinks.
- [x] Loki и Grafana.
- [x] BenchmarkDotNet с воспроизводимыми замерами.
- [x] Unit и integration тесты.

## ✅ Milestone 3 — Production-эксплуатация (v2.1)

Исправления, выявленные при реальном использовании.

- [x] Исправление публикации UDP-порта 514 → 5140 внутри контейнера.
- [x] Исправление двух тихих дефектов `LokiLogSink`.
- [x] `ReceivedAt`: `TEXT` → Unix milliseconds.
- [x] Persistent SQLite connection и `busy_timeout`.
- [x] Переиспользование `SocketAddress`.
- [x] Версионирование Grafana dashboard.

## ✅ Milestone 4 — Production-readiness (v2.2)

Аудит ownership, batching, shutdown, storage isolation, HTTP lifecycle,
конфигурации и устойчивости парсера завершён.

### P0 — безопасность конвейера

- [x] P0.1 `OwnedIngress`: явное освобождение buffer вытесненной записи.
- [x] P0.2 Windowed batching по `BatchSize` и `BatchTimeout`.
- [x] P0.3 Graceful shutdown: сохранение хвоста и отсутствие утечек/double-dispose.

### P1 — production-readiness

- [x] P1.1 SQLite primary изолирован от best-effort Loki/Console secondaries.
- [x] P1.2 HTTP lifetime `LokiLogSink`: response disposal, timeout,
  ограниченный retry и корректная cancellation semantics.
- [x] P1.3 Startup validation через `ValidateOnStart`, включая требование
  ровно одного SQLite primary.
- [x] P1.4 Parser hardening для повреждённых и обрезанных datagram.

## 🟡 Milestone 5 — Эксплуатационная зрелость (В РАБОТЕ)

Текущий незарелизенный блок делает repository удобным для развёртывания
и сопровождения, не меняя архитектуру горячего пути.

### Завершено в `Unreleased`

- [x] P2.3 Production, development и monitoring Docker Compose сценарии.
- [x] P2.3 Non-root chiseled runtime, read-only root filesystem,
  сброшенные capabilities и resource limits.
- [x] P2.3 Исправленный systemd unit с `StateDirectory`, правильным SQLite path
  и временем graceful shutdown.
- [x] P2.4 Актуальные `README.md`, `DESIGN_GUIDE.md`, `DOCKER.md`
  и `DOCKER-CHANGES.md`.
- [x] Provisioning Loki datasource и Grafana dashboard.
- [x] Очистка legacy Grafana-файлов и нормализация line endings через
  `.gitattributes`.

### Осталось

- [ ] P2.1 Метрики: received / parsed / sqlite_saved / channel_dropped.
- [ ] P2.2 Retention SQLite и контролируемая очистка старых записей.

## 🔮 Milestone 6 — Экстремальная нагрузка (v3.0)

Цель — нагрузки порядка `>100k pps`. Реализовывать только после измерений,
подтверждающих необходимость.

- [ ] `SocketAsyncEventArgs` вместо `ReceiveFromAsync`.
- [ ] `System.IO.Pipelines` для будущего TCP-транспорта.
- [ ] Оценка другого хранилища при объёмах, выходящих за разумные границы SQLite.
- [ ] Горизонтальное масштабирование через durable broker.

---

## Как отслеживать работу

Каждый milestone оформляется как GitHub Milestone, а задача — как Issue с меткой
`bug`, `enhancement`, `performance`, `operations` или `documentation`.

Закрытый пункт должен ссылаться на commit или pull request, который его реализовал.
Оптимизации горячего пути выполняются только после измерения baseline и повторного
замера результата.
