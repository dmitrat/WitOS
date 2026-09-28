# План запуска .NET в WitOS

Обновлено: **2026-09-28**. База: **WitOS 0.0.44**, commit `e619a14`, ABI v18, upstream .NET **10.0.8**.

**Сейчас: готова native-основа и часть адаптеров runtime; гостевой managed .NET ещё не запущен. Работа идёт на этапе P1 — полная линковка и настоящий startup-драйвер.**

Это основной файл отслеживания работ. Детали реализации и исторические результаты остаются в [`@Docs/Implementation/`](@Docs/Implementation/). План перечисляет известные обязательные блоки; интеграция может выявить новые подзадачи. Число символов линкера не равно числу задач или проценту готовности. Срок уточняем после P1, когда будет конкретный полный образ и измеренный объём оставшегося порта.

## Что считаем запуском .NET

| Рубеж | Проверяемый результат | Статус |
| --- | --- | --- |
| Первый гостевой .NET, P4 | Обычный NativeAOT executable со стандартной CoreLib проходит настоящий bootstrap, входит в managed `Main`, выделяет объекты и выполняет реальный GC с сохранением корней | Не достигнут |
| Полный M3, P5 | В одном гостевом runtime работают GC, finalization, managed exceptions и managed threads; проходят проверки жизненного цикла и отказов | Не достигнут |
| Цель совместимости, P6 / M6 | Upstream CoreCLR/JIT запускает обычную portable managed-сборку, собранную на другой ОС, без перекомпиляции под WitOS | Не достигнут |

NativeAOT — средство раннего запуска и реализации системных компонентов. Он не заменяет CoreCLR/JIT как контракт приложений. Не создаём собственную CoreLib, особый язык C# или обязательный WitOS TFM. Сохраняем обычные SDK, TFM, NuGet и managed semantics; см. [контракт совместимости](@Docs/RFC-0015-DotNet-Runtime-Port-and-Compatibility-Contract.md).

## Карта этапов

| Этап | Состояние | Условие завершения |
| --- | --- | --- |
| P0. Native-фундамент и стенд | Готов в текущем ограниченном профиле | Изоляция, native services и воспроизводимые проверки работают в госте |
| **P1. Полная линковка и startup-драйвер** | **В работе; ближайший рубеж** | Настоящий guest executable линкуется без unresolved symbols, Windows libraries и заглушек |
| P2. Загрузка полного образа и бюджеты | Частично: работает ограниченный PE loader | Гость безопасно загружает реальный runtime image и входит в его native entry |
| P3. Runtime threads, контексты, обход стеков и GC | Частично: готовы native-примитивы | Реальные runtime threads и collector согласованно работают, корни видимы GC |
| P4. Bootstrap и первый managed `Main` | Частично: отдельные startup-подсистемы | Минимальный managed workload с allocation + GC проходит внутри WitOS |
| P5. Полный M3 и регрессии | Не завершён; есть native prerequisites | Exceptions, finalization и managed threads проходят в одном runtime |
| P6. CoreCLR/JIT и переносимые приложения | Не начат как исполняемый гостевой порт | Неизменённые managed binaries проходят согласованную compatibility suite |

Порядок показывает зависимости, а не отдельные коммиты. P1–P3 потребуют итераций; P3 и P4 интегрируются вместе. Нельзя доказать GC/ThreadStore detach без настоящего collector. Если exception/context-инфраструктура нужна bootstrap или обходу корней, реализуем её в P3, а не откладываем до P5.

## P0. Что уже подтверждено

