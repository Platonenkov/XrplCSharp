# Руководство по протоколу кредитования (XLS-66d)

Данное руководство описывает использование протокола кредитования XRPL с SDK XrplCSharp. Протокол обеспечивает обеспеченное кредитование на уровне реестра через кредитных брокеров и хранилища.

> **Примечание:** Протокол кредитования требует поправки `LendingProtocol` (XLS-66d). Функция находится в статусе черновика и может быть изменена. Требуется rippled 3.1.0+.

## Содержание

- [Обзор](#обзор)
- [Ключевые концепции](#ключевые-концепции)
- [Типы транзакций](#типы-транзакций)
- [Пошаговая настройка кредитного брокера](#пошаговая-настройка-кредитного-брокера)
- [Создание и управление кредитом](#создание-и-управление-кредитом)
- [CounterpartySignature (совместная подпись LoanSet)](#counterpartysignature-совместная-подпись-loanset)
- [Объекты реестра](#объекты-реестра)
- [Тип Number](#тип-number)
- [Типичные ошибки](#типичные-ошибки)
- [Лучшие практики](#лучшие-практики)

---

## Обзор

Протокол кредитования XRPL реализует обеспеченное кредитование на уровне реестра:

```text
Брокер (Кредитор)                       Заёмщик
┌──────────────────────┐              ┌──────────────────────┐
│                      │   LoanSet    │                      │
│  Vault ◄── Депозит   │ ◄──────────► │  Получает основную   │
│  Cover ◄── Депозит   │ (совместная  │  сумму               │
│  LoanBroker ── Loan  │  подпись)    │  Погашает LoanPay    │
└──────────────────────┘              └──────────────────────┘
```

**Брокер** (кредитор) создаёт хранилище для активов, настраивает кредитного брокера с параметрами кредитования и выдаёт кредиты заёмщикам. **Заёмщик** совместно подписывает кредитное соглашение и погашает долг периодическими платежами.

---

## Ключевые концепции

### Хранилище (Vault)

Хранилище содержит активы, доступные для кредитования. Создаётся через `VaultCreate`, хранит XRP или IOU-токены. Перед созданием кредитного брокера необходимо создать и пополнить хранилище.

### Кредитный брокер (LoanBroker)

**LoanBroker** — объект реестра, представляющий кредитную организацию. Ссылается на хранилище и определяет параметры кредитования: ставки покрытия, комиссии за управление и лимиты долга. Создаётся через `LoanBrokerSet`.

### Покрытие (Cover)

Брокер должен внести **покрытие** (залог со стороны брокера) в кредитного брокера. Покрытие защищает заёмщиков и обеспечивает участие брокера в рисках. Управляется через `LoanBrokerCoverDeposit` и `LoanBrokerCoverWithdraw`.

### Кредит (Loan)

**Loan** — объект реестра, представляющий активный кредит между брокером и заёмщиком. Создаётся через `LoanSet` (требует совместной подписи обеих сторон). Отслеживает основную сумму, процентные ставки, график платежей и остатки.

### CounterpartySignature

`LoanSet` — особая транзакция, требующая **двух подписей**: брокер (отправитель) подписывает транзакцию обычным способом, а заёмщик (контрагент) предоставляет `CounterpartySignature`. Обе подписывают одну и ту же транзакцию, но каждая под своим hash-префиксом, начиная с `fixCleanup3_4_0` в rippled.

### Тип Number

Числовые поля кредитования (например, `PrincipalRequested`, `DebtMaximum`) используют тип `Number` XRPL — 12-байтовый формат из 8-байтовой знаковой мантиссы и 4-байтовой знаковой экспоненты. Это отличается от стандартного типа `Amount`.

---

## Типы транзакций

| Транзакция | Назначение | Кто отправляет |
|-----------|---------|----------------|
| `LoanBrokerSet` | Создать или обновить кредитного брокера | Брокер |
| `LoanBrokerDelete` | Удалить кредитного брокера | Брокер |
| `LoanBrokerCoverDeposit` | Внести покрытие в брокера | Брокер |
| `LoanBrokerCoverWithdraw` | Вывести покрытие из брокера | Брокер |
| `LoanBrokerCoverClawback` | Возврат покрытия от держателя | Брокер |
| `LoanSet` | Создать новый кредит (совместная подпись) | Брокер + Заёмщик |
| `LoanDelete` | Удалить полностью погашенный кредит | Брокер |
| `LoanManage` | Управление состоянием кредита (дефолт/ухудшение) | Брокер |
| `LoanPay` | Внести платёж по кредиту | Заёмщик |

---

## Пошаговая настройка кредитного брокера

### 1. Создание хранилища

Брокер создаёт хранилище для хранения активов кредитования:

```csharp
using Xrpl.Models.Transactions;
using Xrpl.Models.Common;
using Xrpl.Sugar;
using static Xrpl.Models.Common.Common;

VaultCreate vaultTx = new VaultCreate
{
    Account = walletBroker.ClassicAddress,
    Asset = new IssuedCurrency { Currency = "XRP" },
};
vaultTx = await client.Autofill(vaultTx);
TransactionSummary vaultResult = await client.SubmitAndWait(vaultTx, walletBroker, true);

// Извлечение VaultID из метаданных
string vaultId = GetCreatedObjectId(vaultResult, LedgerEntryType.Vault);
```

### 2. Пополнение хранилища

Внесение активов в хранилище для кредитования:

```csharp
VaultDeposit depositTx = new VaultDeposit
{
    Account = walletBroker.ClassicAddress,
    VaultID = vaultId,
    Amount = new Currency { Value = "100000000", CurrencyCode = "XRP" }, // 100 XRP
};
depositTx = await client.Autofill(depositTx);
await client.SubmitAndWait(depositTx, walletBroker, true);
```

### 3. Создание кредитного брокера

Создание брокера, ссылающегося на пополненное хранилище:

```csharp
LoanBrokerSet brokerTx = new LoanBrokerSet
{
    Account = walletBroker.ClassicAddress,
    VaultID = vaultId,
};
brokerTx = await client.Autofill(brokerTx);
TransactionSummary brokerResult = await client.SubmitAndWait(brokerTx, walletBroker, true);

string brokerId = GetCreatedObjectId(brokerResult, LedgerEntryType.LoanBroker);
```

### 4. Настройка параметров брокера (опционально)

Обновление параметров кредитования:

```csharp
LoanBrokerSet updateTx = new LoanBrokerSet
{
    Account = walletBroker.ClassicAddress,
    VaultID = vaultId,
    CoverRateMinimum = 15000,        // 150% минимальная ставка покрытия
    CoverRateLiquidation = 12000,    // 120% порог ликвидации
    ManagementFeeRate = 100,         // 1% комиссия за управление (базисные пункты / 100)
};
updateTx = await client.Autofill(updateTx);
await client.SubmitAndWait(updateTx, walletBroker, true);
```

### 5. Внесение покрытия

Депозит покрытия для возможности выдачи кредитов:

```csharp
LoanBrokerCoverDeposit coverTx = new LoanBrokerCoverDeposit
{
    Account = walletBroker.ClassicAddress,
    LoanBrokerID = brokerId,
    Amount = new Currency { Value = "50000000", CurrencyCode = "XRP" }, // 50 XRP
};
coverTx = await client.Autofill(coverTx);
await client.SubmitAndWait(coverTx, walletBroker, true);
```

---

## Создание и управление кредитом

### 1. Создание кредита (LoanSet)

`LoanSet` требует совместной подписи брокера и заёмщика. Подробности в разделе [CounterpartySignature](#counterpartysignature-совместная-подпись-loanset).

```csharp
LoanSet loanTx = new LoanSet
{
    Account = walletBroker.ClassicAddress,
    LoanBrokerID = brokerId,
    Counterparty = walletBorrower.ClassicAddress,
    PrincipalRequested = "10000000",  // Тип Number (не drops)
};

// Требуется специальная совместная подпись — см. раздел CounterpartySignature
TransactionSummary result = await SubmitLoanSetWithCounterpartySig(
    client, loanTx, walletBroker, walletBorrower);

string loanId = GetCreatedObjectId(result, LedgerEntryType.Loan);
```

### 2. Внесение платежа по кредиту

Заёмщик вносит платежи по кредиту:

```csharp
LoanPay payTx = new LoanPay
{
    Account = walletBorrower.ClassicAddress,
    LoanID = loanId,
    Amount = new Currency { Value = "10000000", CurrencyCode = "XRP" },
};
payTx = await client.Autofill(payTx);
TransactionSummary result = await client.SubmitAndWait(payTx, walletBorrower, true);
```

### 3. Удаление полностью погашенного кредита

После полного погашения брокер может удалить кредит:

```csharp
LoanDelete deleteTx = new LoanDelete
{
    Account = walletBroker.ClassicAddress,
    LoanID = loanId,
};
deleteTx = await client.Autofill(deleteTx);
await client.SubmitAndWait(deleteTx, walletBroker, true);
```

> **Важно:** Нельзя удалить кредит с непогашенным остатком (`tecHAS_OBLIGATIONS`). Кредит должен быть полностью погашен.

### 4. Управление состоянием кредита

Брокер может отметить кредит как дефолтный, обесценённый или восстановить его:

```csharp
// Отметить кредит как дефолтный
LoanManage manageTx = new LoanManage
{
    Account = walletBroker.ClassicAddress,
    LoanID = loanId,
    Flags = LoanManageFlags.tfLoanDefault,
};
manageTx = await client.Autofill(manageTx);
await client.SubmitAndWait(manageTx, walletBroker, true);
```

**Флаги LoanManage (взаимоисключающие):**
- `tfLoanDefault` — отметить кредит как дефолтный
- `tfLoanImpair` — отметить кредит как обесценённый
- `tfLoanUnimpair` — восстановить кредит из обесценённого состояния

### 5. Удаление кредитного брокера

Когда у брокера нет активных кредитов:

```csharp
LoanBrokerDelete deleteBrokerTx = new LoanBrokerDelete
{
    Account = walletBroker.ClassicAddress,
    LoanBrokerID = brokerId,
};
deleteBrokerTx = await client.Autofill(deleteBrokerTx);
await client.SubmitAndWait(deleteBrokerTx, walletBroker, true);
```

---

## CounterpartySignature (совместная подпись LoanSet)

`LoanSet` уникальна среди транзакций XRPL — требует **двух подписей**. Брокер подписывает транзакцию обычным способом (`TxnSignature`), а заёмщик предоставляет `CounterpartySignature` — внутренний STObject, содержащий `SigningPubKey` и `TxnSignature` заёмщика.

SDK предоставляет `LoanSigningHelper` и `XrplWallet.SignAsLoanCounterparty()` с тремя паттернами подписи, аналогичными Batch signing (V1/V2/V3).

### Подготовка (общая для всех паттернов)

```csharp
using Xrpl.Wallet;

// Autofill рассчитывает комиссию (включая overhead CounterpartySignature)
loanTx = await client.Autofill(loanTx);
// PrepareForSigning устанавливает SigningPubKey брокера и удаляет поля подписей
JsonObject prepared = LoanSigningHelper.PrepareForSigning(loanTx, brokerWallet);
```

### V1 — Автоматический (оба ключа доступны локально)

Используйте, когда кошельки брокера и заёмщика доступны на одном устройстве:

```csharp
SignatureResult result = LoanSigningHelper.SignLoanSet(prepared, brokerWallet, borrowerWallet);
await client.SubmitRequest(result.TxBlob);
```

### V2 — Параллельный (ключи на разных устройствах, подпись независимо)

Используйте, когда брокер и заёмщик подписывают независимо, а третья сторона объединяет подписи:

```csharp
// Устройство A (брокер): подписывает транзакцию обычным способом
var brokerDict = JsonSerializer.Deserialize<Dictionary<string, object>>(
    prepared.ToJsonString(), XrplJsonOptions.Default);
SignatureResult brokerSig = brokerWallet.Sign(brokerDict);

// Устройство B (заёмщик): подписывает как контрагент (независимая копия payload)
var borrowerDict = JsonSerializer.Deserialize<Dictionary<string, object>>(
    prepared.ToJsonString(), XrplJsonOptions.Default);
SignatureResult counterpartySig = borrowerWallet.SignAsLoanCounterparty(borrowerDict);

// Комбинатор: объединяет обе подписи в один blob
SignatureResult combined = LoanSigningHelper.CombineLoanSignatures(
    brokerSig.TxBlob, counterpartySig.TxBlob);
await client.SubmitRequest(combined.TxBlob);
```

### V3 — Последовательный (заёмщик подписывает первым, передаёт брокеру)

Используйте в реальном сценарии, когда заёмщик подписывает первым и отправляет частично подписанный blob брокеру:

```csharp
// Шаг 1: Заёмщик получает подготовленный JSON транзакции, подписывает как контрагент
var txDict = JsonSerializer.Deserialize<Dictionary<string, object>>(
    prepared.ToJsonString(), XrplJsonOptions.Default);
SignatureResult withCounterparty = borrowerWallet.SignAsLoanCounterparty(txDict);
// withCounterparty.TxBlob передаётся брокеру (через API, QR-код и т.д.)

// Шаг 2: Брокер получает частично подписанный blob, добавляет TxnSignature
SignatureResult fullySigned = LoanSigningHelper.BrokerSign(
    withCounterparty.TxBlob, brokerWallet);
await client.SubmitRequest(fullySigned.TxBlob);
```

> **Важно:** Не используйте `brokerWallet.Sign()` для частично подписанного LoanSet blob — он не обрабатывает `CounterpartySignature` корректно. Всегда используйте `LoanSigningHelper.BrokerSign()` для паттерна V3.

### Заёмщик с мультиподписью (Counterparty со SignerList)

Когда заёмщик — мультиподписной аккаунт, `CounterpartySignature` принимает мультиподписную форму: пустой `SigningPubKey` и массив `Signers`, который нода сверяет со SignerList **контрагента** под мультиподписным префиксом контрагента: начиная с `fixCleanup3_4_0` это не тот префикс, что у `tx.Signers`. Каждый подписант из списка заёмщика подписывает стандартным вызовом мультиподписи — SDK видит, что брокер подписывает одиночно, и определяет сторону сам, — а композитор расставляет записи по секциям.

```csharp
// Комиссия покрывает по одной базовой комиссии на каждого подписанта контрагента
// (rippled LoanSet::calculateBaseFee), поэтому autofill выполняется с числом подписантов до подписания
Dictionary<string, object> autofilled = await client.Autofill(loanTx.ToDictionary(), signersCount: 2);
JsonObject prepared = LoanSigningHelper.PrepareForSigning(
    JsonNode.Parse(JsonSerializer.Serialize(autofilled, XrplJsonOptions.Default)).AsObject(), brokerWallet);
var preparedDict = JsonSerializer.Deserialize<Dictionary<string, object>>(prepared.ToJsonString(), XrplJsonOptions.Default);

// Брокер: одиночная основная подпись. Подписанты заёмщика: переносимые записи мультиподписи
SignatureResult brokerPart = brokerWallet.Sign(new Dictionary<string, object>(preparedDict));
SignatureResult part1 = signer1.Sign(new Dictionary<string, object>(preparedDict), multisign: true);
SignatureResult part2 = signer2.Sign(new Dictionary<string, object>(preparedDict), multisign: true);

// По данным реестра: композитор находит подписантов в SignerList контрагента
// и заранее проверяет кворум по весам
SignatureResult composed = await client.ComposeSignatures(new[] { brokerPart.TxBlob, part1.TxBlob, part2.TxBlob });

// Оффлайн: подписанты заёмщика перечисляются явно
SignatureResult offline = LoanSigningHelper.CombineLoanSignatures(
    new[] { brokerPart.TxBlob, part1.TxBlob, part2.TxBlob },
    new[] { signer1.ClassicAddress, signer2.ClassicAddress });

await client.SubmitRequest(composed.TxBlob);
```

Подписант, входящий более чем в один SignerList (брокера, спонсора, заёмщика), для композитора по данным реестра является ошибкой; в таком случае необходимо собирать подписи оффлайн с явным указанием сторон.

### Ключевые моменты

- Обе стороны подписывают **одну и ту же транзакцию** (сериализованную для подписи, без полей подписей), каждая под hash-префиксом своей роли
- Прообраз подписи использует `SigningPubKey` **брокера** (отправляющий аккаунт)
- `CounterpartySignature` — это STObject с `isSigningField = false` — он исключён из прообраза подписи
- `Autofill` автоматически рассчитывает корректную комиссию для LoanSet (включая overhead CounterpartySignature ~150 байт)

---

## Объекты реестра

Протокол кредитования создаёт следующие объекты реестра:

| Объект | Описание | Создаётся транзакцией |
|--------|----------|----------------------|
| `Vault` | Хранит активы для кредитования | `VaultCreate` |
| `LoanBroker` | Кредитная организация с параметрами и покрытием | `LoanBrokerSet` |
| `Loan` | Активный кредит между брокером и заёмщиком | `LoanSet` |

### Поля LoanBroker

| Поле | Тип | Описание |
|------|-----|----------|
| `Account` | AccountID | Аккаунт брокера |
| `Asset` | Issue | Основной актив кредитования |
| `Asset2` | Issue | Вторичный актив (залог) |
| `CoverAvailable` | Number | Доступное покрытие |
| `AssetsAvailable` | Number | Доступные активы для кредитования |
| `AssetsTotal` | Number | Общее количество активов в хранилище |
| `DebtTotal` | Number | Общий непогашенный долг |
| `DebtMaximum` | Number | Максимально допустимый долг |
| `CoverRateMinimum` | UInt32 | Минимальная ставка покрытия (15000 = 150%) |
| `CoverRateLiquidation` | UInt32 | Порог ликвидации |
| `ManagementFeeRate` | UInt16 | Ставка комиссии (0-10000 базисных пунктов) |

### Поля Loan

| Поле | Тип | Описание |
|------|-----|----------|
| `Borrower` | AccountID | Аккаунт заёмщика |
| `LoanBrokerID` | Hash256 | Ссылка на кредитного брокера |
| `LoanSequence` | UInt32 | Порядковый номер в рамках брокера |
| `PrincipalOutstanding` | Number | Остаток основной суммы |
| `TotalValueOutstanding` | Number | Общая задолженность |
| `PeriodicPayment` | Number | Сумма платежа за интервал |
| `InterestRate` | UInt32 | Годовая процентная ставка |
| `PaymentInterval` | UInt32 | Интервал между платежами (секунды) |
| `GracePeriod` | UInt32 | Отсрочка до начисления пеней (секунды) |
| `PaymentRemaining` | UInt32 | Оставшиеся платежи |
| `StartDate` | UInt32 | Начало кредита (Ripple epoch) |

> `Counterparty`, `PrincipalRequested` и `PaymentTotal` — поля **транзакции `LoanSet`**, а не объекта `Loan`. Запрошенную сумму rippled записывает в `PrincipalOutstanding`, поэтому у ledger-объекта поля `PrincipalRequested` нет — как нет его и у `LOLoan`.

### Запрос состояния кредита

Используйте `account_objects` для получения кредитов аккаунта:

```csharp
using Xrpl.Models.Methods;

var request = new AccountObjectsRequest(walletBorrower.ClassicAddress);
var response = await client.AccountObjects(request);

foreach (var obj in response.AccountObjectList)
{
    if (obj.LedgerEntryType == LedgerEntryType.Loan)
    {
        Console.WriteLine($"Кредит: {obj}");
    }
}
```

---

## Тип Number

Поля кредитования используют тип `Number` XRPL вместо `Amount`. Тип `Number` — 12-байтовый формат:

- **8 байт** — знаковая int64 мантисса (big-endian)
- **4 байта** — знаковая int32 экспонента (big-endian)

Фактическое значение = `мантисса × 10^экспонента`.

### Нормализация

Ненулевые значения нормализуются так, чтобы мантисса была в диапазоне `[10^18, long.MaxValue]`. Ноль представляется как `мантисса=0, экспонента=Int32.MinValue`.

### Пример

Значение `10000000000000` (10^13) нормализуется до:
- Мантисса: `1000000000000000000` (10^18)
- Экспонента: `-5`
- Бинарное представление: `0x0DE0B6B3A7640000 FFFFFFFB` (12 байт)

### В моделях транзакций

Поля типа Number представлены как `string` в C#-моделях (например, `PrincipalRequested = "10000000"`). Бинарный кодек автоматически выполняет нормализацию и сериализацию.

---

## Типичные ошибки

| Код ошибки | Причина | Решение |
|-----------|---------|---------|
| `tecINSUFFICIENT_FUNDS` | В хранилище брокера недостаточно средств | Внесите больше активов через `VaultDeposit` |
| `tecHAS_OBLIGATIONS` | Нельзя удалить кредит с непогашенным остатком | Полностью погасите кредит через `LoanPay` |
| `tecNO_ENTRY` | LoanBrokerID или LoanID не найден | Проверьте корректность ID |
| `tecNO_PERMISSION` | Действие не разрешено (например, переплата без флага) | Проверьте права аккаунта |
| `tecINSUFFICIENT_PAYMENT` | Сумма платежа слишком мала | Увеличьте сумму платежа |
| `temBAD_SIGNER` | Отсутствует или некорректна CounterpartySignature | Убедитесь, что заёмщик совместно подписал LoanSet |
| `telINSUF_FEE_P` | Комиссия слишком низкая после добавления CounterpartySignature | Повторно вызовите Autofill или увеличьте комиссию перед отправкой |
| `invalid SerialIter geti32` | Ошибка кодирования типа Number | Убедитесь, что поля Number — 12 байт (8 мантисса + 4 экспонента) |

---

## Лучшие практики

1. **Пополните хранилище перед выдачей кредитов** — создайте хранилище, внесите активы (`VaultDeposit`), создайте брокера (`LoanBrokerSet`), внесите покрытие (`LoanBrokerCoverDeposit`), затем создавайте кредиты.

2. **Autofill учитывает LoanSet fee** — `Autofill` автоматически рассчитывает корректную комиссию, включая overhead `CounterpartySignature`. Ручная корректировка комиссии не требуется.

3. **Фильтруйте по LedgerEntryType при извлечении ID** — `GetCreatedObjectId` должен фильтровать по конкретному типу (`LedgerEntryType.Vault`, `LedgerEntryType.LoanBroker`, `LedgerEntryType.Loan`), чтобы не захватить `DirectoryNode`.

4. **Полностью погасите перед удалением** — `LoanDelete` возвращает `tecHAS_OBLIGATIONS`, если остаток не нулевой. Используйте `LoanPay` для погашения.

5. **Используйте разумные значения PrincipalRequested** — убедитесь, что в хранилище достаточно активов для запрашиваемой суммы.

6. **Проверяйте все результаты** — всегда проверяйте `TransactionResult`:
   ```csharp
   if (result.Meta?.TransactionResult != "tesSUCCESS")
       throw new Exception($"Транзакция не удалась: {result.Meta?.TransactionResult}");
   ```

7. **Флаги LoanManage взаимоисключающие** — устанавливайте только один из `tfLoanDefault`, `tfLoanImpair` или `tfLoanUnimpair`.

8. **Флаги LoanPay взаимоисключающие** — `tfLoanOverpayment`, `tfLoanFullPayment` и `tfLoanLatePayment` нельзя комбинировать. `tfLoanOverpayment` может требовать специальной настройки брокера/кредита.

9. **Тестирование на standalone** — поправка LendingProtocol должна быть включена на rippled 3.1.0+:
   ```json
   { "command": "feature", "feature": "LendingProtocol", "vetoed": false }
   ```

---

## Связанные ресурсы

- [Спецификация XLS-66d](https://github.com/XRPLF/XRPL-Standards/tree/master/XLS-0066d-lending-protocol)
- [Спецификация Vault (XLS-65d)](https://github.com/XRPLF/XRPL-Standards/tree/master/XLS-0065d-vault)
- [Документация XRPL](https://xrpl.org/docs/)
