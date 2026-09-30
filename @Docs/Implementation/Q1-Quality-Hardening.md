# Q1 — исправления качества и покрытия перед P6

Дата: 2026-09-30. User ABI v37 / boot ABI v3, upstream .NET 10.0.8 сохранены. **Q1.1–Q1.6 завершены; все обязательные gates прошли.** Исходные находки сохранены в [аудите](P5-Code-Quality-and-Coverage-Audit.md).

## Реализация

| Пункт | Что изменено | Проверка |
| --- | --- | --- |
| Q1.1 | `RuntimeBootEnvelope` разбирает полную последовательность native-fault, stack-fault, init-failure, abrupt и positive blocks. Fatal/verdict/counter не могут оказаться в другом блоке или продублироваться. Legacy kernel diagnostics до runtime boundary отделены. Upstream bookkeeping/InitDLL diagnostics допускаются только на своём месте. | Мутации synthetic fixture и четырёх сохранённых реальных serial logs; лишний fatal, конфликтующие counters, перенос verdict, дублирование/лишние отчёты до/после runtime. |
| Q1.2 | `Publish` только готовит evidence; commit происходит после успешного завершения action. Единственная commit point — atomic replace `current-run.json` schema 3 с указателем/hash последнего успеха. Остальные файлы — восстанавливаемые представления. | Write/rename failure для пяти путей до/после commit; реальный blocked status path; exception после Publish; interrupted restart; сохранение предыдущего успеха и исходной ошибки. |
| Q1.3 | Ожидание control callback ограничивает сам runner. Callback стартует отдельной task; token cancellation, закрытие input и наблюдение поздних исключений не заменяют forced process/job termination и подтверждение его завершения. | Асинхронный и синхронный callbacks игнорируют token; поздняя запись/ошибка после disposal; немедленная ошибка; прежние QMP/descendant/pipe tests. |
| Q1.4 | Второй реальный managed worker выполняет возвращающиеся вызовы с живыми object/array roots. Его собственный kernel stack lease запрещает context replacement, поэтому upstream `HijackCallback` использует return-address fallback. | Никакие runtime/OS результаты не подменяются. Отдельные native assertions требуют сначала redirect, затем return hijack без нового redirect. После GC проверены корни, результат возврата и orderly teardown. |
| Q1.5 | PE corpus расширен с 529 до 555 входов: добавлены 26 структурных verdict cases. Появились воспроизводимые LLVM branch coverage + ASan и отдельный libFuzzer/ASan lane. | 555 guarded inputs, точные ожидаемые результаты pdata/xdata/TLS/reloc мутаций; 500 bounded fuzz runs; source/binary hashes и машинные отчёты. |
| Q1.6 | Ограничен stdout/stderr и serial capture; native object groups выбираются по source names из проверяемого manifest вместо индексов; parser/publication выделены в читаемые функции; новые тесты сообщают конкретную стадию/мутацию. | Граница capture, обе переполненные streams, oversized serial, shuffled/missing/changed native manifests; прежние native image matrices. |

## Семантика публикации и завершения процессов

`current-run.json` — авторитетная запись, содержащая состояние попытки и `lastSuccess.file/sha256`. Читатель проверяет hash указанного immutable acceptance snapshot. Наличие только `acceptance.json` или `last-success.json` больше не является самостоятельным доказательством текущего успеха.

До commit любая ошибка оставляет предыдущий last-success. После commit ошибка обновления производного файла выдаёт warning; успех уже зафиксирован, а следующая попытка восстанавливает представления под `run.lock`. Если предыдущая попытка осталась `running`, recovery переводит её в `interrupted`, убирает незавершённое acceptance и сохраняет предыдущий committed success. Ошибка дополнительной диагностики не подменяет первоначальную ошибку. Это протокол атомарных файловых замен при process/I/O failures, не гарантия сохранности при физическом отказе носителя.

Runner сохраняет command deadline и дополнительный cleanup budget 5 s; control phase занимает максимум 1 s ожидания. Произвольный callback обязан в итоге завершиться: .NET cancellation не уничтожает выполняющийся пользовательский код. Runner не ждёт его бесконечно и наблюдает поздние faults. Потоки stdout/stderr ограничены каждый 8 Mi characters; serial evidence — 8 MiB на чтение. Превышение лимита завершает проверку ошибкой, а усечённые байты не принимаются как полный успешный протокол.

## Guest evidence fallback

