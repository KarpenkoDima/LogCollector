# Почему LogCollector устроен именно так

Этот документ объясняет не абстрактный «идеальный syslog collector», а фактическую реализацию текущего репозитория:

```text
UdpSyslogListener
    → CompositeLogParser / MikroTikSyslogParser / SyslogParser
    → OwnedIngress
    → BatchWriterService
    → FanOutLogRepository
        → SqliteLogSink (primary)
        → LokiLogSink / ConsoleLogSink (secondary)
```

Главные ограничения задачи:

- источник отправляет UDP, поэтому абсолютной гарантии доставки нет;
- приложение должно ограничивать память при burst-нагрузке;
- байты сообщения нужны дольше одного socket receive;
- SQLite должна оставаться источником истины;
- Loki не должен превращать успешный SQLite commit в ошибку batch;
- каждый pool-rented buffer должен иметь одного понятного владельца и освобождаться ровно один раз.

---

## 1. Слои и зависимости

### `LogCollector.Core`

Содержит доменные типы:

- `LogEntry`;
- `SyslogSeverity`.

Проект не зависит от Infrastructure, SQLite, HTTP или Generic Host.

### `LogCollector.Application`

Содержит контракты:

- `ILogParser` — разбор datagram;
- `ILogRepository` — инициализация и сохранение batch;
- `ILogSink` — отдельное назначение логов.

Application зависит только от Core.

### `LogCollector.Infrastructure`

Содержит техническую реализацию:

- UDP socket;
- parsers;
- bounded ingress;
- batch orchestration;
- SQLite;
- Loki;
- Console sink;
- Options validation;
- DI registration.

### `LogCollector.Host`

`Program.cs` является composition root. Он создаёт Generic Host, включает systemd integration и вызывает `AddLogCollectorInfrastructure`.

Зависимости направлены внутрь:

```text
Host → Infrastructure → Application → Core
```

Это не означает, что каждый класс обязан называться «UseCase» или «Repository». Главное архитектурное свойство здесь — доменные типы и контракты не зависят от конкретных способов ввода и хранения.

---

## 2. Полный жизненный цикл одной datagram

Рассмотрим сообщение:

```text
<30>Jun  4 18:00:00 edge-router : firewall,info accepted connection
```

Оно проходит следующие этапы.

### Шаг 1. Socket получает байты в pinned buffer

`UdpSyslogListener` один раз создаёт:

```csharp
GC.AllocateArray<byte>(maxDatagramSize, pinned: true)
```

Этот массив живёт столько же, сколько listener. Его адрес не перемещается GC, поэтому он подходит как стабильное место приёма данных из socket.

Следующий `ReceiveFromAsync` перезапишет тот же массив. Поэтому передавать slices этого массива в asynchronous pipeline нельзя.

### Шаг 2. Datagram копируется в rented buffer

После получения длины listener вызывает:

```csharp
MemoryPool<byte>.Shared.Rent(length)
```

и выполняет одну копию:

```text
pinned receive buffer → rented datagram buffer
```

Эта копия необходима для текущей архитектуры: запись может находиться в ingress и batch дольше, чем длится следующий socket receive.

Следовательно, точная формулировка производительности:

- socket receive buffer переиспользуется;
- parser не копирует отдельные поля;
- на datagram выполняется одна копия в буфер с независимым временем жизни;
- весь процесс не является «абсолютно zero-allocation».

### Шаг 3. Parser создаёт slices

`SyslogParser` работает с:

```csharp
ReadOnlyMemory<byte>
ReadOnlySpan<byte>
```

Поля `TimestampRaw`, `Hostname`, `Topic` и `Message` не получают собственные массивы. Они являются диапазонами одного datagram buffer.

### Шаг 4. Ownership прикрепляется к `LogEntry`

После успешного parse listener создаёт копию struct:

```csharp
entry = entry with { RawBuffer = datagramOwner };
```

С этого момента `RawBuffer` представляет владение памятью всех byte-slices записи.

### Шаг 5. `OwnedIngress` принимает или вытесняет запись

