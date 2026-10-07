# Справочник пользовательского ABI ядра WitOS

Версии: **user ABI v52**, **boot ABI v4**. Источник истины — заголовки `src/Kernel/include/witos/*.h`; этот документ их описывает и проверяется хостовым тестом: каждый `WIT_CALL_*` из `user_abi.h` обязан встречаться здесь. Раскладка вызовов — ABI-1 по [RFC-0011 v3 §7](../RFC-0011-Kernel-Architecture-and-ABI.md), введённая шагом K1.1 плана; судьба каждого прежнего вызова — в [RFC-0011 v3 §8](../RFC-0011-Kernel-Architecture-and-ABI.md). ABI экспериментален до шага K8, но с K1.1 номер вызова, значение статуса и бит права никогда не переиспользуются (RFC-0011 §10.1).

## Классы

| Класс | Смысл |
| --- | --- |
| целевой | Вызов целевого набора RFC-0011 §7.12; остаётся в ABI-1 1.0, его семантика может уточняться до K8 только совместимо |
| транзитный | Вызов нынешней реализации, который RFC-0011 §8 сливает, удаляет или переносит в слой 2 на названном шаге; номер после этого не переиспользуется |

## Соглашение о вызове

| ISA | Инструкция | Номер | Аргументы | Статус | Результат | После возврата |
| --- | --- | --- | --- | --- | --- | --- |
| x64 | `INT 0x80`, DPL3 | RAX | RCX, RDX, R8 | RAX | RDX | Остальные GPR и x87/SSE сохраняются; RFLAGS = 0x202 |
| ARM64 | `SVC #0` | x8 | x0, x1, x2 | x0 | x1 | Остальные регистры и FP/SIMD сохраняются; флаги PSTATE сброшены |

На x64 шаг K5 переводит транспорт на `SYSCALL` с аргументами в RDI, RSI, RDX (RFC-0011 §6.1).

Общие правила для всех вызовов:

- Вызов исполняется на единственном процессоре с запрещёнными прерываниями и не вытесняется. Блокирующие вызовы паркуют поток после полной валидации.
- Структуры передаются по указателю с точным размером; первые два поля любой структуры — `Version` и `Size`. Неизвестная версия даёт `UNSUPPORTED`, неверный размер — `INVALID_ARGUMENT`.
- Весь пользовательский буфер проверяется целиком до первой записи; при ошибке ни один байт назначения не меняется.
- Неиспользуемые аргументы и поля `Reserved` обязаны быть нулями.
- Возврат из `EXCEPTION_CONTINUE` и `THREAD_CONTEXT_RESTORE` при успехе не происходит: исполнение продолжается в переданном контексте.
- `QUERY` возвращает в младших 32 битах `WIT_ABI_VERSION`, в старших — маску семейств `WIT_ABI_FEATURE_*` (`CHANNELS` 1, `DEVICES` 2, `PROCESSES` 4, `UTC` 8, `SMP` 16); сейчас маска нулевая.

## Статусы

| Значение | Имя | Типичная причина |
| --- | --- | --- |
| 0 | `WIT_STATUS_OK` | Успех |
| 1 | `WIT_STATUS_UNSUPPORTED` | Неизвестный вызов, версия, операция или режим |
| 2 | `WIT_STATUS_BAD_HANDLE` | Хэндл не существует, закрыт или устарело поколение |
| 3 | `WIT_STATUS_DENIED` | Хэндл без нужного права или чужой владелец |
| 4 | `WIT_STATUS_BAD_ADDRESS` | Пользовательский диапазон не отображён с нужными правами |
| 5 | `WIT_STATUS_TOO_LARGE` | Превышен предел размера |
| 6 | `WIT_STATUS_INVALID_ARGUMENT` | Нарушен формат аргументов |
| 7 | `WIT_STATUS_WRONG_TYPE` | Хэндл другого вида; узел не каталог |
| 8 | `WIT_STATUS_NO_MEMORY` | Квота или физическая память исчерпаны |
| 9 | `WIT_STATUS_NOT_RESERVED` | Адрес вне резервирования |
| 10 | `WIT_STATUS_NOT_COMMITTED` | Страница не закоммичена |
| 11 | `WIT_STATUS_DEADLOCK` | Цикл ожидания join или повторный вход владельца lifecycle |
| 12 | `WIT_STATUS_BUSY` | Ресурс занят; можно повторить |
| 13 | `WIT_STATUS_TIMED_OUT` | Дедлайн истёк или нет сигнала при опросе |
| 14 | `WIT_STATUS_CLOSED` | Ожидаемый объект закрыт; ссылка на завершённый поток |
| 15 | `WIT_STATUS_INTERRUPTED` | Ожидание прервано активацией (до K1.3 — alertable-ожидание с очередью APC) |
| 16 | `WIT_STATUS_NOT_FOUND` | Файл, модуль или символ отсутствует |
| 17 | `WIT_STATUS_INITIALIZATION_FAILED` | Пользовательский attach DLL вернул отказ; уходит вместе с `LIBRARY` на K8 |
| 18 | — | Зарезервирован для `PEER_CLOSED` каналов (K2) |