Upstream `Thread::HijackCallback` проверяет safe point, пробует `Redirect()` и при неудаче вызывает настоящий `HijackReturnAddress`. В WitOS `THREAD_CONTEXT_SET` отвергает замену контекста leased stack. Fixture использует существующий self-owned lease; foreign suspension/resume сохраняются, а return-address rewrite выполняет сам runtime в user space.

Managed target регулярно возвращает object reference из noinline метода и сохраняет отдельный массив через compacting GC. Worker снимает lease после managed callback, затем проходит настоящие TLS/ThreadStore notifications и THREAD_COMPLETE. Это тест реального fallback в том же production runtime archive, а не отдельная сборка с отключённым redirect или успешная заглушка.

Положительный execution теперь требует **43 orderly worker completions** вместо 41: добавлены два native-hosted managed callbacks. Число стандартных managed `Thread` остаётся 28 на execution. Новая приёмка требует ненулевые **оба** hijack counters; исторический P5 acceptance не переписывается.

## Измеренное покрытие

| Исходник | Покрытые исходы ветвей LLVM | Строки |
| --- | --- | --- |
| `src/Kernel/pe.c` | **282 / 486 — 58,02%** | 254 / 334 — 76,05% |
| `src/Kernel/include/witos/unwind_metadata.h` | **132 / 228 — 57,89%** | 71 / 82 — 86,59% |

Оба исходника инструментированы в hosted native harness. Это начальная точка измерения выбранных parser-путей, не покрытие всего kernel, host tooling или upstream runtime. Часть непокрытых ветвей относится к plain PE profile, которого full-runtime corpus не вызывает; отдельно остаются редкие opcode/chain/quota/error combinations. C# line/branch coverage этим запуском не измерялось.

26 новых проверок имеют заранее заданный expected verdict; это отличается от старых случайных однобитных мутаций, проверяющих только допустимый status и отсутствие выхода за границы. Положительные controls проверяют допустимые изменения DOS stub/checksum. Отдельные Windows differential и guest unwind tests сохраняются.

Инструментальная сборка использует [официальный LLVM 20.1.8](https://github.com/llvm/llvm-project/releases/tag/llvmorg-20.1.8), installer SHA-256 `3197846a2b19063687dd56e93e34cd941e3548d907f23a6131571321bdf9fe7b`. Installer распаковывается в `.tools/`, не запускается. Coverage lane включает ASan; fuzz lane — libFuzzer + ASan, 500 runs, seed 1462848041, max input 1 MiB, per-input timeout 5 s, RSS 512 MiB, outer deadline 120 s. Это ограниченная smoke-проверка; долгий fuzz campaign и высокий процент покрытия не заявляются.

Оба lane включены в `.github/workflows/nativeaot.yml`; удалённый CI ещё не запускался. Команды и детали в [README тестов](../../tests/WitOS.Dev.Tests/README.md).

## Итоговые проверки

Финальная приёмка: `20260930T060327778-8bde0e145ca2483fb643a85a388e0ef7`.

- PE SHA-256: `b7eb786c9a8f6971bee08010968d0369c78d930185279f2f69193ebab4bac53e`.
- Общий hosted/guest managed object: `77afe23566260a63b6888b88e6af663f52f1b5500de7e5bc40d574f28d4926e2`.
- Release: 0 warnings / 0 errors; 29 host groups, 555 guarded PE inputs (159 accepted / 396 rejected), включая 26 structural verdict cases.
- Все 20 kernel/QEMU сценариев; timeout корректно остался `TimedOut=true` при QMP exit 0.
- runtime-audit, runtime-probe, runtime-target, runtime-source, runtime-config и runtime-boot-run прошли. Все четыре native profiles сохранили по 66 ожидаемых contained faults.
- 16 managed guest executions, 64 combined cycles, 688 orderly worker completions. В каждом execution `Redirects > 0` и `ReturnHijacks > 0`; roots, returns и complete teardown проверены. Использовано 269–303 из прежних 3000 ticks.
- LLVM branch coverage + ASan corpus и 500 libFuzzer/ASan smoke runs прошли.

`artifacts/q1-completion/evidence.json` связывает hashes gates/исходников, оба hijack counters, current commit/projections, image/disk/hosted reference и фактические linked inputs. `verify.py` проверяет эти связи. Последняя повторная сборка выполнена после завершения активных host executable и не содержит предупреждений о file locks.

Стабильные отчёты: `artifacts/q1-completion/native-coverage.json`, `native-coverage-profile.json`, `native-fuzz.json`, `native-fuzz.log`. Generated binaries/downloads/logs остаются ignored. Production profile и стандартная CoreLib сохранены; P6 ещё не начат.