Новая запись помещается в bounded channel. Если очередь заполнена, самая старая запись извлекается, её `RawBuffer` освобождается, а новая запись занимает освободившееся место.

### Шаг 6. `BatchWriterService` собирает batch

Consumer читает `LogEntry` из channel и держит их в `List<LogEntry>` до flush по размеру, времени, completion или shutdown.

### Шаг 7. SQLite создаёт строки

Только `SqliteLogSink` преобразует byte fields в `string`:

```csharp
Encoding.UTF8.GetString(entry.Hostname.Span)
```

Это неизбежная граница текущей схемы, поскольку SQLite columns имеют тип `TEXT`.

### Шаг 8. Буфер возвращается в pool

`BatchWriterService.SaveAndDisposeAsync` освобождает `RawBuffer` в `finally` независимо от результата repository write.

---

## 3. Почему одновременно используются `ReadOnlySpan<byte>` и `ReadOnlyMemory<byte>`

### `ReadOnlySpan<byte>`

Span удобен для синхронного разбора:

- дешёвые slices;
- `IndexOf`;
- `SequenceEqual`;
- нет копирования;
- нельзя сохранить в обычном объекте или struct, который переживает текущий stack frame;
- нельзя переносить через `await`.

Поэтому parser использует Span локально.

### `ReadOnlyMemory<byte>`

Memory можно хранить в `LogEntry` и передавать через channel. Она содержит ссылку на backing memory, offset и length.

Важно: `ReadOnlyMemory<byte>` не владеет памятью. Она только указывает на неё. Владение обеспечивается отдельным `IMemoryOwner<byte> RawBuffer`.

### Почему нельзя сразу вызвать `Dispose`

После:

```csharp
var entry = Parse(owner.Memory);
```

нельзя вернуть `owner` в pool, пока запись находится в очереди. Следующий арендатор сможет перезаписать массив, а `Hostname`, `Topic` и `Message` начнут указывать на чужие данные.

Поэтому ownership путешествует вместе с entry до последнего consumer.

---

## 4. `LogEntry` как `readonly struct`

`LogEntry` содержит:

- числовые значения;
- четыре `ReadOnlyMemory<byte>` slices;
- `DateTimeOffset`;
- ссылку на `IMemoryOwner<byte>`.

`readonly struct` уменьшает необходимость создавать отдельный объект `LogEntry` на heap для каждого сообщения. Channel хранит значения inline во внутреннем буфере.

При этом важно не переоценивать результат:

- `IMemoryOwner<byte>` является managed reference;
- реализация `MemoryPool` может создавать небольшой ownership object;
- `List<LogEntry>` выделяет массив для batch;
- SQLite/Loki/Console создают строки;
- JSON payload Loki создаёт объекты и строки.

Верное обещание проекта — **zero-copy field extraction на parser hot path**, а не zero-allocation всего приложения.

---

## 5. Почему listener использует два буфера

Один pinned receive buffer минимизирует долгоживущие pinned allocations. Если арендовать и pin-ить новый массив на каждую datagram, GC и pinned memory fragmentation получили бы лишнюю нагрузку.

Но одного массива недостаточно, потому что pipeline асинхронный:

```text
receive N
receive N+1
receive N+2
```

выполняются быстрее, чем SQLite обязательно сохранит `N`.

Поэтому выбрана схема:

```text
один pinned receive buffer
          ↓ одна копия
отдельный rented buffer на принятую запись
          ↓
очередь и batch
```

Listener также переиспользует один `SocketAddress` для IPv4 overload `ReceiveFromAsync`, чтобы не создавать новый endpoint object на каждую datagram.

### Что listener пока не делает

- не сохраняет IP-адрес sender в `LogEntry`;
- не слушает IPv6: socket создаётся с `AddressFamily.InterNetwork`;
- не принимает TCP syslog;
- не публикует метрики socket errors или parse failures.

---

## 6. Parser: permissive, но с дешёвым hardening

### Поддерживаемые варианты

#### MikroTik extended

```text
<30>Jun  4 18:00:00 edge-router : firewall,info message
```

Parser извлекает topic и текстовую severity.

#### BSD Syslog/RFC 3164