## Вызовы

Аргументы указаны в порядке RCX, RDX, R8 (x0, x1, x2); результат возвращается в RDX (x1).

### Целевой набор

| № | Вызов | Аргументы | Результат | Класс |
| --- | --- | --- | --- | --- |
| 0 | `WIT_CALL_QUERY` | — | версия ABI и маска семейств | целевой |
| 1 | `WIT_CALL_PROCESS_EXIT` | exit code | не возвращается | целевой |
| 2 | `WIT_CALL_HANDLE_CLOSE` | handle | 0 | целевой |
| 3 | `WIT_CALL_HANDLE_DUPLICATE` | thread handle или `WIT_THREAD_SELF`, указатель вывода, rights (0 — те же) | 8; события — K1.2, каналы — K2 | целевой |
| 4 | `WIT_CALL_DEBUG_WRITE` | console handle, buffer, length ≤ 65536 | записано байт | целевой |
| 10 | `WIT_CALL_MEMORY_RESERVE` | size, alignment ≥ 4 КиБ | base | целевой |
| 11 | `WIT_CALL_MEMORY_COMMIT` | base, size, protection | 0 | целевой |
| 12 | `WIT_CALL_MEMORY_DECOMMIT` | base, size | 0 | целевой |
| 13 | `WIT_CALL_MEMORY_PROTECT` | base, size, protection | 0 | целевой |
| 14 | `WIT_CALL_MEMORY_RELEASE` | точная база резервирования | 0 | целевой |
| 15 | `WIT_CALL_MEMORY_RESET` | base, size, 0 | 0 | целевой |
| 16 | `WIT_CALL_MEMORY_QUERY` | buffer, 112, version 2 | 112 | целевой |
| 17 | `WIT_CALL_MEMORY_PRESSURE_EVENT` | — | event handle только для ожидания | целевой |
| 30 | `WIT_CALL_THREAD_CREATE` | `WitThreadCreateRequest`, 48, 0 | thread handle | целевой |
| 31 | `WIT_CALL_THREAD_EXIT` | exit code | не возвращается; в скоординированном профиле завершает компонент до K1.2 | целевой |
| 32 | `WIT_CALL_THREAD_YIELD` | — | 1, если выбран другой поток | целевой |
| 34 | `WIT_CALL_THREAD_QUERY` | buffer, 72, version 3 | 72; с K1.2 первый аргумент — thread handle или `WIT_THREAD_SELF` | целевой |
| 35 | `WIT_CALL_THREAD_SUSPEND` | thread handle | предыдущий счётчик | целевой |
| 36 | `WIT_CALL_THREAD_RESUME` | thread handle | предыдущий счётчик | целевой |
| 37 | `WIT_CALL_THREAD_CONTEXT_GET` | thread handle или `WIT_THREAD_SELF`, buffer, 720 | 0 | целевой |
| 38 | `WIT_CALL_THREAD_CONTEXT_SET` | thread handle, buffer, 720 | 0 | целевой |
| 39 | `WIT_CALL_CONTEXT_PROFILE` | buffer, 32, version 2 | 0 | целевой |
| 40 | `WIT_CALL_THREAD_ACTIVATE` | thread handle, callback, argument | 0; до K1.3 — постановка в очередь APC | целевой |
| 50 | `WIT_CALL_EVENT_CREATE` | flags (`MANUAL_RESET`, `INITIAL_SIGNALED`), rights (0 — `WAIT` и `SIGNAL`), 0 | event handle | целевой |
| 51 | `WIT_CALL_EVENT_SET` | handle | 0 | целевой |
| 52 | `WIT_CALL_EVENT_RESET` | handle | 0 | целевой |
| 53 | `WIT_CALL_OBJECT_WAIT` | `WitUserWaitRequest`, 32, 0 | индекс победителя | целевой |
| 54 | `WIT_CALL_SLEEP_UNTIL` | монотонный дедлайн | 0 | целевой |
| 55 | `WIT_CALL_CLOCK_READ` | clock (`WIT_CLOCK_MONOTONIC` 0; `UTC` 1 — `UNSUPPORTED` до K6) | монотонный счётчик | целевой |
| 56 | `WIT_CALL_CLOCK_FREQUENCY` | clock | частота, Гц | целевой |
| 57 | `WIT_CALL_RANDOM` | buffer, size ≤ 65536, 0 | size | целевой |
| 60 | `WIT_CALL_EXCEPTION_REGISTER` | callback или 0, version 1, flags 0 | 0 | целевой |
| 61 | `WIT_CALL_EXCEPTION_QUERY` | token, buffer, 768 | 0 | целевой |
| 62 | `WIT_CALL_EXCEPTION_CONTINUE` | token, `WitUserExceptionTransfer`, 736 | не возвращается при успехе | целевой |
| 63 | `WIT_CALL_EXCEPTION_REJECT` | token | не возвращается; компонент завершается | целевой |
| 93 | `WIT_CALL_PROCESSOR_QUERY` | buffer, 4, 0 | 4 | целевой |
| 94 | `WIT_CALL_PROCESS_WRITE_BARRIER` | — | 0 | целевой |

