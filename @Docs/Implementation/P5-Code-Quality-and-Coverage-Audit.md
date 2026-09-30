# Аудит качества и покрытия перед P6

Дата: **2026-09-30**. База: локальный P5/M3, user ABI v37 / boot ABI v3, upstream .NET 10.0.8. **Последующий статус: Q1.1–Q1.6 исправлены и проверены; [реализация и evidence](Q1-Quality-Hardening.md).** Ниже сохранены исходные находки на момент аудита, до исправлений.

## Вывод

Подтверждённый запуск NativeAOT со standard CoreLib сохраняется. В просмотренных критических путях нового воспроизводимого дефекта kernel/GC не найдено. Обнаружены **три воспроизводимых дефекта инфраструктуры проверки** средней серьёзности и существенный пробел в доказательстве fallback hijack. Рекомендация: закрыть Q1.1–Q1.4 перед реализацией P6; не расширять архитектуру и не заменять CoreLib ради обхода этих замечаний.

Это риск-ориентированное ревью, а не построчная проверка всего проекта и не доказательство отсутствия ошибок. Инвентаризация: 380 файлов C/C++/C#/ASM, около 33,2 тыс. строк без bin/obj. Production-код, ABI и постоянные тесты в ходе аудита не менялись. Диагностические harness и результаты находятся в ignored `artifacts/p5-quality-audit/`.

Просмотрены: syscall/return validation, thread create/reference/suspend/context/stack lease, динамическая память и rollback, mixed-object waits, native CreateThread, image publication, unwind cache/checked reads, exception dispatch, managed EH/finalization/thread probes, runner, публикация evidence, PE corpus и CI. Весь upstream runtime заново не ревьюировался.

## Подтверждённые дефекты

### Q1.1 — Валидатор принимает противоречивое evidence (средний)

Места: `tools/WitOS.Dev/RuntimeBootProtocol.cs:62`, `:158`, `:166`.

На копиях каждого из четырёх действительных serial logs последней успешной матрицы `Validate` возвращает `true` после каждой независимой мутации:

- вставлен `[USER] [NATIVE-FAIL-FAST]` внутрь успешного managed execution;
- рядом с `Runtime managed thread capacity failures: 4` добавлен такой же отчёт со значением `0`;
- оба `[TEST-PASS] Runtime.NativeFaultContained` удалены из своих блоков и перенесены в конец лога.

Причина: fatal-маркеры проверяются в некоторых отрицательных блоках, но не запрещены в положительных; capacity проверяется подсчётом точной строки, а native-fault verdict — глобальным количеством без привязки к конкретному base/block. Остальные части реального boot-протокола при этих мутациях не изменены.

Последствие — стенд не гарантирует заявленную строгость отчёта: лишнее/противоречивое evidence может пройти. Это **не** доказательство, что реальный kernel пропустил fatal или что исторический P5 был ложным: kernel содержит собственные assertions. Речь о защите от регрессий, испорченных/смешанных логов и несогласованных отчётов.

Исправление: явные границы всех execution blocks, whitelist допустимых fatal-сценариев, ровно один отчёт каждого типа с проверкой значения, отсутствие verdict вне своего блока. Добавить мутации реального лога и небольшие отрицательные fixtures.

Evidence: `artifacts/p5-quality-audit/real-log-mutations.json`, `repros.json`; исходные логи не изменялись.

### Q1.2 — Публикация нескольких JSON не транзакционна (средний)

Место: `tools/WitOS.Dev/RuntimeBootAttempt.cs:68` — последовательность публикации acceptance, last-success и status; обработка отказа `:73`.

В изолированном root сначала опубликован старый успех. В новой попытке искусственно заблокирована запись её `status.json` (на этом пути создан каталог), затем вызван `Publish`. Результат:

- команда бросает `UnauthorizedAccessException`;
- текущий `acceptance.json` удалён обработчиком отказа;
- `last-success.json` уже заменён новой попыткой;
- её per-run `acceptance.json` остался;
- `current-run.json` остался `running`, потому что повторная запись статуса отказа упала на той же ошибке.

Отдельные rename атомарны, но весь переход между несколькими файлами — нет. Старый успех сохранился в истории, однако указатель `last-success` уже не обозначает последнюю полностью завершённую публикацию. Потребители статуса получают противоречивые ответы.