```text
<30>Jun 18 20:50:28 edge-router message
```

Topic остаётся пустым, severity вычисляется из младших трёх бит PRI.

### Проверки

`SyslogParser` проверяет:

- первый байт `<`;
- наличие `>`;
- PRI состоит только из цифр;
- `Utf8Parser` употребил весь PRI segment;
- PRI находится в диапазоне `0..191`;
- timestamp занимает ровно 15 байт и за ним есть пробел;
- hostname не пуст;
- cursor не выходит за source;
- trailing CR/LF удаляются из message.

### Почему parser не является полным RFC validator

Полная проверка месяца, дня, времени, допустимого hostname и всех вариантов RFC усложнила бы hot path. Текущая задача — безопасно выделить поля из ожидаемых RouterOS сообщений и не упасть на повреждённой datagram.

Неизвестный topic принимается. Неизвестная текстовая severity становится `SyslogSeverity.Unknown`, но запись не отбрасывается.

### `CompositeLogParser`

Listener зависит от `ILogParser`, а не от конкретного `SyslogParser`. Сейчас composite содержит только `MikroTikSyslogParser`, но позволяет добавить новый формат без изменения network listener.

Порядок parsers важен: более специфичные форматы должны идти раньше более permissive.

---

## 7. Почему обычный `BoundedChannelFullMode.DropOldest` не подходит

`LogEntry` владеет `RawBuffer`. Если встроенный channel самостоятельно удалит старый item, приложение не получит возможности вызвать:

```csharp
evicted.RawBuffer?.Dispose();
```

Это приведёт к утечке rented buffers при длительной перегрузке.

Поэтому `OwnedIngress` создаёт внутренний channel в режиме:

```csharp
FullMode = BoundedChannelFullMode.Wait
```

но сам listener никогда не вызывает `WriteAsync`. Вместо этого `TryEnqueue` вручную реализует:

```text
TryWrite(new)
    ├─ success → accepted
    └─ full
        → TryRead(oldest)
        → Dispose(oldest.RawBuffer)
        → increment DroppedCount
        → TryWrite(new)
```

### Почему нужен `lock`

Проверка заполнения, вытеснение и вставка должны быть одной атомарной последовательностью. Без lock два producer могли бы одновременно извлечь разные элементы или потерять ownership новой записи.

### Почему `SingleReader = false`

Channel читают два пути:

1. штатный `BatchWriterService`;
2. eviction-path внутри `OwnedIngress.TryEnqueue`.

Поэтому заявлять одного reader нельзя.

### Почему `SingleWriter = false`

Запись и completion потенциально вызываются из разных execution paths. Реализация не даёт channel оптимизацию single-writer.

### Семантика `freshest wins`

Это не backpressure. При заполнении ingress producer не ждёт consumer, а удаляет старейшую запись.

Плюсы:

- memory bound;
- listener продолжает принимать свежие события;
- владелец вытеснённой памяти освобождается корректно.

Минусы:

- при перегрузке теряются старые события;
- точная доставка не гарантируется;
- без metrics оператор может не заметить рост `DroppedCount`.

---

## 8. Batching: окно, а не snapshot

Наивная реализация часто делает:

```text
WaitToReadAsync
→ прочитать всё доступное сейчас
→ немедленно flush
```

При равномерном трафике это способно давать batch по одному элементу: consumer просыпается на каждой новой записи раньше, чем накопится группа.

Текущий `BatchWriterService` использует **windowed accumulation**.

### Алгоритм

1. Ожидается первая запись нового окна.
2. После её появления запускается `BatchTimeout`.
3. Все доступные элементы читаются синхронно через `TryRead`.
4. Если batch ещё не полон, consumer ждёт следующий элемент внутри текущего timeout.
5. Окно закрывается по первому условию:
   - достигнут `BatchSize`;
   - истёк `BatchTimeout`;
   - channel завершён;
   - начался shutdown.
6. Batch передаётся repository.

Например, при `BatchSize = 8` двадцать быстрых сообщений должны сформировать:

```text
[8, 8, 4]
```

а не двадцать batch по одному сообщению.