Зарезервированы: 18–20 (объекты памяти и публикация кода, K5), 33 (`THREAD_SET_TLS`, K5), 41 (`THREAD_AFFINITY`, K7), 70–72 (каналы, K2), 80–85 (устройства, K3), 90–92 (процессы, K5).

### Транзитные вызовы

| № | Вызов | Аргументы | Результат | Класс |
| --- | --- | --- | --- | --- |
| 200 | `WIT_CALL_THREAD_CREATE_SIMPLE` | entry, argument, flags (`DETACHED`, `LIBRARY_NOTIFICATIONS`) | thread handle или 0 для detached | транзитный, K1.2 |
| 201 | `WIT_CALL_THREAD_JOIN` | thread handle | exit code; хэндл потребляется | транзитный, K1.2 |
| 202 | `WIT_CALL_THREAD_COMPLETE` | exit code | не возвращается; упорядоченное завершение после TLS и runtime-уведомлений | транзитный, K1.2 |
| 203 | `WIT_CALL_THREAD_REFERENCE_QUERY` | thread handle, buffer, 56 | 56 | транзитный, K1.2 |
| 204 | `WIT_CALL_THREAD_NATIVE_ID` | — | 32-битный native ID | транзитный, K1.2 |
| 205 | `WIT_CALL_THREAD_CONTEXT_METADATA` | thread handle, buffer, 720 | 0 | транзитный, K1.2 |
| 206 | `WIT_CALL_APC_DEQUEUE` | buffer, 16, 0 | 16 | транзитный, K1.3 |
| 207 | `WIT_CALL_MONOTONIC_QUERY` | buffer, 8, selector (`COUNTER` или `HZ`) | 8 | транзитный, K6 |
| 208 | `WIT_CALL_CPU_CACHE_SIZE` | — | байт крупнейшего кэша | транзитный, K7 |
| 209 | `WIT_CALL_THREAD_CONTEXT_RESTORE` | buffer, 720, version 2 | не возвращается при успехе | транзитный, K8 |
| 210 | `WIT_CALL_STACK_LEASE_ACQUIRE` | thread handle, buffer, 48 | 0 | транзитный, K8 |
| 211 | `WIT_CALL_STACK_LEASE_QUERY` | token, buffer, 48 | 0 | транзитный, K8 |
| 212 | `WIT_CALL_STACK_LEASE_RELEASE` | token | 0 | транзитный, K8 |
| 213 | `WIT_CALL_EXCEPTION_BEGIN` | `WitThreadContext`, 720, 32-битный код | token | транзитный, K8 |
| 214 | `WIT_CALL_FATAL_ARM` | 32-битный код по умолчанию | 0 | транзитный, K8 |
| 215 | `WIT_CALL_FATAL_REPORT` | `WitUserFatalInfo`, 872, version 1 | 0 | транзитный, K8 |
| 216 | `WIT_CALL_THREAD_NAME_SET` | UTF-16 pointer, units < 128, flags 0 | 0 | транзитный, K8 |
| 217 | `WIT_CALL_THREAD_NAME_QUERY` | buffer, 280, version 1 | 0 | транзитный, K8 |
| 218 | `WIT_CALL_CODE_MEMORY` | `WitCodeMemoryRequest`, 64, 0 | база для `RESERVE`, иначе 0 | транзитный, K5 и K8 |
| 219 | `WIT_CALL_FILE` | `WitFileRequest`, 64, 0 | хэндл, байты или позиция | транзитный, K8 |
| 220 | `WIT_CALL_STORAGE_QUERY` | `WitStorageQuery`, 64, 0 | 1056 или 0 в конце списка | транзитный, K8 |
| 221 | `WIT_CALL_LIBRARY` | `WitLibraryRequest`, 64, 0 | зависит от операции | транзитный, K8 |
| 222 | `WIT_CALL_PROCESS_STATE` | `WitProcessStateRequest`, 64, 0 | размер значения, блока или каталога | транзитный, K8 |

