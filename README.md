# LogCollector

`LogCollector` — фоновый сервис на .NET 9 для приёма BSD Syslog от MikroTik по UDP, разбора сообщений без промежуточных строк и пакетной записи в локальную SQLite.

SQLite является обязательным первичным хранилищем. После успешного сохранения batch может дополнительно отправляться в Loki или выводиться в консоль. Проект рассчитан на один экземпляр collector на одном хосте и небольшую внутреннюю сеть, включая текущий сценарий с одним MikroTik и дальнейшее подключение нескольких роутеров.

> UDP не гарантирует доставку. Проект ограничивает потребление памяти и корректно управляет pooled-буферами, но не превращает UDP в надёжный транспорт.

## Что реализовано

- `UdpSyslogListener` принимает IPv4 UDP datagram через один долгоживущий pinned receive buffer;
- после приёма выполняется одна копия datagram в буфер из `MemoryPool<byte>.Shared`, чтобы данные могли безопасно пережить следующий вызов `ReceiveFromAsync`;
- `CompositeLogParser` делегирует разбор конкретным реализациям `ILogParser`;
- `MikroTikSyslogParser` использует байтовый `SyslogParser` без regex, `Split`, `Substring` и промежуточных строк;
- поддерживаются два фактических формата MikroTik:
  - расширенный RouterOS: `hostname : topic,severity message`;
  - обычный BSD Syslog/RFC 3164: `hostname message`;
- `OwnedIngress` ограничивает очередь 10 000 элементами и реализует политику **freshest wins**: при заполнении вытесняется самая старая запись, а принадлежащий ей pooled-буфер освобождается;
- `BatchWriterService` формирует окно batch до первого из событий: `BatchSize`, `BatchTimeout`, завершение channel или остановка приложения;
- `FanOutLogRepository` сначала сохраняет batch в обязательный SQLite primary sink, затем запускает необязательные secondary sinks;
- `SqliteLogSink` использует одну открытую connection, подготовленную команду и одну транзакцию на batch;
- SQLite работает в `WAL` с `synchronous=NORMAL` и индексами по времени, hostname и severity;
- `LokiLogSink` группирует записи по hostname, ограниченно повторяет transient HTTP failures и не отменяет уже успешную запись в SQLite;
- Options проверяются при запуске через `ValidateOnStart`;
- тесты покрывают парсер, владение буферами, overflow очереди, batching, shutdown drain, SQLite, fan-out и HTTP-поведение Loki;
- присутствуют Docker-конфигурации для production, development и monitoring.

## Архитектура и поток данных

```text
MikroTik
   │ UDP/IPv4 datagram
   ▼
UdpSyslogListener
   │
   ├─ один pinned byte[] для socket receive
   ├─ одна CopyTo в MemoryPool<byte> buffer
   ▼
CompositeLogParser
   ▼
MikroTikSyslogParser → SyslogParser
   │
   │ LogEntry + ownership через RawBuffer
   ▼
OwnedIngress (capacity 10 000, explicit drop-oldest)
   │
   ▼
BatchWriterService
   │ batch по размеру или времени
   ▼
FanOutLogRepository
   ├─ PRIMARY: SqliteLogSink ──► logs.db
   ├─ SECONDARY: LokiLogSink ──► Loki ──► Grafana
   └─ SECONDARY: ConsoleLogSink (Development)
```

### Слои решения

```text
LogCollector.Core
└── Domain
    ├── LogEntry
    └── SyslogSeverity

LogCollector.Application
└── Interfaces
    ├── ILogParser
    ├── ILogRepository
    └── ILogSink

LogCollector.Infrastructure
├── Listeners
│   └── UdpSyslogListener
├── Parsers
│   ├── CompositeLogParser
│   ├── MikroTikSyslogParser
│   └── SyslogParser
├── Pipeline
│   ├── OwnedIngress
│   └── BatchWriterService
├── Sinks
│   ├── FanOutLogRepository
│   ├── SqliteLogSink
│   ├── LokiLogSink
│   └── sink factories
└── ServiceCollectionExtensions

LogCollector.Host
├── Program.cs
└── appsettings*.json

LogCollector.Tests
├── Configuration
├── Parsers
├── Pipeline
└── Sinks
```