### Почему batch хранится в `List<LogEntry>`

Размер заранее известен из Options:

```csharp
new List<LogEntry>(BatchSize)
```

Это даёт один переиспользуемый внутренний массив на жизненный цикл service loop. После flush вызывается `Clear`, capacity сохраняется.

### Освобождение ресурсов

`SaveAndDisposeAsync` вызывает repository и затем в `finally` освобождает каждый `RawBuffer`.

Это защищает memory pool даже если SQLite или другой repository path выбросил исключение.

### Семантика ошибки primary

Текущая реализация не повторяет неудачный SQLite batch:

```text
primary throws
→ BatchWriterService logs error
→ entries dropped
→ RawBuffer Dispose
```

Такой выбор предотвращает бесконечное удержание памяти, но означает реальную потерю данных при transient SQLite error. Retry/outbox пока не реализованы.

---

## 9. Нормальная остановка и drain

Hosted services зарегистрированы в порядке:

```text
BatchWriterService
UdpSyslogListener
```

Generic Host останавливает их в обратном порядке:

1. listener перестаёт принимать новые datagram;
2. listener вызывает `OwnedIngress.Complete()`;
3. writer дочитывает channel;
4. неполный хвост сохраняется;
5. `RawBuffer` каждого принятого entry освобождается.

`BatchWriterService` отдельно обрабатывает shutdown во время открытого batch-window и выполняет final drain через `CancellationToken.None`, чтобы уже принятые записи получили шанс дойти до primary storage.

Тесты проверяют:

- сохранение tail;
- shutdown под нагрузкой;
- ровно одно освобождение owner;
- отсутствие потерь внутри уже принятого тестового channel.

### Граница гарантии

Drain относится только к записям, уже находящимся внутри ingress или batch. UDP-пакет, который ещё находился в kernel buffer или в сети, не становится частью гарантии приложения.

Кроме того, host/container/service manager должен дать процессу достаточно времени до принудительного `SIGKILL`.

---

## 10. Почему SQLite — обязательный primary sink

Конфигурация строится из массива `LogSinks`. Для каждого элемента выбирается `ISinkFactory` по полю `Type`.

При startup проверяется:

- `LogSinks` не пуст;
- каждый entry имеет `Type`;
- type зарегистрирован;
- найден ровно один `Sqlite`.

SQLite является source of truth. Loki и Console не могут заменить primary.

### Контракт `FanOutLogRepository`

```text
await SQLite primary
    ├─ failure → throw to BatchWriterService
    └─ success
        → run secondaries in parallel
        → timeout/errors are logged and swallowed
```

Это исправляет важную семантическую проблему обычного `Task.WhenAll` по всем sinks. Если SQLite уже выполнила commit, а Loki упал, batch нельзя называть потерянным в основном хранилище.

### Secondary timeout

Все secondary sinks получают linked token с budget 5 секунд. Они запускаются параллельно. Ошибка одного secondary не мешает другому.

Ограничение текущей схемы: writer всё равно ждёт завершения secondaries или истечения budget перед чтением следующего batch. Поэтому медленный Loki может задержать pipeline до пяти секунд, хотя не отменит primary commit.

Для полной изоляции Loki нужен отдельный bounded channel или durable outbox.

---

## 11. SQLite implementation

`SqliteLogSink` использует:

- одну connection на lifetime sink;
- `journal_mode=WAL`;
- `synchronous=NORMAL`;
- `busy_timeout=5000`;
- prepared `INSERT` command;
- transaction на batch;
- переиспользуемые `SqliteParameter`.

### Почему одна connection

Pipeline имеет одного штатного batch writer. Одна connection и одна prepared command уменьшают повторное открытие базы и подготовку SQL.

### Почему transaction на batch

Без transaction каждое `INSERT` может потребовать отдельного commit. При batch одна транзакция покрывает до `BatchSize` строк.

### Где появляются строки

`LogEntry` несёт UTF-8 bytes. Перед присваиванием SQLite parameters sink вызывает `Encoding.UTF8.GetString`.

Полностью убрать эту аллокацию в текущей TEXT-schema нельзя. Важно, что строки создаются поздно — после batching, непосредственно на storage boundary.