Следующий свободный номер: **223**.

Слившиеся на K1.1 вызовы и имена, которыми замороженная Windows-линия продолжает пользоваться через `user_abi_frozen.h` (ядро этот заголовок не включает; удаляется на K8): `EXIT`, `CLOSE`, `WRITE`, `THREAD_CREATE_REFERENCE`, `THREAD_REFERENCE_DUPLICATE`, `CPU_CONTEXT_QUERY`, `APC_QUEUE`, `EVENT_CREATE_RIGHTS`, `MONOTONIC_READ`, `MONOTONIC_FREQUENCY`, `WIT_STATUS_APC_PENDING`, `WIT_THREAD_REFERENCE_CURRENT`. Без замены ушли домен тиков PIT (`CLOCK_READ`/`CLOCK_FREQUENCY` в тиках, `THREAD_SLEEP`, `EVENT_WAIT`), `THREAD_CURRENT` (константа `WIT_THREAD_SELF` и `THREAD_QUERY`), `EVENT_WAIT_UNTIL` и `EVENT_WAIT_ANY_UNTIL` (`OBJECT_WAIT`), `EXCEPTION_UNWIND` (`EXCEPTION_CONTINUE` с запросом переноса), `CONSOLE_WRITE` (`DEBUG_WRITE`).

### Операции составных вызовов

| Вызов | Операции |
| --- | --- |
| `CODE_MEMORY` | `RESERVE` 0, `ALIAS` 1, `PROTECT` 2, `PUBLISH` 3, `MAP_SPARSE` 4, `RESET_SPARSE` 5, `VALIDATE` 6 |
| `FILE` | `OPEN` 0, `LENGTH` 1, `READ_AT` 2, `READ` 3, `SEEK` 4 |
| `STORAGE_QUERY` | `STAT` 0, `LIST` 1 |
| `LIBRARY` | `LOAD` 0, `SYMBOL` 1, `UNLOAD` 2, `QUERY` 3, `FIND` 4, `PATH` 5, `ACQUIRE_READER` 6, `RELEASE_READER` 7, `QUERY_READER` 8, `FINISH_LIFECYCLE` 9, `SHUTDOWN` 10, `THREAD_ENTER` 11, `THREAD_LEAVE` 12, `MODULE_PATH` 13 |
| `PROCESS_STATE` | `ENV_GET` 0, `ENV_SET` 1, `ENV_BLOCK` 2, `CWD_GET` 3, `CWD_SET` 4 |

`LIBRARY.MODULE_PATH` не принимает хэндл: по адресу в `Ordinal` возвращает `WitLibraryPath` модуля, чей образ его содержит (главного образа компонента или загруженной библиотеки), а с флагом `MAIN_IMAGE` 1 и нулевым адресом — путь главного образа. `NOT_FOUND`, если адрес не принадлежит модулю или главный образ создан не из файла пакета.

`PROCESS_STATE` хранит в ядре окружение и текущий каталог компонента, общие для всех его модулей: блок записей `Name=Value\0` в порядке установки с финальным `\0`; имена сравниваются со свёрткой регистра ASCII; текущий каталог — канонический UTF-8 от `/`, `CWD_SET` принимает только каталог пакета. Отказ не меняет ни состояние, ни вывод. По RFC-0011 §8 это политика слоя 2 и уходит на K8.