- [x] UEFI-загрузка, собственные page tables, прерывания/таймер, kernel context switching, ring-3 isolation и containment пользовательских faults.
- [x] Sparse user memory: reserve/commit/decommit/reset/protect/release, учёт ресурсов и rollback; ограниченные threads, raw FS TLS, static/dynamic compiler TLS.
- [x] Native joins, detached workers, events, deadlines, bounded WaitAny, memory-pressure notifications и idle/wakeup.
- [x] Загрузка ограниченного native PE с RX/RO/RW, relocations, защищённым startup descriptor и native C bootstrap.
- [x] Часть GC OS/PAL: память, discovery, clocks, events, mutex/Crst, native allocation, environment, strings, last-error и errno.
- [x] Native TLS destructors, atexit и private thread-exit notification; это ещё не managed shutdown или GC detach.
- [x] Реальные RhConfig/GCConfig и PalInit; отдельные upstream InterfaceDispatch/AllocHeap, RuntimeInstance, пустой ThreadStore и GC-special Thread record выполняются в native-пробах.
- [x] Native CPU/time helpers, `__chkstk`, CoreLib clock bindings, fatal diagnostics и GC affinity parsing выполняются в госте.
- [x] Pinned source audit, раздельные Windows-reference/WitOS native archives, строгая диагностика линковки и обычный standard-CoreLib executable как hosted reference.
- [x] На базе `e619a14`: 19 boot-сценариев; 4 runtime-прогона по 212 user groups и 54 ожидаемым contained faults; оба GitHub CI workflow успешны.

Ограничения P0 сохраняются. Ни один пункт этого списка сам по себе не означает запуска managed-кода. Полный перечень native-сервисов: [Next-Steps](@Docs/Implementation/Next-Steps.md).

## P1. Полная линковка и настоящий startup-драйвер

**Текущий блокер:** минимальный diagnostic link имеет **64** unresolved symbols; широкий reference workload — **70**. Root `wmain` уже настоящий upstream, но ещё не является корректной точкой входа из WitOS.

- [x] **P1.1** Получить воспроизводимый minimal standard-CoreLib workload и strict-link inventory с настоящими transport/TLS objects: [`NativeAotBoot`](experiments/NativeAotBoot/Program.cs), [readiness](@Docs/Implementation/NativeAot-Startup-Readiness.md).
- [ ] **P1.2 — следующий шаг.** Для каждой группы зависимостей ниже проследить реальные call sites и составить таблицу: обязательная реализация / допустимое upstream отключение необязательной возможности / оставшийся блокер. Зафиксировать первоначальный профиль: x64, один CPU, static image, workstation GC; возможность non-concurrent GC подтвердить реальной конфигурацией и исполнением.
- [ ] **P1.3** Закрыть необходимые CRT/compiler primitives: math/formatting, `_fltused`, security cookie/check и выбранный CFG-профиль. Инициализировать защиты до защищённого кода. Никаких фиктивных cookie/check/dispatch-функций.
- [ ] **P1.4** Определить источник случайности для фактически используемых runtime/security-путей. PRNG TLS constructor, часы и идентификатор потока не считать криптографической энтропией.
- [ ] **P1.5** Связать прямые CoreLib/native platform calls с реальными WitOS handles, memory, waits, identity и console services. Проверить значения, lifetime, ошибки, права и эквивалентность direct/import bindings.
- [ ] **P1.6** Реализовать нужные module/name/encoding/diagnostic paths. Для COM, Windows event log, dynamic library lookup и других необязательных Windows-механизмов принять явное решение по профилю. Не эмулировать всю Windows и не возвращать фиктивный успех.
- [ ] **P1.7** Закрыть выбранную GC OS policy: write-watch, large pages и debug break. Поддержать требуемое поведение либо доказать исключение опционального пути допустимыми upstream настройками; unsupported методы до этого остаются блокерами.
- [ ] **P1.8** Реализовать недостающие PAL startup/thread/context contracts совместно с P3, включая замену Windows COM/FLS startup assumptions и настоящий attach/shutdown.
- [ ] **P1.9** Построить guest entry thunk: validated handoff → публикация image/environment → compiler TLS и native constructors → настоящий upstream executable bootstrap → управляемое завершение. Согласовать calling convention, аргументы, symbol roots и сохранение необходимых metadata sections.
- [ ] **P1.10** Получить полный strict link реального guest-shaped executable. Проверить symbol map, imports, TLS и состав runtime/GC/CoreLib. Сохранять отдельный широкий inventory, чтобы уменьшение workload не скрывало обязательные зависимости.

