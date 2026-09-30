# Аудит качества кода и покрытия тестами

**Исторический статус:** Q0.1–Q0.6 завершены; [исправления и проверки](Q0-Tooling-Hardening.md). Формулировка «ещё не исправлены» ниже описывает момент исходного аудита. Актуальный [аудит перед P6](P5-Code-Quality-and-Coverage-Audit.md) содержит новые замечания Q1.


Дата: 2026-09-29. База: текущая локальная реализация P1, user ABI v35 / boot ABI v3, .NET 10.0.8. Изменения не опубликованы.

**Вывод:** подтверждённый запуск runtime/GC сохраняется, но следующей работой должен стать небольшой пакет исправлений инфраструктуры проверок — Q0 в корневом PLAN.md. Найдены четыре воспроизводимых дефекта host tooling и один пробел CI. Нового подтверждённого дефекта kernel/GC в просмотренных путях не найдено; это не заключение об отсутствии всех дефектов проекта.

## Границы аудита

Проведён риск-ориентированный просмотр kernel context/suspend/reference/exception и dynamic-memory операций, PE validator, native heap/events/waits, runtime attach/hijack/GC walk, worker fixture, build/runner/provenance и обоих CI workflows. Это углублённая проверка критических путей и инвентаризация покрытия, а не построчное ревью всех накопленных изменений или формальное доказательство безопасности.

Production-код, ABI и existing tests в этом аудите не изменены. Новые диагностические стенды и результаты находятся в ignored `artifacts/quality-audit/`. Найденные дефекты ниже **ещё не исправлены**.

## Подтверждённые проблемы

| ID | Приоритет | Место | Проблема и последствие |
| --- | --- | --- | --- |
| Q0.1 | Высокий для надёжности проверок | [Processes.cs](../../tools/WitOS.Dev/Processes.cs), строки 33–42 | Deadline ограничивает ожидание родительского процесса, но не последующее чтение stdout/stderr. Потомок может удерживать перенаправленные pipes после выхода родителя. Воспроизведение: timeout 1 s, возврат через примерно 3,18 s, `TimedOut=false`, exit 0. При постоянно живом потомке ожидание streams не имеет заданного deadline. |
| Q0.2 | Средний | [DevTool.cs](../../tools/WitOS.Dev/DevTool.cs), строки 71–78 и 281 | Старый `acceptance.json` удаляется только при входе в BootRuntimeMatrixAsync, после build/source stages. Если новая попытка падает раньше, previous success остаётся на прежнем месте. CLI корректно возвращает ошибку; ошибочным становится вывод потребителя, который считает этот файл результатом последней попытки. |
| Q0.3 | Средний | [DevTool.cs](../../tools/WitOS.Dev/DevTool.cs), строки 344–346 и 381–390 | Managed gate проверяет одну последовательность первых вхождений маркеров во всём логе. Не требует отдельного полного workload evidence для каждого из двух image bases. Удаление только второго workload-маркера из действительного лога оставляет MarkersInOrder, ValidateUsers и ValidateScheduler успешными. Поэтому автоматически заявленные 2 executions / 26 lifecycles на профиль не полностью защищены самим parser. |
| Q0.4 | Средний | [RuntimeGuestDriver.cs](../../tools/WitOS.Dev/RuntimeGuestDriver.cs), строка 26 | Извлечение compile profile через `[^\s]+` не разбирает quoted arguments. `-I"C:\Workspace With Spaces\src"` превращается в `-I"C:\Workspace`. Сборка fixture не воспроизводима в workspace с пробелами, хотя прочие команды используют ArgumentList. |
| Q0.5 | Средний, coverage gap | [nativeaot.yml](../../.github/workflows/nativeaot.yml), оба paths filter | Нет `src/Boot.Uefi/**`. Изменение loader/entropy/firmware handoff запускает общий kernel workflow, но может не запустить NativeAOT/managed integration. Kernel workflow выполняет `test`, а не полный runtime workload. |

