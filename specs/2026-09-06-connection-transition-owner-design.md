# Владелец перехода соединения (issue #179)

Дата: 2026-09-06. Статус: реализован, ветка `claude/connection-transition-owner-691f22`.

> Cold-review (два прохода, пять ревьюеров) нашёл ещё четыре дефекта на тех же путях, два из
> них воспроизводимы на базовом коде; все исправлены в этом же изменении, потому что оно
> переписывает код, где они живут: `OnceOpen` после `await OnConnected` без проверки владения;
> `_isIntentionalDisconnect`, который `Connect()` после `Disconnect()` не сбрасывал; таймер
> попытки, живущий после тихо отменённого handshake; `Connect()` поверх закрывающегося сокета
> без объявления конца сессии и без sweep'а запросов. Отправка запроса *наблюдается*, а не
> ожидается перед promise — иначе зависшая запись держала бы вызывающего дольше его таймаута.

Issue: https://github.com/StaticBit-io/XrplCSharp/issues/179 — продолжение #178. Четыре гонки
между конкурентными операциями над соединением и два хвоста (`NotConnectedException` без
сообщения, `WebSocketClient.SendMessageAsync` переподключает сокет при отправке).

## 1. Проблема

В `Xrpl/Client/connection.cs` соединение двигают шесть путей: `ChangeServer`,
`Disconnect`/`DisconnectAndWaitAsync`, `Connect`, `RetireCurrentSessionAndReconnectAsync`
(быстрое переподключение из ping-проверки), `ReconnectLoopAsync` и
`OnConnectHandlerFailedAsync`. Каждый сам решает, что делать с сокетом, а два одновременно
идущих пути согласуются точечными проверками `ReferenceEquals(ws, …)` после того `await`
или consumer-callback'а, который кто-то заметил. #178 добавил три такие проверки, ревью
каждый раз находило следующее окно.

Оставшиеся окна (нумерация как в issue):

1. **Выход `ReconnectLoopAsync` против `OnceClose`.** Цикл делает `break` по `IsConnected()`,
   пока его задача ещё завершается; закрытие, обработанное в этот зазор, видит
   `_reconnectLoop` «живым», не запускает замену — и никто не переподключается.
2. **Отправка против retirement.** `Request`/`GRequest` читают `ws` и вызывают `SendMessage`;
   между чтением и отправкой соединение может быть retired. Promise уже отклонён, но запрос
   всё же уходит на сервер, который клиент покинул.
3. **Retire против блокирующего consumer'а.** `RetireCurrentSessionAndReconnectAsync`
   захватывает сессию и сокет *после* `SetConnectionState(RestoringConnection)`. Handler,
   который внутри этого callback'а успел установить новую сессию, получает её помеченной
   retiring.
4. **`ChangeServer` перекрывает конкурентный `Disconnect()`.** `ChangeServer` уступает поток
   (остановка процессора, уведомление о конце сессии, ожидание ping); `Disconnect()` в этот
   момент находит `ws == null`, выставляет `_permanentlyDisconnected` и возвращает «already
   disconnected»; `ChangeServer` затем сбрасывает флаг и подключается — клиент онлайн после
   того, как пользователь его выключил.

Хвосты:

5. `EnsureConnectionForRequest` при `ImmediateFail` бросает `new NotConnectedException()` —
   `Message` равен runtime-умолчанию, consumer, классифицирующий по тексту, его не узнаёт.
6. `WebSocketClient.SendMessageAsync` на не-Open сокете вызывает `_ = Connect()` —
   `ConnectAsync` на уже использованном `ClientWebSocket` бросает, `_ws` уничтожается,
   поднимается `OnConnectionError` — и всё равно продолжает `SendAsync`. Отправка не должна
   переподключать; отправка на неоткрытый сокет должна провалиться, и провал должен быть
   наблюдаем владельцем запроса.

## 2. Решение: поколение перехода

