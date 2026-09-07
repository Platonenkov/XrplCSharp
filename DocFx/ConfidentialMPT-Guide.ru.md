# Гайд: Конфиденциальные MPT (ConfidentialTransfer)

Как XrplCSharp SDK поддерживает конфиденциальные Multi-Purpose Tokens — балансы MPT, скрытые ElGamal-шифрованием и zero-knowledge-доказательствами, с опциональной видимостью для эмитента и аудитора.

> **Важно:** требуется амендмент `ConfidentialTransfer`. На середину 2026 он существует только в ветке rippled `develop` — не входит в релизы, не активен в mainnet/testnet. Фича в статусе draft и может меняться.
>
> **Границы SDK:** XrplCSharp — это **транспортный слой**: модели транзакций, бинарная сериализация, подписание и отправка. Зашифрованные суммы, коммитменты, blinding-факторы и ZK-доказательства для SDK — **непрозрачные hex-блобы**; их формирование требует внешнего prover'а (криптоинструментария от авторов протокола). Пока prover недоступен, возможно только негативное тестирование (см. [Тестирование](#тестирование)).

## Содержание

- [Обзор](#обзор)
- [Настройка выпуска](#настройка-выпуска)
- [Типы транзакций](#типы-транзакций)
- [Жизненный цикл баланса](#жизненный-цикл-баланса)
- [Объекты леджера](#объекты-леджера)
- [Тестирование](#тестирование)
- [Типичные ошибки](#типичные-ошибки)

---

## Обзор

Конфиденциальный MPT делит баланс держателя на **публичную** часть (обычный `MPTAmount`) и **конфиденциальную** (зашифрованную). Третьи стороны видят факт перевода, но не сумму. Эмитент и опциональный аудитор могут расшифровать суммы своими ключами — каждая конфиденциальная операция несёт сумму, зашифрованную отдельно под каждым релевантным ключом.

```text
публичный баланс ──Convert──► конфиденциальный ──Send──► inbox получателя
      ▲                            │      ▲                     │
      └────────ConvertBack─────────┘      └──────MergeInbox─────┘
```

Входящие конфиденциальные переводы попадают в **inbox** держателя и должны быть слиты в расходуемый конфиденциальный баланс транзакцией `ConfidentialMPTMergeInbox` — это защищает получателя от инвалидации его незавершённых доказательств отправителем.

---

## Настройка выпуска

Выпуск должен быть privacy-enabled и нести ElGamal-ключ эмитента (и опционально аудитора):

```csharp
var create = new MPTokenIssuanceCreate
{
    Account = issuer.ClassicAddress,
    // ...
    IssuerEncryptionKey = issuerElGamalPubKeyHex,
    AuditorEncryptionKey = auditorElGamalPubKeyHex,   // опционально
};
```

Для существующего выпуска приватность включается необратимо через `MPTokenIssuanceSet`:

```csharp
var set = new MPTokenIssuanceSet
{
    Account = issuer.ClassicAddress,
    MPTokenIssuanceID = issuanceId,
    Flags = MPTokenIssuanceSetFlags.tfMPTSetCanHoldConfidentialBalance,
    IssuerEncryptionKey = issuerElGamalPubKeyHex,
};
```

Правила preflight rippled (продублированы клиентской валидацией SDK):

- ненулевой `TransferFee` **несовместим** со включением конфиденциальных балансов (`temBAD_TRANSFER_FEE`);
- флаг `tifMPTCanHoldConfidentialBalance` в `ImmutableFlags` — при создании выпуска или в любой последующей транзакции — навсегда запрещает включение приватности;
- `AuditorEncryptionKey` требует наличия `IssuerEncryptionKey`.
- ключ регистрируется один раз. При активной поправке ConfidentialMPTKeyRotation `MPTokenIssuanceSet` с другим ключом заменяет его и увеличивает `IssuerKeyEpoch` / `AuditorKeyEpoch` на выпуске; текущий ключ отклоняется (`tecDUPLICATE`), а ключ аудитора можно зарегистрировать уже после ключа эмитента.

---

## Типы транзакций

| Транзакция | Назначение | Ключевые поля |
|---|---|---|
| `ConfidentialMPTConvert` | Публичный → конфиденциальный | `MPTAmount` (публичная сумма, decimal), `HolderEncryptionKey`, `HolderEncryptedAmount`, `IssuerEncryptedAmount`, `AuditorEncryptedAmount`, `BlindingFactor`, `ZKProof` |
| `ConfidentialMPTMergeInbox` | Слить inbox в расходуемый конфиденциальный баланс | `MPTokenIssuanceID` |
| `ConfidentialMPTConvertBack` | Конфиденциальный → публичный | зашифрованные суммы + `ZKProof` |
| `ConfidentialMPTSend` | Конфиденциальный перевод | `Destination`, `SenderEncryptedAmount`, `DestinationEncryptedAmount`, `IssuerEncryptedAmount`, `AuditorEncryptedAmount`, `AmountCommitment`, `BalanceCommitment`, `ZKProof`, опционально `CredentialIDs` |
| `ConfidentialMPTClawback` | Возврат конфиденциальных средств эмитентом | зашифрованные суммы + доказательство |

Все суммы, зашифрованные под ключами держателя/эмитента/аудитора, поставляет **prover**; SDK валидирует форму (hex-строки, обязательные поля) и сериализует их в подписываемый blob.

---

## Жизненный цикл баланса

1. **Convert**: держатель переводит часть публичного баланса в конфиденциальный домен. Публичный `MPTAmount` уменьшается; `ConfidentialOutstandingAmount` выпуска растёт.
2. **Send**: конфиденциальный перевод в inbox другого держателя. Коммитменты доказывают достаточность баланса, не раскрывая его.
3. **MergeInbox**: получатель сливает средства из inbox в расходуемый конфиденциальный баланс.
4. **ConvertBack**: держатель возвращает средства в публичный домен.
5. **Clawback** (эмитент, если разрешено): изымает конфиденциальные средства у держателя.

---

## Объекты леджера

- `LOMPTokenIssuance`: `IssuerEncryptionKey`, `AuditorEncryptionKey`, `IssuerKeyEpoch` и `AuditorKeyEpoch` (счётчики ротаций, отсутствуют до первой ротации), `ConfidentialOutstandingAmount` (decimal-строка — base-ten UInt64 поле), `ImmutableFlags`
- `LOMPToken`: поля конфиденциального баланса/inbox (зашифрованные блобы + счётчики)

---

## Тестирование

Без внешнего prover'а позитивный путь пройти нельзя. Что вместо этого делает интеграционный набор репозитория (`Tests/Xrpl.Tests/Integration/transactions/TestIConfidentialMPT.cs`):

- строит обычный MPT-выпуск на nightly-стенде (без флагов конфиденциальных балансов и ключей шифрования — позитивный privacy-путь требует внешнего prover'а);
- отправляет `ConfidentialMPTConvert` со структурно валидным, но криптографически фиктивным материалом доказательства;
- проверяет, что нода отвечает **доменным** вердиктом (любой `tem`/`tec` из логики ConfidentialTransfer), а не ошибкой парсинга — то есть сериализация SDK для проверяемой формы payload совместима с парсером ноды вплоть до доменной валидации.

```bash
docker compose -f .ci-config/docker-compose.batchv11.yml up -d --build
dotnet test Tests/Xrpl.Tests/Xrpl.Tests.csproj --settings test.runsettings --filter "TestIConfidentialMPT"
```

Тесты защищены `AmendmentGuard` и завершаются как inconclusive на нодах без амендмента.

---

## Типичные ошибки

| Ошибка | Значение |
|---|---|
| `temDISABLED` | Амендмент `ConfidentialTransfer` не активен |
| `temBAD_CIPHERTEXT` | Некорректный зашифрованный материал |
| `temBAD_TRANSFER_FEE` | Ненулевой `TransferFee` вместе со включением конфиденциальных балансов |
| `tecBAD_PROOF` | ZK-доказательство не проходит верификацию (один из возможных протокольных вердиктов для фиктивного prover-материала) |
| `terLOCKED` | Выпуск или баланс держателя заблокирован |

*English version: [ConfidentialMPT-Guide](ConfidentialMPT-Guide.md)*