Исправление: определить единственную точку commit и правила восстановления/чтения после частичной публикации; не продвигать last-success на незавершённую попытку. Проверять отказ на каждом write/rename, сохранение первоначальной ошибки и восстановление после restart. Простая перестановка двух записей не покрывает все стадии.

Evidence: `artifacts/p5-quality-audit/repros.json`, раздел `publication`. Реальные acceptance-файлы проекта не затрагивались.

### Q1.3 — Cleanup deadline зависит от послушности callback (средний, латентный)

Место: `tools/WitOS.Dev/Processes.cs:56`–`:59`.

Для timeout control создаётся cancellation token на одну секунду, но сам callback непосредственно `await`-ится. Cancellation token не ограничивает callback, который его игнорирует. Диагностический callback выполняет `Task.Delay(7000)` без token: при deadline команды 1 s runner возвращается через **8,03 s**, уже превысив заявленные дополнительные 5 s cleanup, и лишь затем сообщает ошибку подтверждения termination. Бесконечный callback оставит ожидание неограниченным.

Текущий QMP callback передаёт token в асинхронные операции; его постоянное зависание этим аудитом **не** воспроизведено. Дефект относится к гарантии общего runner API и устойчивости к будущей ошибке callback.

Исправление: runner должен сам ограничивать асинхронное ожидание control task общим deadline, после истечения закрывать I/O и переходить к forced cleanup. Наблюдать поздние ошибки task, не считать cancellation подтверждением завершения процесса. Синхронно блокирующий callback требует отдельного явно заданного контракта. Добавить тест игнорирования cancellation без намеренного вечного зависания.

Evidence: `artifacts/p5-quality-audit/repros.json`, раздел `unboundedControl`.

## Покрытие тестами и оставшиеся риски

| Область | Что уже проверяется | Чего пока не доказано |
| --- | --- | --- |
| Host runner | Реальные процессы, descendants, чужие pipe writers, две output streams, QMP handshake, forced fallback | Независимое ограничение callback; лимит объёма stdout/stderr; все исключения и стадии cleanup |
| Evidence/protocol | Отдельные positive executions, exit/timeout, hashes, failure history, schema и мутации | Противоречия/границы Q1.1; файловые отказы после частичной публикации Q1.2 |
| Потоки/GC | Реальные Thread/Join/Monitor/TLS, parked/running roots, quota recovery, attachment/detachment, ThreadStore audits | Отдельная приёмка return-address hijack; существенно разные interleavings и длительный stress |
| Kernel memory/handles | Whole-buffer checks, поколения, no-access accounting, частичные allocation failures, teardown | Exhaustive/property-based state-machine coverage; SMP намеренно вне профиля |
| PE/unwind | 529 guarded PE inputs; Windows differential unwind, forged entry и transactional output rejection | Coverage-guided fuzzing, sanitizer evidence, систематические мутации содержимого TLS/reloc/pdata/xdata с ожидаемым verdict |
| Managed semantics | Реальные EH/filters/rethrow, GC в EH/finalizers, suppression/resurrection, native release, аппаратные fault cases | Полная BCL/API compatibility, async/ThreadPool/Task, dynamic code — отдельный P6 |

**Q1.4 — fallback hijack, приоритет до P6.** `tests/Runtime.NativeAot/worker_lifecycle.cpp:273` допускает `Redirects > before || ReturnHijacks > before`; `RuntimeBootProtocol.cs:147` проверяет то же условие. Во всех 16 опубликованных checkpoints итоговой P5-матрицы счётчики равны `attempts=2, redirects=1, returns=0, unsafe=1`. Это доказывает redirect и отказ unsafe snapshot, но не fallback. Отчёт печатается до последующих managed cycles, поэтому нулевой checkpoint не доказывает, что fallback никогда не происходил позже. Положительного отдельного assertion/evidence для этой ветви нет. Нужен детерминированный реальный guest-сценарий возвратного hijack с живыми корнями и восстановлением исполнения; если путь неприменим к профилю, это нужно обосновать по upstream и явно ограничить claim.