### 2.1. Инвариант

У соединения в каждый момент ровно один владелец — **переход** (transition), обозначенный
монотонным `long _generation`. Кто владеет поколением, тот и решает, что происходит с
сокетом. Операция, обнаружившая, что поколение сменилось, **отступает** (stands down): ничего
больше не трогает и, если у неё есть вызывающий, сообщает ему, что её перекрыли.

Все записи в `ws`, `_generation`, `_permanentlyDisconnected` и состояние reconnect-цикла
(`_reconnectCts`, `_reconnectLoopGeneration`, `_reconnectAttempts`) делаются под одной
блокировкой `_transitionLock` (она поглощает нынешние `_disconnectLock` и
`_reconnectStateLock`). `_sessionLock` остаётся отдельным (его берёт per-frame путь
`IsFromLiveSession`) и вкладывается внутрь `_transitionLock`; `_messageProcessorLock` тоже
вкладывается. Порядок захвата фиксирован: `_transitionLock` → `_sessionLock` →
`_messageProcessorLock`. Под `_transitionLock` не выполняется consumer-код и нет `await`.

### 2.2. Кто начинает поколение, кто продолжает

**Начинают новое поколение** (`TakeOver`) — команды пользователя и быстрый reconnect:

| Путь | Условие захвата | Что делает под блокировкой |
|---|---|---|
| `Connect()` | безусловно, если не `IsConnected()` | `++_generation`, снять `_permanentlyDisconnected`, остановить reconnect-цикл (cts наружу для Cancel/Dispose), `ws` не трогает (он `null` или Connecting: Connecting-сокет предыдущего владельца забирается и закрывается) |
| `ChangeServer()` | безусловно | `++_generation`, retire сессии, забрать `ws`, остановить цикл, остановить ping-таймер, отсоединить процессор сообщений |
| `Disconnect()` / `DisconnectAndWaitAsync()` | безусловно | `++_generation`, `_permanentlyDisconnected = true`, забрать `ws` (сессия **не** retire — `OnceClose` объявляет `UserDisconnected`), остановить цикл, ping, процессор |
| `RetireCurrentSessionAndReconnectAsync(socket)` | **условно**: только если `ws` всё ещё тот сокет, который ping-проверка признала мёртвым | как `ChangeServer`, плюс установить `_reconnectCts`, `_reconnectAttempts = 1` |

Условный захват закрывает ещё одну гонку, которую issue не перечисляет: `Disconnect()` во
время ping-проверки. Раньше `RetireCurrent…` проверял `_permanentlyDisconnected` в двух местах
по ходу; теперь он просто не получает владения, если сокет уже забрали.

**Продолжают текущее поколение** (никогда не увеличивают его) — callback'и сокета и цикл:

- `OnceClose` / `OnConnectionFailed` для сокета текущего поколения: делают свою уборку и
  запускают reconnect-цикл **под текущим поколением**, если цикл под ним ещё не активен.
  Для сокета старого поколения — только объявления (`OnDisconnect`, `OnSessionEnded`,
  `_disconnectTcs`), без sweep'а, без смены состояния и без цикла: там уже есть владелец.
- `ReconnectLoopAsync(generation)`: каждая итерация под блокировкой проверяет владение;
  retire предыдущей попытки — под блокировкой, без смены поколения.
- `OnConnectHandlerFailedAsync`: ту же уборку, что и сегодня, и цикл под поколением
  сессии, чей handler упал, с seed'ом счётчика попыток. Если цикл под этим поколением уже
  активен (handler упал у его же попытки), цикл просто продолжает — его счётчик и так растёт.
- `OnceOpen` для сессии не текущего поколения — отказ (сокет закроет тот, кто его создал,
  см. 2.4).

### 2.3. Окно 1: атомарное освобождение цикла

