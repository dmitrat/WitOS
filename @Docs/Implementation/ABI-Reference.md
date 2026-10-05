# Справочник пользовательского ABI ядра WitOS

Версии: **user ABI v51**, **boot ABI v4**. Источник истины — заголовки `src/Kernel/include/witos/*.h`; этот документ их описывает и проверяется хостовым тестом: каждый `WIT_CALL_*` из `user_abi.h` обязан встречаться здесь. ABI экспериментальный и до P6.5 не заморожен; классы стабильности ниже — предложение для заморозки.

## Классы стабильности

| Класс | Смысл | Что произойдёт при заморозке P6.5 |
| --- | --- | --- |
| core | Примитивы ядра, не привязанные к конкретному runtime: память, потоки, события, время, случайность, хэндлы | Замораживаются первыми; дальше только добавление |
| runtime-pal | Возможности, нужные PAL upstream .NET: контексты, приостановка, stack leases, исключения, fatal, APC, references, код | Замораживаются вместе с первым гостевым CoreCLR; набор может расшириться |
| legacy | Домен тиков PIT; сохраняется ради существующих фикстур | Не входят в замороженный набор; удаляются после перевода фикстур |
| experimental | Временный транспорт bring-up: загрузочный пакет, файлы, DLL | Заменяются настоящими сервисами хранения и загрузчиком; совместимость не обещается |

## Соглашение о вызове

| ISA | Инструкция | Номер | Аргументы | Статус | Результат | После возврата |
| --- | --- | --- | --- | --- | --- | --- |
| x64 | `INT 0x80`, DPL3 | RAX | RCX, RDX, R8 | RAX | RDX | Остальные GPR и x87/SSE сохраняются; RFLAGS = 0x202 |
| ARM64 (план A2) | `SVC #0` | x8 | x0, x1, x2 | x0 | x1 | Будет описано в A2 |

Общие правила для всех вызовов:

- Вызов исполняется на единственном процессоре с запрещёнными прерываниями и не вытесняется. Блокирующие вызовы паркуют поток после полной валидации.
- Структуры передаются по указателю с точным размером; первые два поля любой структуры — `Version` и `Size`. Неизвестная версия даёт `UNSUPPORTED`, неверный размер — `INVALID_ARGUMENT`.
- Весь пользовательский буфер проверяется целиком до первой записи; при ошибке ни один байт назначения не меняется.
- Неиспользуемые аргументы и поля `Reserved` обязаны быть нулями.
- Возврат из `THREAD_CONTEXT_RESTORE`, `EXCEPTION_CONTINUE` и `EXCEPTION_UNWIND` при успехе не происходит: исполнение продолжается в переданном контексте.

## Статусы

| Значение | Имя | Типичная причина |
| --- | --- | --- |
| 0 | `WIT_STATUS_OK` | Успех |
| 1 | `WIT_STATUS_UNSUPPORTED` | Неизвестный вызов, версия или режим профиля |
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
| 15 | `WIT_STATUS_APC_PENDING` | Alertable-ожидание прервано очередью APC |
| 16 | `WIT_STATUS_NOT_FOUND` | Файл, модуль или символ отсутствует |
| 17 | `WIT_STATUS_INITIALIZATION_FAILED` | Пользовательский attach DLL вернул отказ |

## Вызовы

Аргументы указаны в порядке RCX, RDX, R8; результат возвращается в RDX.