Q0.1–Q0.4 проверены executable probes, Q0.5 — сопоставлением обоих workflow и входов build pipeline. Это дефекты инструментария/автоматического контроля, а не обнаруженная компрометация гостевого ядра.

### Как воспроизведено

`artifacts/quality-audit/Audit.csproj` загружает настоящий WitOS.Dev.dll и вызывает его методы через reflection:

- `Processes.RunAsync`: короткоживущий launcher порождает процесс с трёхсекундной задержкой и унаследованными handles. Вызывается deadline 1 s; фиксируются elapsed, TimedOut и exit code.
- `DevTool.RunAsync(runtime-boot-run)`: изолированный root с отсутствующим обязательным header и копией предыдущего acceptance. Команда возвращает 1, acceptance остаётся byte-for-byte прежним. Рабочий acceptance проекта не изменялся.
- Pattern извлекается из текущего RuntimeGuestDriver.cs; на quoted include проверяется фактический результат Regex.
- Реальные MarkersInOrder/ValidateUsers/ValidateScheduler получают копию guest serial log с удалённым вторым workload-маркером. Исходный лог не изменяется. Это mutation-тест host parser, не новый QEMU-прогон.

Результаты: `tooling-repros.json`, `marker-repro.json`, `run.log`. Сообщение об отсутствующем header в run.log — ожидаемое воспроизведение Q0.2.

## Что проверено дополнительно

Свежий `dotnet build WitOS.slnx --configuration Release` прошёл: 0 warnings / 0 errors (`release-build.log`).

Настоящий [src/Kernel/pe.c](../../src/Kernel/pe.c) собран в hosted C harness. Проверены 527 входов: исходный runtime PE, 257 коротких префиксов, 13 усечений хвоста и 256 детерминированных мутаций первого KiB. Каждый переданный диапазон заканчивается непосредственно перед PAGE_NOACCESS, а его доступные страницы readonly. Seed: `0x57314A29`. Результат: 156 принятых / 371 отклонённый вход; падений и записи в readonly input не обнаружено (`pe-build.log`, `pe-run.log`, `pe-audit.c`).

Принятая мутация не обязательно ошибка: часть изменяемых header/DOS bytes не влияет на допустимость образа. Стенд проверяет bounded memory safety этой выборки, а не semantic equivalence каждого принятого PE. Он не покрывает все unwind/relocation/TLS combinations, чтения до начала input внутри mapped padding и не заменяет coverage-guided fuzzing или sanitizers.

Полные QEMU matrices в этом аудите заново не запускались: production runtime/kernel/runner не менялись. Предыдущая [финальная приёмка P1](P1-Completion-Audit.md) и её hash-linked evidence остаются отдельным источником guest evidence; новая работа добавила проверки самого tooling и hosted parser.

## Оценка покрытия

Процент line/branch coverage **не измерен**. В просмотренной конфигурации нет настроенной общей coverage-инструментации. Число PASS-групп, векторов или lifecycle нельзя превращать в процент покрытия.