`_reconnectLoopGeneration` заменяет `_reconnectLoop` + `IsCompleted`. Цикл, увидев после
попытки открытый сокет, освобождает метку **под `_transitionLock` и с повторной проверкой
`ShouldBeConnected()` под той же блокировкой**. `OnceClose` принимает решение «запускать ли
цикл» под той же блокировкой. Двух исходов достаточно, третьего нет: либо `OnceClose` видит
активный цикл (и тот, взяв блокировку, увидит уже закрытый сокет и продолжит крутиться), либо
цикл уже освободился (и `OnceClose` запускает новый).

### 2.4. Окно 4 и «сокет, созданный во время гонки»

`ConnectCoreAsync(generation, ct)` (бывший `ConnectInternalAsync`) проверяет владение под
блокировкой в трёх точках: после захвата `_connectLock` (до создания сокета), **вместе с
установкой `ws`** (создание сокета и установка сессии — одна критическая секция, так что
перекрывающий `TakeOver` либо застаёт `ws == null`, либо забирает уже созданный сокет) и после
`await socket.Connect()`. Потеряв владение после подключения, он закрывает свой сокет сам,
если тот всё ещё в `ws`, и выходит.

`ChangeServer` проверяет владение после каждого `await` и после каждого consumer-callback'а
(`SetConnectionState`, sweep — continuations consumer'а выполняются inline, `NotifySessionEnded`).
Запись `url`/`config` — под блокировкой вместе с проверкой. Уведомление `Connecting`
переносится **после** захвата: handler, вызвавший из него `Disconnect()`, должен победить.

### 2.5. Что видит вызывающий, когда его перекрыли

| Операция | Перекрыта `Disconnect` | Перекрыта `ChangeServer`/`Connect` |
|---|---|---|
| `ChangeServer(A)` | `NotConnectedException` («Client has been disconnected») | `OperationCanceledException` с текстом, называющим перекрывшую операцию; проверка после `WaitForConnectionAsync` — по `url != A` |
| `Connect()` | `NotConnectedException` (как сейчас, из `WaitForConnectionAsync`) | успех, если клиент подключён — контракт `Connect` («OperationCanceledException только от токена вызывающего») сохраняется |
| `Disconnect()` | — | молча: не отправляет финальное `Disconnected`-уведомление, если поколение ушло |

`ChangeServer`, перекрытый другим `ChangeServer`, уже разобрал старое соединение — это не
откатывается, перекрывший владеет остатком.

### 2.6. Окно 2 и хвост 6: отправка под блокировкой, наблюдаемая отправка

`Request`/`GRequest` берут сокет и запускают отправку **под `_transitionLock`**:
синхронный префикс `SendMessageAsync` (захват `_sendLock` сокета и выдача записи в
`ClientWebSocket`) выполняется до освобождения блокировки, так что retirement, идущий под той
же блокировкой, либо застаёт запрос ещё не отправленным (и `ws == null` его отклонит), либо
уже выданным в сокет. Остаток: отправка, вставшая в очередь за другой отправкой того же
сокета, повторно проверяет `_isIntentionalDisconnect` после `_sendLock`; между этой проверкой
и записью блокировки нет — это несколько инструкций, задокументировано.

`WebSocketClient`:
- `public Task SendMessageAsync(byte[] message)` — возвращает `Task`; не-Open сокет →
  faulted `InvalidOperationException`; `Connect()` из отправки удалён; исключения `SendAsync`
  не глотаются; `_sendLock` сериализует целые сообщения (заодно закрывает перемешивание
  кадров двух >1 МБ сообщений, которое `ManagedWebSocket` не запрещает).
- `public void SendMessage(string)` — прежний fire-and-forget контракт (ping, consumer'ы):
  ошибку сообщает через `OnError`, как и раньше, соединение не трогает.

`Request` ждёт `Task` отправки; провал → `requestManager.Reject(id, DisconnectedException)`,
после чего `await Promise` отдаёт отказ вызывающему. Ожидание отправки перед ожиданием
ответа ничего не стоит: ответ не может прийти раньше, чем запрос ушёл.

### 2.7. Хвост 5

`NotConnectedException()` без аргумента получает сообщение по умолчанию: «The client is not
connected to a server…». `ImmediateFail` бросает с текстом, называющим политику.

## 3. Тесты

Новые классы в `Tests/Xrpl.Tests/Client/` (MSTest, префикс `TestU`):

- **`TestUConnectionTransitionOwner`**:
  - `Disconnect()` из `OnSessionEnded`-handler'а внутри `ChangeServer` побеждает: клиент
    остаётся `Disconnected`, `IsConnected()` false, `ChangeServer` бросает
    `NotConnectedException`, второй сервер не получает handshake (окно 4, детерминированно).
  - второй `ChangeServer(C)`, запущенный из `OnSessionEnded` первого `ChangeServer(B)`:
    первый бросает `OperationCanceledException`, клиент оказывается на C, ровно один
    `OnConnected`.
  - `ChangeServer`, запущенный из `OnConnectionStatus(RestoringConnection)` быстрого
    reconnect'а (сервер молчит на ping, `SilentOnPingServer`): fast reconnect отступает,
    клиент подключается к новому серверу один раз, `RestoringConnection` после `Connected`
    не приходит (окно 3 в его достижимой форме).
  - сокет, закрывающийся сразу после успешной попытки reconnect-цикла (`CloseAfterHandshakeServer`
    закрывает первые N соединений сразу после handshake): клиент всё равно доходит до сервера,
    когда тот перестаёт закрывать (окно 1 в его достижимой форме; сам зазор в несколько
    инструкций мок не открывает — это сказано в remarks теста).
  - `Disconnect()` из `OnConnected`-handler'а: `OnceOpen` не сообщает `Connected` поверх
    `Disconnected` и не запускает ping-таймер (найдено cold-review).
  - `Connect()` после `Disconnect()` к серверу, который поднимется позже: клиент остаётся
    reconnecting, а не «closed permanently» — `_isIntentionalDisconnect` следует за поколением
    (найдено cold-review).
  - `ImmediateFail` даёт непустое сообщение, содержащее «not connected»;
    `new NotConnectedException()` тоже (в том же классе, не отдельным).
- **`TestWebSocketClient`** (дополнение): `SendMessage` на закрытом сокете не поднимает
  `OnConnectionError` и не уничтожает сокет; `SendMessageAsync` на закрытом сокете — faulted.
- `TestUReconnectSessionRaces`, `TestURequestDuringServerSwitch`, `TestUFastReconnectSettling`,
  `TestUChangeServerFailure`, `TestUSessionEndedNotification`, `TestUOnConnectedHandlerFailure`
  остаются регрессионной сеткой; их remarks про `_reconnectLoop` обновляются.

## 4. Что не меняется

- Публичный API `Connection`: `ws`, `WebsocketSendAsync`, события. `WebsocketSendAsync`
  остаётся для совместимости (тесты `TestSubscribe` его используют), `Request` им больше не
  пользуется.
- Порядок «`ws` очищается до sweep'а» из #177 сохраняется: `TakeOver` забирает сокет первым.
- `_isIntentionalDisconnect`, per-socket трекинг `_userInitiatedSockets`, `_pingTimeoutSocket`
  / `_networkDropSocket` — как есть; они про классификацию закрытия, не про владение.
- Семантика `OnSessionEnded`, `OnDisconnect`, `OnConnectionStatus` — как есть.

## 5. Версия и changelog

11.3.2.0 выпущена (тег v11.3.2). Это изменение меняет контракт (`ChangeServer` может бросить
«перекрыт», `WebSocketClient.SendMessage` больше не переподключает) — minor: **11.4.0.0**,
`PackageVersion` в `Xrpl/Xrpl.csproj`, раздел в `CHANGES.md` с датой релиза, проставляемой
при выпуске.
