# Roadmap

Планирование развития LogCollector по milestone'ам. Каждый milestone —
законченный инкремент с чёткой целью.

## ✅ Milestone 1 — Работающий конвейер (v1.0)

Первая версия, доказавшая жизнеспособность идеи.

- [x] UDP-приём syslog
- [x] Запись в SQLite
- [x] Channel-based конвейер
- [x] Первые unit-тесты

## ✅ Milestone 2 — Production-архитектура (v2.0)

Полное переписывание на zero-allocation Clean Architecture.

- [x] `readonly struct LogEntry` (Рихтер)
- [x] Zero-allocation парсер (Кокоса)
- [x] Batch-drain конвейер (Клири)
- [x] FanOut-архитектура синков
- [x] Docker Compose + Loki + Grafana
- [x] BenchmarkDotNet с воспроизводимыми замерами
- [x] 23 теста

## ✅ Milestone 3 — Production-эксплуатация (v2.1)

Всё, что вскрылось при реальной работе на debian-cicd.

- [x] Исправление порта 514→5140
- [x] Исправление двух тихих багов LokiLogSink
- [x] `ReceivedAt` TEXT→INTEGER
- [x] Persistent SQLite connection
- [x] SocketAddress reuse
- [x] Grafana dashboard в git

## 🟡 Milestone 4 — Production-readiness (v2.2, В РАБОТЕ)

По итогам аудита 2026-07-14. Блок P0 закрыт, P1/P2 в работе.

### ✅ P0 — до любого запуска (ЗАКРЫТО)
- [x] P0.1 Безопасное вытеснение — OwnedIngress с явным Dispose
- [x] P0.2 Настоящее накопление batch — окно вместо snapshot-drain
- [x] P0.3 Корректное завершение — доказано тестами (хвост + owners=0)

### 🔜 P1 — до объявления production-ready
- [ ] P1.1 Изоляция SQLite от Loki (primary/secondary sinks)
  Сейчас `Task.WhenAll` связывает их судьбу — зависший Loki блокирует SQLite.
- [ ] P1.2 HTTP lifetime — Dispose response, timeout, retry-лимит, cancellation
- [ ] P1.3 Валидация конфигурации при запуске — падать рано с понятной ошибкой
- [ ] P1.4 Усиление парсера — битые дейтаграммы без падения

### 🔮 P2 — эксплуатационная зрелость
- [ ] P2.1 Метрики: received / parsed / sqlite_saved / channel_dropped
- [ ] P2.2 Retention SQLite (автоочистка старых записей)
- [ ] P2.3 Deployment hardening (systemd TimeoutStopSec, resource limits)
- [ ] P2.4 Документация production-эксплуатации

## 🔮 Milestone 5 — Экстремальная нагрузка (v3.0)

Цель: >100k pps. Оправдано только при реальной потребности.

- [ ] `SocketAsyncEventArgs` вместо `ReceiveFromAsync`
  Исключает аллокацию на каждый вызов при приёме.
- [ ] `System.IO.Pipelines` для TCP-транспорта
- [ ] Оценка перехода на ClickHouse при retention >1 месяца
- [ ] Горизонтальное масштабирование (несколько инстансов + Kafka)

---

## Как это отслеживается

Каждый milestone → GitHub Milestone. Каждый пункт → Issue с меткой
(`bug` / `enhancement` / `performance`). Закрытые пункты ссылаются на
commit или PR, который их реализовал.

Принцип приоритизации (из опыта проекта): исправления горячего пути
(аллокации, блокировки) важнее холодных микрооптимизаций. Числа важнее
предположений — каждый perf-пункт требует замера до и после.