### Ожидание

`OBJECT_WAIT` — единственное ожидание: события и хэндлы потоков в одном массиве (до четырёх), абсолютный монотонный дедлайн (`WIT_WAIT_INFINITE` — бесконечно, 0 — опрос), все хэндлы проверяются до потребления сигнала, победитель публикуется атомически. Флаг `ALL` реализован для замороженной линии и уходит вместе с ней (в целевом ABI RFC 0011 §7.4 он `UNSUPPORTED`); хэндл, повторённый в массиве, недопустим только в режиме `ALL`, как в Windows. Флаг `ALERTABLE` до K1.3 возвращает `INTERRUPTED` при очереди APC. Истёкшие дедлайны обрабатываются раньше последующих сигналов и закрытий. Домена тиков PIT в ABI больше нет: таймер лишь продвигает проверку монотонных дедлайнов.

## Структуры

| Структура | Заголовок | Размер | Версия |
| --- | --- | --- | --- |
| `WitUserStartup` | `user_abi.h` | 24 | равна `WIT_ABI_VERSION` |
| `WitUserImageInfo` | `image_info.h` | 568 | 2 |
| `WitUserMemoryInfo` | `memory_info.h` | 112 | 2 |
| `WitUserThreadInfo` | `thread_info.h` | 72 | 3 |
| `WitThreadReferenceInfo` | `thread_reference.h` | 56 | 3 |
| `WitThreadCreateRequest` | `thread_reference.h` | 48 | 1 |
| `WitUserWaitRequest` | `wait_objects.h` | 32 | 1 |
| `WitUserApc` | `wait_objects.h` | 16 | — |
| `WitStackLeaseInfo` | `stack_lease.h` | 48 | 1 |
| `WitThreadContext` | `thread_context.h` | 720 | 2 |
| `WitUserExceptionInfo` | `exception.h` | 768 | 1 |
| `WitUserExceptionTransfer` | `exception.h` | 736 | 1 |
| `WitUserFatalInfo` | `fatal_info.h` | 872 | 1 |
| `WitCpuContextInfo` | `cpu_context_info.h` | 32 | 2 |
| `WitThreadNameInfo` | `thread_name.h` | 280 | 1 |
| `WitCodeMemoryRequest` | `code_memory.h` | 64 | 1 |
| `WitFileRequest` | `file_io.h` | 64 | 1 |
| `WitStorageQuery` | `storage_query.h` | 64 | 1 |
| `WitStorageInfo` | `storage_query.h` | 1056 | 1 |
| `WitLibraryRequest` | `library.h` | 64 | 2 |
| `WitLibraryInfo` | `library.h` | 40 | 2 |
| `WitLibraryPath` | `library.h` | 1040 | 2 |
| `WitLibraryLifecycle` | `library.h` | 192 | 2 |
| `WitProcessStateRequest` | `process_state.h` | 64 | 1 |

`WitThreadContext` и производные от него структуры содержат регистры x64 и образ FXSAVE64. Для ARM64 эти структуры получают отдельную регистровую часть с тем же префиксом `Version`, `Size`, `ThreadId`, `StackLow`, `StackHigh`, `State`, `Flags` (RFC-0011 v3 §7.3); она появляется на шаге K1.4 плана.

## Хэндлы и права

Токен хэндла: `owner << 32 | generation << 16 | (slot + 1)`. Поколение растёт при каждом переиспользовании слота, поэтому устаревший токен никогда не совпадает с новым объектом.

| Вид | Значение | Права |
| --- | --- | --- |
| `CONSOLE` | 1 | `WRITE` 1 |
| `SELF` | 2 | — |
| `THREAD` | 3 | `JOIN` 2 (уходит на K1.2 вместе с `THREAD_JOIN`) |
| `EVENT` | 4 | `WAIT` 4, `SIGNAL` 8 |
| `THREAD_REFERENCE` | 5 | `WAIT` 4, `QUERY` 16, `GET_CONTEXT` 32, `SET_CONTEXT` 64, `SUSPEND_RESUME` 128 |
| `FILE` | 6 | `READ` 16 |
| `LIBRARY` | 7 | `READ` 16 |
| `LIBRARY_READER` | 8 | `READ` 16 |
| `LIBRARY_LIFECYCLE` | 9 | `READ` 16 |