Направление зависимостей:

```text
Host ──► Infrastructure ──► Application ──► Core
Host ─────────────────────────────────────► Core
```

`Core` не зависит от внешних пакетов. `Application` содержит контракты. Сетевой ввод, pipeline, SQLite, Loki и DI находятся в `Infrastructure`. `Host` является composition root.

Подробное объяснение решений: [`DESIGN_GUIDE.md`](DESIGN_GUIDE.md).

## Поддерживаемые сообщения

### Расширенный формат MikroTik

```text
<30>Jun  4 18:00:00 edge-router : firewall,info forward: in:ether1 out:bridge
```

Из него извлекаются:

- `Priority = 30`;
- `TimestampRaw = "Jun  4 18:00:00"`;
- `Hostname = "edge-router"`;
- `Topic = "firewall"`;
- `Severity = Info`;
- `Message = "forward: in:ether1 out:bridge"`.

### Обычный BSD Syslog/RFC 3164

```text
<30>Jun 18 20:50:28 edge-router filter rule changed by admin
```

В этом варианте `Topic` остаётся пустым, `Severity` определяется из PRI, а весь текст после hostname становится `Message`.

Парсер выполняет дешёвые структурные проверки, но не является полным валидатором RFC 3164. Timestamp устройства сохраняется как исходные 15 байт: RFC 3164 не содержит года и timezone.

## Быстрый локальный запуск

### Требования

- .NET 9 SDK;
- опционально `sqlite3` для просмотра базы;
- опционально `netcat` для отправки тестовой datagram из Linux.

### Сборка и тесты

```bash
dotnet restore
dotnet build -c Release
dotnet test -c Release
```

### Запуск в Development

Development-конфигурация слушает `5140/udp`, выводит разобранные записи в консоль и сохраняет их в `dev-logs.db`.

Linux/macOS:

```bash
DOTNET_ENVIRONMENT=Development \
  dotnet run --project LogCollector.Host
```

PowerShell:

```powershell
$env:DOTNET_ENVIRONMENT = "Development"
dotnet run --project LogCollector.Host
```

### Тестовая отправка из Linux

```bash
printf '%s\n' '<30>Jun  4 18:00:00 edge-router : firewall,info local-test' \
  | nc -u -w1 127.0.0.1 5140
```

### Тестовая отправка из PowerShell

```powershell
$udp = [Net.Sockets.UdpClient]::new()
$data = [Text.Encoding]::UTF8.GetBytes(
    '<30>Jun  4 18:00:00 edge-router : firewall,info local-test')
$udp.Send($data, $data.Length, '127.0.0.1', 5140)
$udp.Dispose()
```

### Проверка SQLite

```bash
sqlite3 dev-logs.db \
  'SELECT Id, Hostname, Topic, Severity, Message, ReceivedAt FROM Logs ORDER BY Id DESC LIMIT 20;'
```

## Настройка MikroTik

Пример RouterOS, где `192.168.88.10` — адрес хоста с collector:

```routeros
/system logging action
add name=logcollector \
    target=remote \
    remote=192.168.88.10 \
    remote-port=514 \
    bsd-syslog=no

/system logging
add action=logcollector topics=firewall
add action=logcollector topics=system
add action=logcollector topics=warning
```

`bsd-syslog=no` рекомендуется для текущего парсера, потому что RouterOS добавляет `topic,severity`. При `bsd-syslog=yes` сообщение также принимается, но `Topic` будет пустым, а severity будет восстановлена из PRI.

Если action уже существует, измените его через `set`, а не создавайте второй. На хосте должен быть разрешён входящий UDP-порт collector.

Не включайте без измерений логирование каждого проходящего firewall packet: даже один роутер способен создать поток, существенно превышающий обычные system/DHCP/warning события.

## Конфигурация

