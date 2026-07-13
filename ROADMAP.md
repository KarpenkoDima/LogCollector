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

## 🔜 Milestone 4 — Устойчивость к отказам (v2.2)

Цель: деградация одного sink не влияет на остальные.

- [ ] Независимые воркеры на канал для каждого sink
  Сейчас `Task.WhenAll` в FanOut означает, что зависший Loki блокирует SQLite.
  Решение: отдельный `Channel<LogEntry>` и `BackgroundService` на каждый sink.
- [ ] Retry-стратегия для Loki при недоступности
  Локальный WAL-буфер или очередь вместо потери батча.
- [ ] Health-check endpoint для каждого sink
- [ ] Метрика `dropped-entries` через EventCounters

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