`WIT_THREAD_SELF` (`~1`) — псевдохэндл текущего потока для вызовов, принимающих хэндл потока.

## Память

Защита: `NONE` 0, `READ` 1, `WRITE` 2. Исполняемые страницы создаются только через `CODE_MEMORY` в отдельной near-code арене с W^X; на K5 их заменяют объекты памяти.

| Область (x64) | Начало | Конец, не включая |
| --- | --- | --- |
| Компонент | `0x0000008000000000` | `0x0000008000200000` |
| Окно PE-образа | `0x0000008000100000` | — |
| Near-code арена | `0x0000008001000000` | `0x0000008040000000` |
| Динамическая арена данных | `0x0000010000000000` | `0x0000011000000000` |

Фиксированные адреса относятся к контролируемому профилю и не являются обещанием для приложений.

## TLS

Сырой TLS выбирается ядром и адресуется через FS (TPIDRRO_EL0 на ARM64); он принадлежит замороженной линии и уходит на K8:

| Смещение | Поле |
| --- | --- |
| 0 | self pointer |
| 8 | хэндл потока |
| 16 | начальный аргумент |
| 24 | 32-битный native last-error, записываемый пользователем |
| 28 | зарезервировано, 0 |
| 32 | данные приложения |

Compiler TLS адресуется через GS. Страница содержит указатель по смещению 0x58 на вектор слотов по смещению 0x80; слот 0 — главный образ, слоты 1–4 — DLL; данные главного образа начинаются со смещения 256 (`WIT_COMPILER_TLS_DATA_OFFSET`). Это не Windows TEB, а минимальная раскладка, совместимая с кодом MSVC.

## Квоты профиля

| Ресурс | Обычный профиль | Full-runtime профиль |
| --- | --- | --- |
| Потоки на компонент | 4 | 4 |
| Хэндлы | 16 | 32 |
| События | 4 | 16 |
| Owned pages | 128 | 2048 |
| Резервирования | 8 | 32 |
| Бюджет тиков | 10 | 3000 |
| Хэндлы в `OBJECT_WAIT` | 4 | 4 |
| APC в очереди потока | 4 | 4 |
| Stack leases | 4 | 4 |
| Глубина вложенных исключений | 4 | 4 |
| DLL в графе | 4 | 4 |
| Library readers | 16 | 16 |
| Байт в одном `DEBUG_WRITE` | 65536 | 65536 |
| Окружение процесса | 4096 UTF-16 единиц, 64 переменные | 4096 UTF-16 единиц, 64 переменные |
| Текущий каталог | 1025 байт UTF-8 | 1025 байт UTF-8 |
| PE-образ | 256 КиБ, 128 unwind | 1088 КиБ, 4096 unwind |
| PE-образ библиотеки | 256 КиБ, 320 unwind | 1088 КиБ, 4096 unwind |

## Стартовый контракт компонента

Первый поток получает в RCX (x0) адрес `WIT_USER_INFO` с неизменяемым `WitUserStartup`: версия равна `WIT_ABI_VERSION`, размер 24, хэндл консоли и адрес `WitUserImageInfo` (ноль для raw-фикстур). Стек выровнен под вызов ABI x64, адрес возврата равен нулю. Стартовый дескриптор корневой задачи по RFC-0011 §7.11 заменяет его на шаге K4.

## Загрузочный контракт v4

`WitBootInfo`, 112 байт, магия `0x574954424F4F5430`. Содержит архитектуру (`WIT_ARCH_X64` 1, `WIT_ARCH_ARM64` 2; загрузчик берёт её у `wit_arch_identity()`), карту памяти до 1024 регионов (только conventional RAM помечена usable), разделы образа ядра, 32-байтовое зерно энтропии, которое ядро потребляет и затирает, и до 128 экстентов неизменяемого загрузочного пакета `WITPAK01`. Все указатели identity-mapped. Подробности: `src/Kernel/include/witos/boot.h`.

## Исправленные комментарии заголовков

`library.h` утверждал, что TLS и ненулевые точки входа DLL не поддержаны, хотя с ABI v45–v48 они работают через lifecycle-планы, thread notifications и static DLL TLS. Комментарий исправлен вместе с появлением этого справочника; не поддержаны только forwarders.