| Область | Существующее доказательство | Что остаётся непроверенным / следующий тест |
| --- | --- | --- |
| Kernel и native adapters | 20 boot-сценариев; 4 runtime-профиля по 284 User-группы / 66 ожидаемых faults; права, whole-buffer validation, rollback, поколения handles | Генерируемые последовательности reserve/commit/protect/reset/free и waits/close/reuse; систематический отказ на N-й операции |
| PE/unwind | Реальные archived objects, Windows differential references, malformed metadata и guard cases; новый 527-case hosted audit | Coverage-guided corpus для headers, TLS, relocations, unwind chains/scopes; sanitizer runs и сохранённые минимальные regression inputs |
| Runtime lifecycle/GC | 13 workers × 4 профиля × 2 bases = 104 lifecycle; actual FixAllocContext, register roots, compacting GC, service guard и GC внутри TLS cleanup | Это небольшой набор сценариев, повторённый на профилях; длительный stress и вариации расписания отсутствуют в основном workload |
| Конкурентный detach | Fixture действительно запускает workers совместно и GC во время TLS destructor | После concurrent GC shutdown observers специально сериализованы (`worker_lifecycle.cpp`, строка 245). Это не тест одновременного detach нескольких threads и всех гонок attach/detach/GC |
| Hijack | Наблюдался реальный context redirection; отказ active service frame с сохранением контекста/error state | Return-address fallback не форсировался; failure injection Get/SetContext, suspend/resume и отсутствие context handle в полном runtime |
| Recovery и fault policy | Native отказные сценарии сильные; invalid handoff и полный teardown | Managed OOM/init-failure, runtime exception translation, attached-thread raw exit/fault и последующий GC — P3.5/P3.6/P3.8, затем P5 |
| Host tooling | Host build и end-to-end commands | Нет закреплённых regression tests на обнаруженные deadline/provenance/log-parser/argv ошибки; текущие reproductions нужно перенести из artifacts в versioned tests |
| CI | Kernel и NativeAOT workflows разделены, source/package pins и guest matrix настроены | Исправить paths dependency; последний P1 workflow ещё не подтверждён на GitHub, поскольку изменения не публиковались |

## Качество и сопровождаемость

Сильные стороны: kernel-owned identity/stack bounds; проверка полных диапазонов до записи; generation-bearing handles; разделение reserve/commit; фиксация ownership no-access страниц; строгая линковка и canonical source pins; сопоставление archived objects; negative tests с проверкой сохранности выходов; source-built Windows references.

Основной долг — концентрация orchestration и критериев доказательства в крупных методах и вручную синхронизируемых списках. DevTool.cs — 508 строк, user.c — 816, user_space.c — 410; сами размеры не являются дефектом, но длинные source/marker inventories и извлечение compile arguments из строки повышают риск расхождения producer/consumer. Выносить стоит прежде всего manifest lifecycle, типизированный build profile и parser результатов. Массовое косметическое переписывание kernel перед новыми acceptance cases не рекомендовано.

Дополнительная измерительная задача: `decommit_range` перезапускает обход ownership records после unmap. Алгоритм ограничен quota, но имеет квадратичный worst case; после роста лимита с 128 до 2048 pages надо измерить максимальное время IF-disabled memory-call. Это найденный performance risk, а не доказанное нарушение deadline или требование немедленно менять алгоритм.

## Следующий порядок работ

1. **Q0:** одним пакетом исправить четыре tooling дефекта и CI paths; закрепить воспроизведения в repo tests. Дедлайн должен охватывать exit и drain с определённой политикой потомков; latest-attempt status должен быть отделён от last-success history; managed evidence — проверяться отдельно на каждом base; compiler arguments — передаваться структурно или разбираться корректным Windows argv parser.
2. Прогнать быстрые host regression tests, затем один полный обязательный pipeline на стабильном пакете изменений. Не перезапускать дорогую source/QEMU матрицу после каждого редакционного изменения.
3. **P3.5 → P3.6/P3.8:** runtime exception/fault translation, детерминированные отказы allocation/initialization и явная политика abrupt termination. Для raw exit нельзя подразумевать, что ThreadStore автоматически удалил запись до освобождения TLS/stack.
4. **P5:** совместить реальные managed exceptions, finalizers, Thread/monitor и GC в одной acceptance suite; добавить bounded stress/repeat режим.
5. **P6:** CoreCLR/JIT и portable DLL без AOT-пересборки. RFC0018/19/20 прочитаны как Draft v0.1: обязательность .NET/CoreCLR сохранена; WASM/JS/browser/GUI/PowerShell не становятся зависимостями текущего bring-up.

P1 остаётся исторически подтверждённым milestone для описанного workload. Найденные проблемы не отменяют сохранённые два запуска на каждом base, но означают, что текущая автоматическая система приёмки требует Q0 до дальнейшего расширения runtime.