### Schema и индексы

Таблица содержит:

```text
Priority
TimestampRaw
Hostname
Topic
Severity
Message
ReceivedAt
```

Индексы созданы по:

```text
ReceivedAt
Hostname
Severity
```

### Ограничения SQLite path

- один writer подходит текущей архитектуре;
- retention не реализован;
- source IP не сохраняется;
- при ошибке batch нет retry;
- длительное firewall logging может быстро увеличить файл;
- WAL backup нельзя сводить к копированию одного `logs.db` во время активной записи.

---

## 12. Loki как best-effort secondary

`LokiLogSink` вызывается только после успешного SQLite primary.

### Grouping

Текущая реализация группирует entries только по `Hostname`:

```text
один Loki stream на distinct hostname в batch
```

В stream labels входят:

- статические labels из конфигурации, например `app=logcollector`;
- динамический `hostname`.

`Topic`, `Severity` и текст message не добавляются как labels текущей реализацией.

Это уменьшает cardinality индекса. Message отправляется как log line.

### Почему manual grouping

Для key используется `ReadOnlyMemory<byte>` с `ReadOnlyMemoryByteComparer`, который сравнивает содержимое без предварительного преобразования каждого hostname в string.

String создаётся один раз на distinct hostname в batch, а не на каждую запись при grouping.

### HTTP policy

Для каждого push:

- endpoint: `/loki/api/v1/push`;
- per-request timeout: 2 секунды;
- максимум две повторные попытки после первой;
- 4xx считается permanent и не повторяется;
- network error, timeout и 5xx считаются transient;
- caller cancellation пробрасывается;
- каждый `HttpResponseMessage` освобождается;
- после исчерпания попыток ошибка логируется, но не выбрасывается как потеря SQLite batch.

### Граница best-effort

Если Loki был недоступен, пропущенные данные остаются в SQLite, но автоматически не отправляются повторно после восстановления. Для replay нужен outbox или отдельный механизм чтения SQLite.

---

## 13. Config-driven sinks и расширяемость

Новый sink добавляется так:

1. реализовать `ILogSink`;
2. создать `ISinkFactory`;
3. зарегистрировать factory в `ServiceCollectionExtensions`;
4. добавить элемент в `LogSinks`.

Listener, parser и batch writer при этом не меняются.

Но primary semantics сейчас зафиксирована специально для SQLite: DI требует ровно один `Type=Sqlite`. Добавить другой primary без изменения composition logic нельзя. Это осознанное ограничение текущего продукта, а не полностью универсальная sink framework.

---

## 14. Options validation

`SyslogListenerOptionsValidator` проверяет:

```text
Port: 1..65535
MaxDatagramSize: 256..65507
```

`BatchWriterOptionsValidator` проверяет:

```text
BatchSize: 1..10000
BatchTimeout: > 0 и <= 30 секунд
```

`ValidateOnStart` переносит ошибку конфигурации на startup, до нормального приёма трафика.

Sink-specific значения (`ConnectionString`, `Endpoint`, type names) проверяются во время построения `ILogRepository` через factories и DI logic.

---

## 15. Потоки, concurrency и ownership

### Producer

Штатный producer один — `UdpSyslogListener`. Но `OwnedIngress` не заявляет `SingleWriter=true`, потому что completion и возможные будущие producer paths не объединены строгим single-writer контрактом.

### Consumer

Штатный consumer один — `BatchWriterService`. Однако eviction-path тоже читает старый item из channel, поэтому внутреннему channel требуется `SingleReader=false`.

### SQLite

`SqliteLogSink` рассчитан на последовательные вызовы от одного writer. Он не защищает connection/command от конкурентного `SaveBatchAsync` несколькими потоками. Текущий pipeline соблюдает это ограничение.

### Loki secondaries

`FanOutLogRepository` запускает разные secondary sinks параллельно, но один и тот же sink получает один вызов на batch из последовательного main writer.

---

## 16. Карта владения буфером