**Готово, когда:** реальный guest executable линкуется; все выбранные runtime-зависимости имеют действительную реализацию или обоснованно исключены профилем. Нет `/FORCE`, Windows implementation libraries, fake GC/OS helpers или успеха, подменяющего unsupported behavior. Это ещё не доказательство исполнения в госте.

## P2. Загрузить полный runtime image

Зависит от образа P1; подготовка loader/context policy может идти раньше.

- [ ] **P2.1** Измерить именно WitOS image: mapped size, sections, relocations, TLS, unwind/handler metadata, stack requirements и начальные runtime allocations. Сравнить с бюджетами и определить необходимые изменения.
- [ ] **P2.2** Расширить PE-профиль для реально требуемых metadata/directories с полной валидацией до allocation. Сохранять RX/RO/RW, unmapped gaps, защиту kernel pages и rollback неопубликованного компонента.
- [ ] **P2.3** Реализовать требуемую обработку unwind/handler/chained metadata совместно с P3. Простое принятие таблиц загрузчиком не считать работающим unwinder.
- [ ] **P2.4** Поднять image/page/stack/TLS/heap/handle/event/thread/reservation quotas по измерениям. Устранить связанные фиксированные размеры и проверить переполнение, exhaustion, failed commit и освобождение ресурсов.
- [ ] **P2.5** Согласовать timer/run budget с runtime initialization и GC: сохранить диагностику зависаний и проверяемую изоляцию, а не убрать ограничения без замены.
- [ ] **P2.6** Загрузить actual runtime image, выполнить native entry на обоих поддерживаемых адресах, проверить точки startup до managed entry и teardown после принудительного отказа.

**Исходный разрыв:** Windows reference занимает 954 368 mapped bytes (233 страницы) и 2 593 unwind entries. Сейчас guest image cap — 256 KiB, всего 128 owned frames на компонент и 128 plain unwind records; fixed user stack — 16 KiB, compiler TLS — один модуль до 3 840 bytes. Размер Windows reference не является требованием к окончательному WitOS image.

**Готово, когда:** гость загружает полный adapted image и входит в его native startup; защита и восстановление ресурсов подтверждены. Сам рост лимитов этап не закрывает.

## P3. Runtime threads, контексты и настоящий GC

Native threads/TLS, Thread record и пустой ThreadStore уже существуют. Managed attachment, suspension, root visibility и GC detach ещё не доказаны.

- [ ] **P3.1** Определить kernel-owned context/thread capabilities и реализовать capture/get/set/restore контекста с проверкой владельца, stack bounds, selectors, flags и всех буферов. Не подменять capability числом thread ID или writable TLS.
- [ ] **P3.2** Реализовать безопасный процессный rendezvous: приостановка/возобновление runtime threads, preemptive/cooperative transitions, hijack/redirection либо другой реальный upstream-compatible путь. Учесть parked threads, idle, native callbacks и завершение потоков.
- [ ] **P3.3** Подключить настоящий ThreadStore attach и runtime TLS к compiler TLS lifecycle. Обработать главный поток, native workers, повторный вход и освобождение/reuse; подтвердить visibility runtime thread list.
- [ ] **P3.4** Обеспечить настоящий stack walk и runtime code-manager/unwind support для managed/native transitions и перечисления корней. Проверить не только stack bounds, но и регистры, return addresses и GC metadata.
- [ ] **P3.5** Доставлять необходимые native faults/exception contexts в runtime, сохраняя contained failure компонента. Для CET/shadow-stack/context и vectored-handler путей определить проверяемый профиль вместо успешных заглушек.
- [ ] **P3.6** Запустить upstream collector: реальные heap/segments, handle table, allocation contexts, write barriers/card tables и необходимые GC workers. Проверить отказ allocation/initialization и согласованность memory accounting.
- [ ] **P3.7** Выполнить настоящий collection с локальными, статическими и межпоточными корнями, где соответствующие потоки уже подключены. Проверить stop/resume, отсутствие deadlock и отсутствие потерянных живых объектов.
- [ ] **P3.8** Подключить реальный RuntimeThreadShutdown/ThreadStore detach, включая GC `FixAllocContext`, к native exit notification. Проверить normal exit, detached exit, slot reuse и отдельно abrupt fault/raw-exit policy.