| № | Вызов | Аргументы | Результат | Класс |
| --- | --- | --- | --- | --- |
| 0 | `WIT_CALL_QUERY` | — | версия ABI | core |
| 1 | `WIT_CALL_WRITE` | console handle, buffer, length ≤ 256 | записано байт; минимальный вывод без кучи для fatal-путей | core |
| 2 | `WIT_CALL_EXIT` | exit code | не возвращается | core |
| 3 | `WIT_CALL_CLOSE` | handle | 0 | core |
| 4 | `WIT_CALL_MEMORY_RESERVE` | size, alignment ≥ 4 КиБ | base | core |
| 5 | `WIT_CALL_MEMORY_COMMIT` | base, size, protection | 0 | core |
| 6 | `WIT_CALL_MEMORY_DECOMMIT` | base, size | 0 | core |
| 7 | `WIT_CALL_MEMORY_PROTECT` | base, size, protection | 0 | core |
| 8 | `WIT_CALL_MEMORY_RELEASE` | точная база резервирования | 0 | core |
| 9 | `WIT_CALL_THREAD_CREATE` | entry, argument, flags (`DETACHED`, `LIBRARY_NOTIFICATIONS`) | join handle или 0 для detached | core |
| 10 | `WIT_CALL_THREAD_YIELD` | — | 1, если выбран другой поток | core |
| 11 | `WIT_CALL_THREAD_EXIT` | exit code | не возвращается; в скоординированном профиле завершает компонент | core |
| 12 | `WIT_CALL_THREAD_JOIN` | join handle | exit code; хэндл потребляется | core |
| 13 | `WIT_CALL_CLOCK_READ` | — | тики PIT | legacy |
| 14 | `WIT_CALL_CLOCK_FREQUENCY` | — | 100 | legacy |
| 15 | `WIT_CALL_THREAD_SLEEP` | абсолютный дедлайн в тиках PIT | 0 | legacy |
| 16 | `WIT_CALL_EVENT_CREATE` | flags (`MANUAL_RESET`, `INITIAL_SIGNALED`) | event handle с правами wait и signal | core |
| 17 | `WIT_CALL_EVENT_SET` | handle | 0 | core |
| 18 | `WIT_CALL_EVENT_RESET` | handle | 0 | core |
| 19 | `WIT_CALL_EVENT_WAIT` | handle, дедлайн в тиках PIT | 0 | legacy |
| 20 | `WIT_CALL_MEMORY_QUERY` | buffer, 112, version 2 | 112 | core |
| 21 | `WIT_CALL_MONOTONIC_READ` | — | монотонный счётчик | core |
| 22 | `WIT_CALL_MONOTONIC_FREQUENCY` | — | частота, Гц | core |
| 23 | `WIT_CALL_SLEEP_UNTIL` | монотонный дедлайн | 0 | core |
| 24 | `WIT_CALL_EVENT_WAIT_UNTIL` | handle, монотонный дедлайн | 0 | core |
| 25 | `WIT_CALL_THREAD_CURRENT` | — | заимствованный хэндл текущего потока | core |
| 26 | `WIT_CALL_MEMORY_RESET` | base, size, 0 | 0 | core |
| 27 | `WIT_CALL_THREAD_QUERY` | buffer, 72, version 3 | 72 | core |
| 28 | `WIT_CALL_PROCESS_WRITE_BARRIER` | — | 0 | runtime-pal |
| 29 | `WIT_CALL_CPU_CACHE_SIZE` | — | байт крупнейшего кэша | runtime-pal |
| 30 | `WIT_CALL_EVENT_WAIT_ANY_UNTIL` | массив хэндлов, count ≤ 4, монотонный дедлайн | индекс победителя | core |
| 31 | `WIT_CALL_MEMORY_PRESSURE_EVENT` | — | event handle только для ожидания | runtime-pal |
| 32 | `WIT_CALL_MONOTONIC_QUERY` | buffer, 8, selector (`COUNTER` или `HZ`) | 8 | runtime-pal |
| 33 | `WIT_CALL_RANDOM` | buffer, size ≤ 65536, 0 | size | core |
| 34 | `WIT_CALL_THREAD_REFERENCE_DUPLICATE` | current pseudo или reference, указатель вывода, rights (0 — те же) | 8 | runtime-pal |
| 35 | `WIT_CALL_THREAD_REFERENCE_QUERY` | reference, buffer, 56 | 56 | runtime-pal |
| 36 | `WIT_CALL_THREAD_NATIVE_ID` | — | 32-битный native ID | runtime-pal |
| 37 | `WIT_CALL_OBJECT_WAIT` | `WitUserWaitRequest`, 32, 0 | индекс победителя | runtime-pal |
| 38 | `WIT_CALL_APC_QUEUE` | reference, callback, argument | 0 | runtime-pal |
| 39 | `WIT_CALL_APC_DEQUEUE` | buffer, 16, 0 | 16 | runtime-pal |
| 40 | `WIT_CALL_EVENT_CREATE_RIGHTS` | flags, rights, 0 | event handle | runtime-pal |
| 41 | `WIT_CALL_CONSOLE_WRITE` | `WitConsoleWriteRequest`, 40, 0 | 0; число байт пишется в `Written` | runtime-pal |
| 42 | `WIT_CALL_PROCESSOR_QUERY` | buffer, 4, 0 | 4 | runtime-pal |
| 43 | `WIT_CALL_THREAD_NAME_SET` | UTF-16 pointer, units < 128, flags 0 | 0 | runtime-pal |
| 44 | `WIT_CALL_THREAD_NAME_QUERY` | buffer, 280, version 1 | 0 | runtime-pal |
| 45 | `WIT_CALL_CPU_CONTEXT_QUERY` | buffer, 32, version 2 | 0 | runtime-pal |
| 46 | `WIT_CALL_THREAD_CONTEXT_GET` | reference или current, buffer, 720 | 0 | runtime-pal |
| 47 | `WIT_CALL_THREAD_SUSPEND` | reference | предыдущий счётчик | runtime-pal |
| 48 | `WIT_CALL_THREAD_RESUME` | reference | предыдущий счётчик | runtime-pal |
| 49 | `WIT_CALL_THREAD_CONTEXT_SET` | reference, buffer, 720 | 0 | runtime-pal |
| 50 | `WIT_CALL_THREAD_CONTEXT_RESTORE` | buffer, 720, version 2 | не возвращается при успехе | runtime-pal |
| 51 | `WIT_CALL_THREAD_CONTEXT_METADATA` | reference, buffer, 720 | 0 | runtime-pal |
| 52 | `WIT_CALL_STACK_LEASE_ACQUIRE` | reference, buffer, 48 | 0 | runtime-pal |
| 53 | `WIT_CALL_STACK_LEASE_QUERY` | token, buffer, 48 | 0 | runtime-pal |
| 54 | `WIT_CALL_STACK_LEASE_RELEASE` | token | 0 | runtime-pal |
| 55 | `WIT_CALL_EXCEPTION_REGISTER` | callback или 0, version 1, flags 0 | 0 | runtime-pal |
| 56 | `WIT_CALL_EXCEPTION_QUERY` | token, buffer, 768 | 0 | runtime-pal |
| 57 | `WIT_CALL_EXCEPTION_CONTINUE` | token, `WitThreadContext`, 720 | не возвращается при успехе | runtime-pal |
| 58 | `WIT_CALL_EXCEPTION_REJECT` | token | не возвращается; компонент завершается | runtime-pal |
| 59 | `WIT_CALL_EXCEPTION_BEGIN` | `WitThreadContext`, 720, 32-битный код | token | runtime-pal |
| 60 | `WIT_CALL_FATAL_ARM` | 32-битный код по умолчанию | 0 | runtime-pal |
| 61 | `WIT_CALL_FATAL_REPORT` | `WitUserFatalInfo`, 872, version 1 | 0 | runtime-pal |
| 62 | `WIT_CALL_EXCEPTION_UNWIND` | token, `WitUserExceptionTransfer`, 736 | не возвращается при успехе | runtime-pal |
| 63 | `WIT_CALL_THREAD_COMPLETE` | exit code | не возвращается; упорядоченное завершение после TLS и runtime-уведомлений | core |
| 64 | `WIT_CALL_THREAD_CREATE_REFERENCE` | `WitThreadCreateRequest`, 48, 0 | reference handle | runtime-pal |
| 65 | `WIT_CALL_CODE_MEMORY` | `WitCodeMemoryRequest`, 64, 0 | база для `RESERVE`, иначе 0 | runtime-pal |
| 66 | `WIT_CALL_FILE` | `WitFileRequest`, 64, 0 | хэндл, байты или позиция | experimental |
| 67 | `WIT_CALL_STORAGE_QUERY` | `WitStorageQuery`, 64, 0 | 1056 или 0 в конце списка | experimental |
| 68 | `WIT_CALL_LIBRARY` | `WitLibraryRequest`, 64, 0 | зависит от операции | experimental |
| 69 | `WIT_CALL_PROCESS_STATE` | `WitProcessStateRequest`, 64, 0 | размер значения, блока или каталога | experimental |