| Сценарий | Кто освобождает `RawBuffer` |
|---|---|
| Parser не распознал datagram | `UdpSyslogListener` в `finally` |
| Entry принят ingress и затем сохранён | `BatchWriterService.SaveAndDisposeAsync` |
| Entry принят ingress, repository выбросил exception | `BatchWriterService.SaveAndDisposeAsync` в `finally` |
| Очередь переполнена, старый entry вытеснен | `OwnedIngress.TryEnqueue` |
| После вытеснения новая запись неожиданно не записалась | `OwnedIngress.TryEnqueue` освобождает новый owner |
| Штатная остановка с хвостом | final drain writer, затем `SaveAndDisposeAsync` |

Ключевой invariant:

```text
каждый rented owner либо передан ingress, либо немедленно Dispose;
после передачи ingress listener его больше не освобождает.
```

---

## 17. Failure semantics

| Сбой | Текущее поведение |
|---|---|
| Datagram не распознана | buffer освобождается, запись не создаётся |
| SocketException в receive loop | warning, listener продолжает работу |
| Ingress заполнен | старейшая запись и её buffer удаляются |
| SQLite initialization failed | startup writer завершается с ошибкой |
| SQLite batch write failed | ошибка логируется, batch теряется, buffers освобождаются |
| Loki startup недоступен | warning, приложение продолжает старт |
| Loki 4xx | без retry, Loki copy теряется |
| Loki timeout/network/5xx | ограниченные retry, затем Loki copy теряется |
| Secondary завис | отмена после общего timeout, primary остаётся успешным |
| Shutdown | listener завершает producer, writer пытается сохранить хвост |

Эта таблица важнее общего заявления «сервис надёжный»: она точно показывает, где данные сохраняются, где могут быть потеряны и что происходит с памятью.

---

## 18. Почему нет Native AOT

Текущая реализация не использует Dapper; SQLite доступ реализован напрямую через `Microsoft.Data.Sqlite` и prepared command.

Тем не менее Native AOT не включён автоматически, потому что для него требуется отдельная проверка всей dependency graph:

- trimming compatibility Generic Host и Options;
- JSON serialization Loki payload;
- systemd integration;
- SQLite native dependencies;
- publish/runtime tests для нужных Linux architectures.

Небольшой chiseled runtime image уже уменьшает production footprint без изменения модели выполнения. Переход к AOT должен быть отдельной измеряемой задачей, а не декларативным флагом в Dockerfile.

---

## 19. Границы текущей версии

- UDP/IPv4;
- один bound address `0.0.0.0`;
- BSD Syslog/RFC 3164 и MikroTik extension;
- без RFC 5424;
- без TCP/TLS syslog;
- timestamp устройства хранится без года и timezone;
- IP sender не сохраняется;
- ingress capacity 10 000 пока hardcoded;
- overflow policy — drop oldest;
- SQLite retry отсутствует;
- retention SQLite отсутствует;
- Loki best-effort без replay;
- Loki может задержать следующий batch до secondary timeout;
- один SQLite writer;
- горизонтальное масштабирование потребует другой модели storage/coordination;
- нет metrics endpoint и настоящего readiness endpoint.

---

## 20. Следующие архитектурные шаги

Для эксплуатации с несколькими MikroTik приоритетны:

1. добавить source IP в domain и SQLite schema;
2. экспортировать счётчики received, parsed, rejected, ingress dropped;
3. измерять channel depth и batch latency;
4. добавить retention worker для SQLite;
5. различать transient/permanent SQLite failures и реализовать bounded retry;
6. вынести Loki в отдельный bounded pipeline;
7. добавить readiness, отражающий UDP bind и SQLite initialization;
8. провести burst/load test на реальных RouterOS сообщениях;
9. проверить backup и restore WAL-базы;
10. сделать capacity и secondary timeout конфигурируемыми после benchmark.

Текущая архитектура уже хорошо решает основную задачу: принимает UDP, ограничивает память, не теряет ownership pooled-буферов и отделяет обязательный SQLite primary от необязательных secondary sinks. Дальнейшее развитие должно усиливать наблюдаемость и эксплуатационную надёжность, а не переписывать базовый pipeline без измеримых причин.