**Готово, когда:** runtime thread state и GC работают вместе в actual guest workload. Compiler TLS, GC-special record или вызов `PalInit` отдельно этот этап не закрывают. Однопроцессорный data fence не заменяет managed suspension.

## P4. Первый гостевой managed .NET

Интегрируется с P3: bootstrap, создание типов/статик и collector имеют взаимные зависимости.

- [ ] **P4.1** Провести настоящий `RhInitialize`/`InitDLL` через все обязательные подсистемы; заменить отдельные startup-пробы полным runtime-путём и проверяемыми startup checkpoints.
- [ ] **P4.2** Через upstream runtime зарегистрировать executable module, ReadyToRun/TypeManager, GC tables/statics, frozen objects и eager constructors. Ядро только доставляет/защищает образ, а не инициализирует managed metadata.
- [ ] **P4.3** Дойти до обычного managed `Main` со стандартной CoreLib. Не использовать custom CoreLib, native функцию с похожим именем или обход managed bootstrap.
- [ ] **P4.4** Выполнить существующий `NativeAotBoot`: static initialization, `new` объекта, массив 4096 bytes, `GC.Collect`, проверка static/local roots и содержимого массива, `GC.KeepAlive`, результат `42`.
- [ ] **P4.5** Подтвердить реальное выполнение collector и managed workload через контролируемые checkpoints и проверки значений. Один serial marker или QEMU exit code недостаточен.
- [ ] **P4.6** Проверить путь возврата/выхода, повторный запуск нового компонента, teardown и отсутствие остаточных ресурсов. Добавить отдельный воспроизводимый guest-managed command/test в runner и CI.
- [ ] **P4.7** Прогнать поддерживаемые RAM/CPU-профили; закрепить image/source hashes, логи и известные ограничения в implementation docs.

**Готово, когда:** все пункты P4 проходят внутри QEMU/WitOS. Именно здесь впервые пишем «гостевой .NET запущен», уточняя NativeAOT и ограниченный профиль. Hosted Windows execution остаётся только reference.

## P5. Завершить M3: полноценная интеграционная проверка NativeAOT

- [ ] **P5.1** Реальные managed `throw/catch/finally`, nested unwind, сохранение корней через исключения и корректное освобождение native ресурсов; поддерживаемые hardware faults проходят правильную runtime translation.
- [ ] **P5.2** Настоящий finalizer thread и finalization queue, `GC.WaitForPendingFinalizers`, очереди/сигнализация и порядок shutdown.
- [ ] **P5.3** Managed `Thread`, monitor/синхронизация и thread-local state работают поверх реального attach/detach; GC сохраняет корни работающих и parked managed threads.
- [ ] **P5.4** Проверить OOM, stack overflow/fatal paths, ошибки startup и abrupt termination. Не путать обычный managed catch с аварией native bootstrap.
- [ ] **P5.5** Один end-to-end guest workload совмещает allocation/GC, exceptions, finalization и threads; серия повторных запусков не даёт утечек или stale handles/TLS/runtime records.
- [ ] **P5.6** Закрепить поддержанный профиль, воспроизводимую сборку/запуск и CI acceptance. Сравнить результаты с hosted reference; неподдержанные возможности перечислить явно.

**Готово, когда:** выполнен первоначальный M3 acceptance, а не только `Hello World` или единичный GC. Наличие этого результата ещё не обеспечивает запуск обычных IL assemblies.