Generic Host читает конфигурацию из:

1. `appsettings.json`;
2. `appsettings.{Environment}.json`;
3. environment variables;
4. аргументов командной строки.

Для environment variables вложенность задаётся двойным подчёркиванием, а элементы массива `LogSinks` — числовым индексом.

### `SyslogListener`

| Параметр | Значение по умолчанию | Допустимый диапазон | Назначение |
|---|---:|---:|---|
| `Port` | `514` | `1..65535` | UDP-порт процесса |
| `MaxDatagramSize` | `8192` | `256..65507` | размер единственного pinned receive buffer |

Пример:

```bash
SyslogListener__Port=5140
SyslogListener__MaxDatagramSize=8192
```

### `BatchWriter`

| Параметр | Значение по умолчанию | Допустимый диапазон | Назначение |
|---|---:|---:|---|
| `BatchSize` | `500` | `1..10000` | максимум записей в одной транзакции |
| `BatchTimeout` | `00:00:02` | `> 0`, не более 30 секунд | максимальная длительность неполного batch-window |

### `LogSinks`

Требуется **ровно один** sink с `Type = Sqlite`. Он становится primary и является источником истины.

```json
{
  "LogSinks": [
    {
      "Type": "Sqlite",
      "ConnectionString": "Data Source=logs.db"
    }
  ]
}
```

Допустимые типы текущей версии:

| Type | Роль | Параметры |
|---|---|---|
| `Sqlite` | обязательный primary | `ConnectionString` |
| `Loki` | optional secondary | `Endpoint`, optional `Labels` |
| `Console` | optional secondary для разработки | без обязательных параметров |

Пример SQLite + Loki:

```json
{
  "LogSinks": [
    {
      "Type": "Sqlite",
      "ConnectionString": "Data Source=logs.db"
    },
    {
      "Type": "Loki",
      "Endpoint": "http://loki:3100",
      "Labels": {
        "app": "logcollector",
        "environment": "production"
      }
    }
  ]
}
```

Эквивалентные environment variables:

```dotenv
LogSinks__0__Type=Sqlite
LogSinks__0__ConnectionString=Data Source=/app/data/logs.db
LogSinks__1__Type=Loki
LogSinks__1__Endpoint=http://loki:3100
LogSinks__1__Labels__app=logcollector
```

## Поведение очереди при нагрузке

Внутренняя capacity сейчас жёстко задана в DI:

```text
10 000 LogEntry
```

Когда очередь заполнена, `OwnedIngress`:

1. извлекает самую старую запись;
2. вызывает `RawBuffer.Dispose()` ровно один раз;
3. увеличивает `DroppedCount`;
4. помещает новую запись.

Это политика **freshest wins**. Listener не ждёт освобождения места и продолжает принимать новые datagram, пока успевает socket и процесс.

Следствия:

- память очереди ограничена;
- старые сообщения могут быть потеряны при длительной перегрузке;
- UDP сам по себе тоже может потерять datagram до попадания в приложение;
- `DroppedCount` пока не опубликован как metric или health signal.

## Batching и запись

Batch-window открывается после получения первой записи и закрывается при первом условии:

1. накоплено `BatchWriter.BatchSize` элементов;
2. истёк `BatchWriter.BatchTimeout`;
3. producer завершил channel;
4. началась остановка приложения.

SQLite primary записывает batch в одной транзакции. После успешного commit вторичные sinks запускаются параллельно с общим budget 5 секунд.

### Важная семантика ошибок

- ошибка SQLite считается ошибкой primary;
- `FanOutLogRepository` пробрасывает её в `BatchWriterService`;
- `BatchWriterService` логирует ошибку и освобождает все буферы batch;
- **автоматического retry SQLite в текущей версии нет, такой batch теряется**;
- ошибка, timeout или недоступность Loki не отменяет уже успешный SQLite commit;
- пропущенные Loki batch автоматически не воспроизводятся после восстановления Loki.

## SQLite schema

Текущая таблица:

```sql
CREATE TABLE Logs (
    Id           INTEGER PRIMARY KEY AUTOINCREMENT,
    Priority     INTEGER NOT NULL,
    TimestampRaw TEXT    NOT NULL,
    Hostname     TEXT    NOT NULL,
    Topic        TEXT    NOT NULL,
    Severity     INTEGER NOT NULL,
    Message      TEXT    NOT NULL,
    ReceivedAt   INTEGER NOT NULL
);
```

`ReceivedAt` хранится как Unix milliseconds UTC. Создаются индексы:

```text
idx_logs_received_at
idx_logs_hostname
idx_logs_severity
```

Строки создаются только на границе SQLite/Loki/Console. До этого поля `LogEntry` являются `ReadOnlyMemory<byte>` slices одного rented buffer.

## Docker

Docker-файлы соответствуют текущим секциям `SyslogListener`, `BatchWriter` и `LogSinks`.

- `docker-compose.yml` — production: один collector + SQLite;
- `docker-compose.dev.yml` — самостоятельный SDK-container с `dotnet watch`;
- `docker-compose.monitoring.yml` — override, добавляющий Loki и Grafana к тому же collector.

Подробные команды, security-настройки и backup: [`DOCKER.md`](DOCKER.md).

### Production

```bash
cp .env.example .env
docker compose build --pull logcollector
docker compose up -d
docker compose logs -f logcollector
```

Поток портов:

```text
MikroTik → host 514/udp → Docker NAT → container 5140/udp
```

### Development

```bash
mkdir -p data
docker compose -f docker-compose.dev.yml up --build
```

Listener доступен на `127.0.0.1:5140/udp`, база находится в `./data/dev-logs.db`.

### Monitoring

```bash
mkdir -p secrets
openssl rand -base64 36 > secrets/grafana_admin_password.txt
chmod 600 secrets/grafana_admin_password.txt

docker compose \
  -f docker-compose.yml \
  -f docker-compose.monitoring.yml \
  up -d --build
```

Grafana по умолчанию доступна на `http://127.0.0.1:3000`. Loki не публикуется на host. Dashboard использует Loki labels `app` и `hostname`.

## Тестирование

```bash
dotnet test -c Release --logger "console;verbosity=normal"
```

Тестовый проект проверяет:

- корректное извлечение полей и zero-copy slices;
- PRI, обрезанные datagram, пустой hostname и неизвестные topic/severity;
- validation границ Options;
- flush по timeout и размеру;
- последовательности batch `[8, 8, 4]`;
- освобождение `RawBuffer` после успеха и ошибки;
- explicit drop-oldest без утечки owner;
- drain хвоста при штатной остановке;
- SQLite round-trip всех полей;
- primary/secondary semantics fan-out;
- Loki 2xx, 4xx, 5xx retry, response disposal и cancellation.

## Гарантии и ограничения текущей версии

### Что гарантируется внутри процесса

- bounded очередь не растёт бесконечно;
- вытеснённый pooled-буфер освобождается;
- принятый в batch буфер освобождается после попытки сохранения;
- успешный SQLite commit не отменяется ошибкой secondary sink;
- конфигурация основных Options проверяется при запуске.

### Что не гарантируется

- доставка UDP datagram;
- отсутствие потерь при переполнении ingress;
- retry или durable replay при ошибке SQLite;
- replay пропущенных Loki batch;
- сохранение IP-адреса UDP-отправителя — в базе хранится только hostname из сообщения;
- RFC 5424 и TCP syslog;
- retention/автоматическая очистка SQLite;
- горизонтальное масштабирование нескольких writer над одной SQLite;
- настоящий readiness endpoint.

## Текущий scope дальнейшего развития

До расширения с одного до нескольких MikroTik наиболее полезны:

1. сохранение source IP;
2. метрики received/parsed/dropped/channel depth/SQLite latency;
3. configurable retention SQLite;
4. retry policy для transient SQLite errors;
5. отделение Loki от основного writer отдельной очередью;
6. нагрузочный тест на реальных сообщениях RouterOS;
7. backup/restore test.