**Q1.5 — parser corpus и измерение покрытия.** В `PeCorpus.c:47`–`:51` 257 коротких префиксов, 13 усечений хвоста и 256 однобитных мутаций только первых 1024 байт. Ещё три guarded case — baseline и две границы размера image. Для большинства мутаций проверяется допустимый enum/no crash, а не требуемое принятие/отказ. Это полезный bounded-read smoke corpus, но он сам по себе не проверяет глубокие directory/unwind структуры. Отдельные hand-written metadata tests есть — их нельзя считать отсутствующими. Следующий шаг: целевые структурные мутации с ожидаемыми verdict, branch-coverage baseline для host/доступных native parsers и отдельный sanitizer/fuzz lane. Процент line/branch coverage сейчас **не измерен**; число повторений не заменяет его.

Дополнительный риск: `Processes.cs:36`–`:42` накапливает stdout/stderr без ограничения, `DevTool.cs:345` читает весь serial file в память. Watchdog ограничивает время, но не объём. В этом аудите OOM хоста намеренно не вызывался. Нужны file-backed capture или явный лимит с диагностикой truncation и запретом принимать усечённое evidence как полный успех.

## Качество и сопровождаемость

Сильные стороны: реальные upstream зависимости вместо успешных заглушек; стандартная CoreLib; отдельные host/guest доказательства; строгая линковка и pins; IF-disabled publication; поколения handle/reference/lease; readonly image metadata; transactional output при unwind; отдельный uncached API. В просмотренных путях whole-range validation предшествует copy-out, создание потоков откатывает observer/private identity/pages, foreign stack lease блокирует опасный resume/mutation.

Долг сопровождения:

- `RuntimeCpuImage.cs:99` и `RuntimeConfigProbe.cs:209` выбирают объекты по диапазонам вроде `[31..35]` и `[7..12]`. Количество проверяется, семантика порядка не закодирована. Именованный manifest снизит риск изменения состава при вставке нового объекта.
- `user.c` — 863 строки, `user_runtime_config_tests.c` — 707, `DevTool.cs` — 562. Размер сам по себе не дефект, но длинный syscall switch и склеенные строки с несколькими изменениями состояния усложняют ревью. Сначала разнести parser/publication в небольшие функции с явными инвариантами при исправлениях Q1; массовую перезапись ядра не начинать.
- Managed probes возвращают код этапа, но часто скрывают конкретную причину через `catch { Failed = 1; }`. Для следующих нагрузок полезны bounded diagnostics: cycle/worker/phase/expected/actual, без изменения семантики проверяемого runtime.
- README host-тестов всё ещё указывал 527 вместо 529 inputs; в заголовке старого Q0-аудита была повреждена кириллица. Эти две неточности документации исправлены в этом аудите; старые результаты сохранены как история.
- На старте ревью было 65 modified и 240 untracked entries. Локальный worktree — актуальная база; удалённая воспроизводимость и CI этим не подтверждаются. Перед будущей публикацией нужен review полного набора новых файлов, а не только `git diff` отслеживаемых файлов.

## Проверки этого аудита

- Свежая `dotnet build WitOS.slnx --configuration Release`: **0 warnings / 0 errors**.
- Свежая `dotnet run --project tests/WitOS.Dev.Tests --configuration Release --no-build -- --pe`: **24 groups passed**, **529 PE inputs**, 157 accepted / 372 rejected.
- Свежая `dotnet run --project tools/WitOS.Dev --configuration Release --no-build -- test`: **все 20 kernel/QEMU сценариев прошли**, включая timeout с `exit=0, TimedOut=true` после QMP shutdown.
- Изолированные executable repro Q1.1–Q1.3; мутации всех четырёх реальных serial logs.
- `git diff --check`: без ошибок whitespace (предупреждения CRLF/LF — существующее состояние).

Логи: `artifacts/p5-quality-build.log`, `artifacts/p5-quality-host-tests.log`, `artifacts/p5-quality-kernel-tests.log`. Команды reproduction: `dotnet run --project artifacts/p5-quality-audit/Audit.csproj --configuration Release` и та же команда с `-- real`. Harness компилирует через linked Compile inputs текущие production tooling файлы, а не копию их алгоритмов.

Полные `runtime-source/config/boot-run` заново в этом аудите не запускались: runtime/overlay не менялись. Использовано сохранённое evidence P5 run `20260930T044307187-ae0aae48c9ce4d01b0e0e504fce9cef0`, PE SHA-256 `c10b7a66c024c7410438e835b63968c297f00525aa1856565849e92460abe4f7`. Это явно отделено от свежих проверок выше. Историческое завершение P5 сохраняется; Q1 фиксирует новый долг перед P6.