## P6. CoreCLR/JIT и исходная цель совместимости

Это отдельный большой этап M6. Его подробный объём уточняется по фактическому CoreCLR port; NativeAOT link inventory не описывает все его зависимости.

- [ ] **P6.1** Зафиксировать pinned upstream CoreCLR/JIT build profile и WitOS platform boundary. Повторно инвентаризировать PAL, native dependencies и runtime services; переиспользовать проверенные механизмы, не предполагать автоматическую совместимость.
- [ ] **P6.2** Реализовать требования JIT к executable memory, W^X/protection transitions, публикации кода, code registration/unwind и context/exception handling. Текущий PAL отвергает executable allocations.
- [ ] **P6.3** Подготовить доставку и чтение runtime/application assemblies, metadata и конфигурации из гостя; выбрать минимальное хранилище/образ для bring-up, затем нужные filesystem/stream semantics. Полная файловая система не блокирует P4.
- [ ] **P6.4** Реализовать host/startup и binding: стандартные `.runtimeconfig.json`, `.deps.json`, framework/assembly resolution и путь запуска `dotnet Application.dll` без AOT-пересборки приложения.
- [ ] **P6.5** Запустить первый portable IL executable через настоящий JIT со стандартной CoreLib; allocations, GC, exceptions и threads должны выполняться в CoreCLR.
- [ ] **P6.6** Проверить runtime generics, reflection, `Assembly.Load`, `AssemblyLoadContext`, dynamic loading, `DynamicMethod`, `Reflection.Emit` и compilation expression trees в поддержанном upstream профиле.
- [ ] **P6.7** Завершить необходимые BCL platform services и регрессии: ThreadPool/Task/async, timers, synchronization, streams/files, encoding/globalization и другие API выбранной compatibility suite. Native/platform-specific зависимости пакетов описывать отдельно.
- [ ] **P6.8** Поддержать обычный developer workflow: стандартный TFM/SDK/MSBuild/NuGet на хосте, доставка output без перекомпиляции под WitOS; определить runtime packaging, diagnostics/debugging и доступные средства тестирования.
- [ ] **P6.9** Формализовать compatibility test: собрать portable приложение на другой ОС, зафиксировать hashes DLL и зависимостей, перенести байты без изменений, запустить в WitOS и сравнить поведение. Добавить representative portable NuGet libraries и regression matrix поддерживаемых API.

**Готово, когда:** неизменённые portable managed binaries проходят объявленный контракт совместимости. Нельзя обещать произвольному NuGet-пакету работу Windows/Linux-specific native dependencies только на основании переносимости IL.

## Текущая карта 64 unresolved symbols для P1

Снимок minimal startup на `e619a14`. Это группы исследования/реализации, не 64 независимых шага. Наличие имени не означает, что обязательно нужно реализовать одноимённый Windows API: сначала проверяем call site и выбранный профиль. Каждое фактическое исключение пути должно быть обосновано и проверено; его нельзя считать реализованной возможностью.