Следующий свободный номер: **70**.

### Операции составных вызовов

| Вызов | Операции |
| --- | --- |
| `CODE_MEMORY` | `RESERVE` 0, `ALIAS` 1, `PROTECT` 2, `PUBLISH` 3, `MAP_SPARSE` 4, `RESET_SPARSE` 5, `VALIDATE` 6 |
| `FILE` | `OPEN` 0, `LENGTH` 1, `READ_AT` 2, `READ` 3, `SEEK` 4 |
| `STORAGE_QUERY` | `STAT` 0, `LIST` 1 |
| `LIBRARY` | `LOAD` 0, `SYMBOL` 1, `UNLOAD` 2, `QUERY` 3, `FIND` 4, `PATH` 5, `ACQUIRE_READER` 6, `RELEASE_READER` 7, `QUERY_READER` 8, `FINISH_LIFECYCLE` 9, `SHUTDOWN` 10, `THREAD_ENTER` 11, `THREAD_LEAVE` 12, `MODULE_PATH` 13 |
| `PROCESS_STATE` | `ENV_GET` 0, `ENV_SET` 1, `ENV_BLOCK` 2, `CWD_GET` 3, `CWD_SET` 4 |

`LIBRARY.MODULE_PATH` (ABI v51) не принимает хэндл: по адресу в `Ordinal` возвращает `WitLibraryPath` модуля, чей образ его содержит (главного образа компонента или загруженной библиотеки), а с флагом `MAIN_IMAGE` 1 и нулевым адресом — путь главного образа. `NOT_FOUND`, если адрес не принадлежит модулю или главный образ создан не из файла пакета. Это запрос неизменяемых записей, разрешённый в любой момент, в том числе во время lifecycle.

