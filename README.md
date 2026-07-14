# LogCollector

Лёгкий сервис на .NET 9 для приёма BSD Syslog (RFC 3164) от MikroTik по UDP и пакетной записи в SQLite.

## Что реализовано

- UDP listener без копирования datagram: приём сразу в память из `MemoryPool<byte>`;
- байтовый парсер без regex и промежуточных строк;
- стандартный RFC 3164 и расширение RouterOS `: topic,severity message`;
- bounded `Channel<LogEntry>` с контролем памяти;
- пакетная запись через Dapper в одну SQLite-транзакцию;
- WAL, индексы по времени и устройству, повтор записи при временной ошибке;
- корректное освобождение pooled-буферов и drain очереди при остановке;
- валидация конфигурации, systemd, Docker и end-to-end тест.

Сервис намеренно принимает только MikroTik/BSD Syslog по UDP. TCP и Winlogbeat JSON из исходного учебного ТЗ не входят в текущую, уточнённую задачу.

## Архитектура

```text
MikroTik
   │ UDP datagram
   ▼
UdpSyslogListener ──► Rfc3164Parser ──► bounded Channel<LogEntry>
                                              │
                                              ▼
                                      BatchProcessor
                                              │ batch + transaction
                                              ▼
                                      SqliteLogRepository
```

- `LogCollector.Core` — доменные типы, без внешних зависимостей.
- `LogCollector.Application` — интерфейсы и пакетная оркестрация.
- `LogCollector.Infrastructure` — UDP, парсер, SQLite/Dapper и DI.
- `LogCollector.Host` — composition root и конфигурация.
- `LogCollector.Tests` — unit, integration и полный UDP→SQLite тест.

## Локальный запуск

Нужен .NET SDK 9.0.305 или совместимый patch-релиз.

```powershell
dotnet restore
dotnet test -c Release
dotnet run --project LogCollector.Host
```

По умолчанию сервис слушает `5140/udp` и создаёт `logs.db`. Отправка теста из PowerShell:

```powershell
$udp = [Net.Sockets.UdpClient]::new()
$data = [Text.Encoding]::UTF8.GetBytes('<132>Jul 14 12:30:45 edge-router : firewall,info accepted')
$udp.Send($data, $data.Length, '127.0.0.1', 5140)
$udp.Dispose()
```

## Настройка MikroTik

Пример RouterOS (замените адрес коллектора):

```routeros
/system logging action
add name=logcollector target=remote remote=192.168.88.10 remote-port=514 bsd-syslog=yes

/system logging
add action=logcollector topics=firewall
add action=logcollector topics=system
add action=logcollector topics=warning
```

Если action уже существует, используйте `set`, а не `add`. Межсетевой экран хоста должен разрешать входящий UDP/514.

## Docker

```bash
docker compose up -d --build
docker compose logs -f
```

Compose публикует стандартный `514/udp`, внутри непривилегированный контейнер слушает `5140/udp`. База хранится в volume `logcollector-data`.

Разработка с портом 5140 и каталогом `./data`:

```bash
docker compose -f docker-compose.yml -f docker-compose.dev.yml up --build
```

## Конфигурация

Настройки читаются из `appsettings.json`, environment variables и аргументов командной строки. Для environment variables вложенность задаётся двойным подчёркиванием, например `PIPELINE__BATCHSIZE=1000`.

| Секция | Параметр | По умолчанию | Назначение |
|---|---|---:|---|
| `UdpListener` | `Address` | `0.0.0.0` | адрес bind |
| `UdpListener` | `Port` | `5140` | UDP-порт |
| `UdpListener` | `MaxDatagramSize` | `8192` | максимальный размер сообщения |
| `Pipeline` | `Capacity` | `10000` | верхняя граница очереди |
| `Pipeline` | `BatchSize` | `500` | максимум строк в транзакции |
| `Pipeline` | `FlushInterval` | `00:00:02` | максимальная задержка неполного batch |
| `Pipeline` | `RetryDelay` | `00:00:01` | пауза после ошибки SQLite |
| `Sqlite` | `ConnectionString` | `Data Source=logs.db` | расположение базы |
| `Sqlite` | `BusyTimeoutSeconds` | `5` | ожидание занятой SQLite |

Таблица `logs` содержит `priority`, `facility`, `severity`, исходный timestamp устройства, hostname, topic, message и UTC-время приёма в Unix milliseconds.

## Важные эксплуатационные свойства

UDP не имеет настоящего backpressure. Когда SQLite не успевает, bounded channel останавливает чтение сокета; после заполнения socket buffer ядро начнёт отбрасывать новые datagram. Это сохраняет ограниченное потребление памяти, но не гарантирует доставку — для гарантированной доставки нужен TCP или брокер сообщений.

`MemoryPool<byte>` уменьшает число больших массивов, но аренда ownership-объекта всё ещё может создавать небольшую служебную аллокацию. Поэтому формулировка здесь честная: парсинг и выделение полей zero-copy, а не «абсолютный zero-allocation всего процесса».