| Группа | Количество | Имена / привязка к работам |
| --- | ---: | --- |
| PAL attachment/startup | 2 | `PalInitComAndFlsSlot`, `PalAttachThread` → P1.8, P3.3/P3.8 |
| PAL contexts/hijack/CET | 10 | `PalGetCompleteThreadContext`, `PalSetThreadContext`, `PalAllocateCompleteOSContext`, `PalRestoreContext`, `PalHijack`, `PalGetHijackTarget`, `PalAreShadowStacksEnabled`, `GetSSP`, `SetSSP`, `PopulateControlSegmentRegisters` → P3 |
| PAL names/modules | 3 | `PalSetCurrentThreadName`, `PalSetCurrentThreadNameW`, `PalGetModuleFileName` → P1.6 |
| GC OS | 4 | `GCToOSInterface::{GetWriteWatch, ResetWriteWatch, VirtualReserveAndCommitLargePages, DebugBreak}` → P1.7 |
| Handles/memory/waits/identity | 14 | `CloseHandle`, `__imp_CloseHandle`, `CreateEventExW`, `SetEvent`, `Sleep`, `WaitForMultipleObjectsEx`, `VirtualAlloc`, `VirtualFree`, `DuplicateHandle`, `GetCurrentProcess`, `GetCurrentThread`, `__imp_GetCurrentThreadId`, `GetCurrentProcessorNumberEx`, `GetThreadPriority` → P1.5/P3 |
| COM | 3 | `CoGetApartmentType`, `CoInitializeEx`, `CoUninitialize` → P1.6/P1.8 |
| Console/module access | 6 | `WriteFile`, `GetStdHandle`, `GetConsoleOutputCP`, `GetModuleFileNameW`, `__imp_GetModuleHandleW`, `__imp_GetProcAddress` → P1.5/P1.6 |
| Conversion/CRT/math | 6 | `MultiByteToWideChar`, `WideCharToMultiByte`, `LocalFree`, `__stdio_common_vsnprintf_s`, `_fltused`, `log` → P1.3/P1.6 |
| Diagnostics | 5 | `FormatMessageW`, `RegisterEventSourceW`, `DeregisterEventSource`, `ReportEventW`, `__imp_IsDebuggerPresent` → P1.6 |
| Exception/unwind | 6 | `RaiseFailFastException`, `__imp_RaiseFailFastException`, `__imp_RaiseException`, `__imp_AddVectoredExceptionHandler`, `__imp_RtlVirtualUnwind`, `__C_specific_handler` → P2/P3/P5 |
| Compiler protections | 4 | `__GSHandlerCheck`, `__guard_dispatch_icall_fptr`, `__security_check_cookie`, `__security_cookie` → P1.3/P3 |
| Randomness | 1 | `BCryptGenRandom` → P1.4 |
| **Всего** | **64** | Широкий диагностический link имеет 70; различие не является мерой готовности |

Полный отчёт после обновления: `artifacts/runtime-readiness/readiness.md` и `readiness.json`; широкий inventory — `artifacts/runtime-source/`. Артефакты генерируются локально и не хранятся в Git. Их содержимое не заменяет guest execution evidence.

## Проверки и правила обновления плана

Для native/boot/image/runner изменений обязательны:

```powershell
dotnet build WitOS.slnx --configuration Release
dotnet run --project tools/WitOS.Dev --configuration Release -- test
```

Для runtime/source-port изменений также выполняются `runtime-audit`, `runtime-probe`, `runtime-target` и `runtime-source` через тот же инструмент. `runtime-config` включает source-build/target/readiness и четыре native guest-прогона. После P4 нужен отдельный guest-managed acceptance, поскольку существующий `runtime-config` не доказывает managed execution. Точный состав проверок определяется [AGENTS.md](AGENTS.md).

1. Отмечать `[x]` только после выполнения условия и фактической проверки; для частично выполненного пункта оставлять `[ ]` и пояснять готовую часть.
2. После каждого завершённого участка обновлять здесь базовую версию, карту этапов и ближайший следующий пункт; добавлять ссылку на implementation doc с evidence.
3. При закрытии P1 обновить unresolved snapshot, размеры actual guest image и оценку оставшейся работы. Старые измерения сохранять в исторических документах, не выдавать за текущие.
4. Новый обнаруженный блокер добавлять под нужный этап с зависимостью и критерием закрытия. Не перепрыгивать обязательную инфраструктуру ради количества закрытых символов.
5. Прогресс измерять прохождением рубежей P1–P6. Один commit, native probe или hosted test не закрывает весь рубеж.

Сейчас вне критического пути первого запуска: GUI/shell, GPU, networking, distributed services, широкая аппаратная поддержка, SMP и встраивание ядра во firmware/chip. Возвращаем их в план только по отдельной цели либо если конкретная runtime-зависимость этого требует.