`PROCESS_STATE` (ABI v50) хранит в ядре окружение и текущий каталог компонента, общие для всех его модулей. Окружение — блок записей `Name=Value\0` в порядке установки с финальным `\0`; имена сравниваются со свёрткой регистра ASCII и не содержат `=` и NUL; повторная установка переносит запись в конец. Текущий каталог — канонический UTF-8 от `/`; `CWD_SET` принимает уже разрешённый путь и только каталог пакета (`NOT_FOUND` для отсутствующего, `WRONG_TYPE` для файла). Вывод копируется только целиком, результат — его размер в любом случае; отказ не меняет ни состояние, ни вывод. Создатель компонента может задать переменные до запуска; новый компонент начинает с пустым окружением и `/`.

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
| `WitConsoleWriteRequest` | `console_info.h` | 40 | 1 |
| `WitCodeMemoryRequest` | `code_memory.h` | 64 | 1 |
| `WitFileRequest` | `file_io.h` | 64 | 1 |
| `WitStorageQuery` | `storage_query.h` | 64 | 1 |
| `WitStorageInfo` | `storage_query.h` | 1056 | 1 |
| `WitLibraryRequest` | `library.h` | 64 | 2 |
| `WitLibraryInfo` | `library.h` | 40 | 2 |
| `WitLibraryPath` | `library.h` | 1040 | 2 |
| `WitLibraryLifecycle` | `library.h` | 192 | 2 |
| `WitProcessStateRequest` | `process_state.h` | 64 | 1 |

`WitThreadContext` и производные от него структуры содержат регистры x64 и образ FXSAVE64. Для ARM64 эти структуры получат отдельную регистровую часть с тем же префиксом `Version`, `Size`, `ThreadId`, `StackLow`, `StackHigh`, `State`, `Flags`; это решение закрепляется в A2.

