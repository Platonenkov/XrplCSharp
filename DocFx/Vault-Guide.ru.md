# Руководство по Vault (XLS-65d)

Руководство по использованию XRPL Single Asset Vault с SDK XrplCSharp. Vault позволяет объединять активы (XRP, IOU-токены или MPT) и выпускать доли (shares) для вкладчиков пропорционально их вкладу.

> **Примечание:** Vault требует поправку Single Asset Vault (XLS-65d). Функция находится в статусе draft и может измениться.

## Содержание

- [Обзор](#обзор)
- [Ключевые концепции](#ключевые-концепции)
- [Типы транзакций](#типы-транзакций)
- [Пошаговое руководство](#пошаговое-руководство)
- [Формат поля Data](#формат-поля-data)
- [Ledger-объект: Vault](#ledger-объект-vault)
- [Частые ошибки](#частые-ошибки)
- [Лучшие практики](#лучшие-практики)

---

## Обзор

Vault — это ledger-структура, которая хранит один тип актива и выпускает доли (в виде MPT-токенов) для вкладчиков. При внесении активов вкладчик получает доли пропорционально вкладу. Доли можно обменять обратно на базовые активы.

```
Вкладчик A                         Vault (псевдо-аккаунт)
┌──────────────────┐              ┌──────────────────────────┐
│  Депозит 100 XRP │ ──────────►  │  Asset: XRP              │
│  Получает доли   │ ◄──────────  │  AssetsTotal: 300 XRP    │
└──────────────────┘              │  Shares (MPT): выпущены  │
                                  │  Owner: rBroker...       │
Вкладчик B                       │  ShareMPTID: 000ABC...   │
┌──────────────────┐              └──────────────────────────┘
│  Депозит 200 XRP │ ──────────►
│  Получает доли   │ ◄──────────
└──────────────────┘
```

При создании vault создаются:
- **Псевдо-аккаунт**, который хранит объединённые активы
- **MPTokenIssuance** для долей vault

---

## Ключевые концепции

### Доли Vault (MPT)

При внесении активов vault выпускает доли в виде MPT-токенов. Количество долей зависит от поля Scale и текущего курса обмена.

**Расчёт долей:** Активы умножаются на `10^Scale` для преобразования дробных значений в целые числа. Например, при `Scale = 6` внесение 20.3 единиц создаёт 20 300 000 долей.

- **XRP и MPT vault:** фиксированный Scale = 0 (1 единица актива = 1 доля)
- **Trust line token vault:** Scale от 0 до 18 (по умолчанию 6)

### Политика вывода (WithdrawalPolicy)

Определяет правила вывода:
- `0x0001` (`vaultStrategyFirstComeFirstServe`) — вкладчики могут выкупить любое количество активов при наличии достаточного числа долей

### Вид vault (LendingProtocolV1_1)

По умолчанию vault открытый (open-ended): внесение и вывод возможны в любой момент. С поправкой LendingProtocolV1_1 vault можно создать закрытым (closed-ended), с тремя фазами, зафиксированными при создании:

| Фаза | Заканчивается | Внесение | Вывод |
|------|---------------|----------|-------|
| Subscription | `SubscriptionDate` (включительно) | разрешено | разрешён |
| Investment | `RedemptionDate` | отклоняется (`tecEXPIRED`) | отклоняется (`tecTOO_SOON`) |
| Redemption | никогда | отклоняется (`tecEXPIRED`) | разрешён |

`VaultKind`, `SubscriptionDate` и `RedemptionDate` задаются в `VaultCreate` и позже не меняются. Закрытому vault нужны обе даты, причём redemption не раньше чем через минуту и строго раньше чем через тридцать лет после subscription; открытый vault не может нести ни одной. В моделях даты представлены как `DateTime?`, по сети передаются секундами от Ripple Epoch.

```csharp
using Xrpl.Models.Ledger;   // VaultKind

VaultCreate vaultTx = new VaultCreate
{
    Account = wallet.ClassicAddress,
    Asset = new IssuedCurrency { Currency = "XRP" },
    VaultKind = (uint)VaultKind.ClosedEnded,
    SubscriptionDate = DateTime.UtcNow.AddDays(7),
    RedemptionDate = DateTime.UtcNow.AddDays(97),
};
```

Узел без поправки отклоняет `VaultCreate` с любым из трёх полей (`temDISABLED`). В ledger-объекте `VaultKind` отсутствует у открытого vault независимо от того, создан он до поправки или после.

### Флаги Vault

Устанавливаются только при создании через `VaultCreate`:
- **tfVaultPrivate** (`0x00010000`) — ограничивает доступ аккаунтами с учётными данными в указанном Permissioned Domain
- **tfVaultShareNonTransferable** (`0x00020000`) — делает доли непередаваемыми между аккаунтами

### Нереализованные убытки (LossUnrealized)

Поле `LossUnrealized` отслеживает потенциальные убытки (например, от clawback). При наличии нереализованных убытков стоимость погашения каждой доли пропорционально уменьшается.

---

## Типы транзакций

| Транзакция | Назначение | Кто отправляет |
|-----------|-----------|---------------|
| `VaultCreate` | Создание нового vault | Владелец vault |
| `VaultDeposit` | Внесение активов в vault | Любой аккаунт |
| `VaultWithdraw` | Обмен долей на активы | Любой держатель долей |
| `VaultSet` | Обновление метаданных/настроек | Владелец vault |
| `VaultDelete` | Удаление пустого vault | Владелец vault |
| `VaultClawback` | Clawback активов у держателя | Эмитент актива |

---

## Пошаговое руководство

### 1. Создание Vault

```csharp
using Xrpl.Models.Transactions;
using Xrpl.Models.Common;
using Xrpl.Sugar;
using static Xrpl.Models.Common.Common;

// Создание XRP vault
VaultCreate vaultTx = new VaultCreate
{
    Account = wallet.ClassicAddress,
    Asset = new IssuedCurrency { Currency = "XRP" },
};
vaultTx = await client.Autofill(vaultTx);
TransactionSummary result = await client.SubmitAndWait(vaultTx, wallet, true);

// Извлечение VaultID из метаданных
string vaultId = GetCreatedObjectId(result, LedgerEntryType.Vault);
```

### 2. Создание Vault с дополнительными параметрами

```csharp
VaultCreate vaultTx = new VaultCreate
{
    Account = wallet.ClassicAddress,
    Asset = new IssuedCurrency { Currency = "XRP" },
    AssetsMaximum = "1000000000",           // макс. 1000 XRP (в drops)
    MPTokenMetadata = "48656C6C6F",         // hex-метаданные для долей
    Data = "7B226E223A225465737420566175"
         + "6C74222C2277223A226578616D70"
         + "6C652E636F6D227D",              // hex-JSON метаданные vault
    WithdrawalPolicy = 1,                   // FirstComeFirstServe
    Scale = 6,                              // точность (только trust line токены)
    Flags = (uint)VaultCreateFlags.tfVaultShareNonTransferable,
};
vaultTx = await client.Autofill(vaultTx);
TransactionSummary result = await client.SubmitAndWait(vaultTx, wallet, true);
```

### 3. Внесение активов

Любой аккаунт может внести активы:

```csharp
VaultDeposit depositTx = new VaultDeposit
{
    Account = depositor.ClassicAddress,
    VaultID = vaultId,
    Amount = new Currency { Value = "50000000", CurrencyCode = "XRP" }, // 50 XRP
};
depositTx = await client.Autofill(depositTx);
await client.SubmitAndWait(depositTx, depositor, true);
```

### 4. Вывод активов

Любой держатель долей может обменять их на активы:

**Вывод по количеству активов:**

```csharp
VaultWithdraw withdrawTx = new VaultWithdraw
{
    Account = depositor.ClassicAddress,
    VaultID = vaultId,
    Amount = new Currency { Value = "25000000", CurrencyCode = "XRP" }, // 25 XRP
};
withdrawTx = await client.Autofill(withdrawTx);
await client.SubmitAndWait(withdrawTx, depositor, true);
```

**Вывод на другой аккаунт:**

```csharp
VaultWithdraw withdrawTx = new VaultWithdraw
{
    Account = depositor.ClassicAddress,
    VaultID = vaultId,
    Amount = new Currency { Value = "25000000", CurrencyCode = "XRP" },
    Destination = "rRecipient...",
    DestinationTag = 42,
};
```

> **Примечание:** Комиссия за перевод (transfer fee) не применяется к VaultWithdraw.

### 5. Обновление настроек Vault

Владелец может обновить изменяемые поля:

```csharp
VaultSet setTx = new VaultSet
{
    Account = wallet.ClassicAddress,
    VaultID = vaultId,
    AssetsMaximum = "2000000000",   // увеличить лимит до 2000 XRP
    Data = "7B226E223A2255706461746564227D",  // новые метаданные
};
setTx = await client.Autofill(setTx);
await client.SubmitAndWait(setTx, wallet, true);
```

> **Ограничение:** VaultSet может изменять только `Data`, `AssetsMaximum` и `DomainID`. Статус vault (публичный/приватный) является постоянным.

### 6. Удаление пустого Vault

Vault должен иметь нулевой баланс и не иметь выпущенных долей:

```csharp
VaultDelete deleteTx = new VaultDelete
{
    Account = wallet.ClassicAddress,
    VaultID = vaultId,
};
deleteTx = await client.Autofill(deleteTx);
await client.SubmitAndWait(deleteTx, wallet, true);
```

### 7. Clawback активов (только эмитент)

Эмитент актива может выполнить clawback у держателя долей vault. Clawback не применим к нативному XRP.

```csharp
VaultClawback clawbackTx = new VaultClawback
{
    Account = issuer.ClassicAddress,
    VaultID = vaultId,
    Holder = "rShareHolder...",
    Amount = new Currency
    {
        Value = "100",
        CurrencyCode = "USD",
        Issuer = issuer.ClassicAddress,
    },
};
clawbackTx = await client.Autofill(clawbackTx);
await client.SubmitAndWait(clawbackTx, issuer, true);
```

> При `Amount = 0` эмитент забирает все средства, вплоть до общего количества долей держателя.

---

## Формат поля Data

Поле `Data` хранит произвольные метаданные в виде hex-строки (макс. 256 байт). SDK предоставляет класс `VaultDataFormat` для рекомендуемой JSON-структуры:

```csharp
using Xrpl.Models.Ledger;

// Создание структурированных данных
VaultDataFormat data = new VaultDataFormat
{
    Name = "My Investment Vault",
    Website = "https://example.com",
};

// Конвертация в hex для VaultCreate/VaultSet
string hex = VaultDataFormat.ToHex(data);

// Парсинг hex из ledger-объекта Vault
LOVault vault = ...; // получен через ledger_entry
VaultDataFormat parsed = vault.DataParsed;  // [JsonIgnore] свойство-помощник
string rawUtf8 = vault.DataRaw;            // [JsonIgnore] сырая UTF-8 строка
```

JSON-структура `VaultDataFormat`: `{"n":"name","w":"website"}` — без пробелов, hex-кодированная.

---

## Ledger-объект: Vault

Получение Vault ledger entry через `ledger_entry`:

```csharp
using Xrpl.Models.Methods;
using Xrpl.Models.Ledger;

LedgerEntryRequest request = new LedgerEntryRequest { Index = vaultId };
LedgerEntryResponse response = await client.LedgerEntry(request);

LOVault vault = (LOVault)response.Node;

Console.WriteLine($"Owner: {vault.Owner}");
Console.WriteLine($"Asset: {vault.Asset}");
Console.WriteLine($"AssetsTotal: {vault.AssetsTotal}");
Console.WriteLine($"AssetsAvailable: {vault.AssetsAvailable}");
Console.WriteLine($"ShareMPTID: {vault.ShareMPTID}");
Console.WriteLine($"Scale: {vault.Scale}");
Console.WriteLine($"Metadata: {vault.DataParsed?.Name}");
```

### Ключевые поля LOVault

| Поле | Тип | Описание |
|------|-----|----------|
| `Account` | string | Адрес псевдо-аккаунта vault |
| `Owner` | string | Адрес владельца vault |
| `Asset` | IssuedCurrency | Хранимый актив |
| `AssetsTotal` | string | Общая стоимость vault (тип Number) |
| `AssetsAvailable` | string | Доступные активы (тип Number) |
| `AssetsMaximum` | string | Лимит хранения, 0 = без ограничений (тип Number) |
| `LossUnrealized` | string | Нереализованные убытки (тип Number) |
| `ShareMPTID` | string | ID MPTokenIssuance для долей |
| `WithdrawalPolicy` | uint? | Стратегия вывода |
| `Scale` | uint? | Точность при расчёте долей |
| `LEVersion` | uint? | Версия схемы (`VaultVersion`), отсутствует у vault, созданных до cash-basis учёта |
| `VaultKind` | uint? | `VaultKind.ClosedEnded` (1) для закрытого vault, иначе отсутствует |
| `SubscriptionDate` | DateTime? | Конец фазы subscription (только closed-ended) |
| `RedemptionDate` | DateTime? | Начало фазы redemption (только closed-ended) |
| `Data` | string | Hex-метаданные (макс. 256 байт) |
| `Sequence` | uint? | Sequence транзакции создания |

### Флаги Vault (LOVault)

```csharp
[Flags]
public enum VaultLedgerFlags : uint
{
    lsfVaultPrivate = 0x00010000,
}
```

---

## Частые ошибки

| Код ошибки | Причина |
|-----------|---------|
| `tecHAS_OBLIGATIONS` | Невозможно удалить vault с ненулевыми долями или активами |
| `tecNO_PERMISSION` | Не-владелец пытается выполнить VaultSet/VaultDelete, или не-эмитент — VaultClawback |
| `tecUNFUNDED` | Недостаточно средств для VaultDeposit |
| `tecINSUFFICIENT_FUNDS` | Недостаточно долей для VaultWithdraw |
| `tecFROZEN` | Trust line между псевдо-аккаунтом vault и эмитентом заморожена |
| `temMALFORMED` | Data превышает 256 байт или недопустимые значения полей |
| `tecOBJECT_NOT_FOUND` | VaultID не существует |

---

## Лучшие практики

1. **Всегда задавайте AssetsMaximum** — ограничивает риски и предотвращает неконтролируемые депозиты
2. **Используйте VaultDataFormat** — обеспечивает стандартную структуру метаданных, читаемую другими приложениями
3. **Проверяйте LossUnrealized** — перед выводом убедитесь, что vault не имеет нереализованных убытков
4. **Приватные vault для регулируемых активов** — используйте `tfVaultPrivate` с Permissioned Domain для KYC-gated vault
5. **Непередаваемые доли** — используйте `tfVaultShareNonTransferable`, когда доли не должны торговаться на вторичном рынке
6. **Scale для trust line токенов** — выбирайте Scale тщательно при создании (нельзя изменить позже). Больший Scale = точнее гранулярность, но больше числа долей
7. **Учитывайте clawback** — только эмитент актива может выполнить clawback, и только для не-XRP активов. Clawback создаёт нереализованные убытки для других вкладчиков