## Хэндлы и права

Токен хэндла: `owner << 32 | generation << 16 | (slot + 1)`. Поколение растёт при каждом переиспользовании слота, поэтому устаревший токен никогда не совпадает с новым объектом.

| Вид | Значение | Права |
| --- | --- | --- |
| `CONSOLE` | 1 | `WRITE` 1 |
| `SELF` | 2 | — |
| `THREAD` | 3 | `JOIN` 2 |
| `EVENT` | 4 | `WAIT` 4, `SIGNAL` 8 |
| `THREAD_REFERENCE` | 5 | `WAIT` 4, `QUERY` 16, `GET_CONTEXT` 32, `SET_CONTEXT` 64, `SUSPEND_RESUME` 128 |
| `FILE` | 6 | `READ` 16 |
| `LIBRARY` | 7 | `READ` 16 |
| `LIBRARY_READER` | 8 | `READ` 16 |
| `LIBRARY_LIFECYCLE` | 9 | `READ` 16 |

`WIT_THREAD_REFERENCE_CURRENT` (`~1`) — псевдохэндл текущего потока для вызовов, принимающих reference.

## Память

Защита: `NONE` 0, `READ` 1, `WRITE` 2. Исполняемые страницы создаются только через `CODE_MEMORY` в отдельной near-code арене с W^X.

| Область (x64) | Начало | Конец, не включая |
| --- | --- | --- |
| Компонент | `0x0000008000000000` | `0x0000008000200000` |
| Окно PE-образа | `0x0000008000100000` | — |
| Near-code арена | `0x0000008001000000` | `0x0000008040000000` |
| Динамическая арена данных | `0x0000010000000000` | `0x0000011000000000` |

Фиксированные адреса относятся к контролируемому профилю и не являются обещанием для приложений.

## TLS

Сырой TLS выбирается ядром и адресуется через FS:

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
| Хэндлы в `EVENT_WAIT_ANY_UNTIL` и `OBJECT_WAIT` | 4 | 4 |
| APC в очереди потока | 4 | 4 |
| Stack leases | 4 | 4 |
| Глубина вложенных исключений | 4 | 4 |
| DLL в графе | 4 | 4 |
| Library readers | 16 | 16 |
| Окружение процесса | 4096 UTF-16 единиц, 64 переменные | 4096 UTF-16 единиц, 64 переменные |
| Текущий каталог | 1025 байт UTF-8 | 1025 байт UTF-8 |
| PE-образ | 256 КиБ, 128 unwind | 1088 КиБ, 4096 unwind |
| PE-образ библиотеки | 256 КиБ, 320 unwind | 1088 КиБ, 4096 unwind |

## Стартовый контракт компонента

Первый поток получает в RCX адрес `WIT_USER_INFO` с неизменяемым `WitUserStartup`: версия равна `WIT_ABI_VERSION`, размер 24, хэндл консоли и адрес `WitUserImageInfo` (ноль для raw-фикстур). Стек выровнен под вызов ABI x64, адрес возврата равен нулю.

## Загрузочный контракт v4

`WitBootInfo`, 112 байт, магия `0x574954424F4F5430`. Содержит архитектуру (`WIT_ARCH_X64` 1, `WIT_ARCH_ARM64` 2; загрузчик берёт её у `wit_arch_identity()`), карту памяти до 1024 регионов (только conventional RAM помечена usable), разделы образа ядра, 32-байтовое зерно энтропии, которое ядро потребляет и затирает, и до 128 экстентов неизменяемого загрузочного пакета `WITPAK01`. Все указатели identity-mapped. Подробности: `src/Kernel/include/witos/boot.h`.

## Исправленные комментарии заголовков

`library.h` утверждал, что TLS и ненулевые точки входа DLL не поддержаны, хотя с ABI v45–v48 они работают через lifecycle-планы, thread notifications и static DLL TLS. Комментарий исправлен вместе с появлением этого справочника; не поддержаны только forwarders.
