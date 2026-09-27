# BootTo.NET: проверенные выводы для WitOS

**Дата:** 2026-09-27.
**База WitOS:** 0.0.26 / ABI v13.
**Решение:** использовать проект как источник сценариев проверки и инженерных наблюдений. Сохранять upstream NativeAOT/CoreCLR и существующий стенд WitOS.

## Что именно проверено

Изучены локальные исходники C:\Workspace\@Misc\BootTo.NET и draft @Docs/WitOS-BootToNET-Lessons.md. Draft оставлен без изменений: часть его рекомендаций относится к уже завершённым M0/M1.

В локальном снимке нет .git. Семь ключевых файлов побайтово совпали с upstream-коммитом [37297d3888fe28e64cd3b969d7ea288c9a6f28e8](https://github.com/nifanfa/BootTo.NET/commit/37297d3888fe28e64cd3b969d7ea288c9a6f28e8): Directory.Build.targets, Qemu.targets, EfiApplication.c, CoreLib.cs, TaskScheduler.cs, GarbageCollectionValidation.cs и NativeLib/Runtime.asm. Это подтверждает происхождение изученных файлов, но не устанавливает ревизию каждого файла всего скачанного дерева.

Локальная папка IL2LLVM содержит готовые EXE/DLL, не исходники компилятора. Дополнительно прочитаны Runtime.cs, Methods.cs, MethodContext.cs и Instructions/Exceptions.cs из [IL2LLVM e55e087845985aa812c9409cd54e35cef80aef34](https://github.com/nifanfa/IL2LLVM/tree/e55e087845985aa812c9409cd54e35cef80aef34). Соответствие этого compiler source локальному IL2LLVM.exe не установлено.

BootTo.NET не запускался и его готовый компилятор не исполнялся. Это целевой source review ключевых подсистем, а не подтверждение всех демонстраций автора или полный аудит большого LanguageFeatureValidation.cs. Собственные новые тесты WitOS, описанные ниже, реально опубликованы и выполнены стандартным NativeAOT.

## Почему BootTo.NET быстрее достигает C# и приложений

| Участок | Что делает BootTo.NET | Значение для WitOS |
| --- | --- | --- |
| Компиляция | NoStdLib, удаление стандартных reference assemblies, собственная CoreLib, IL2LLVM | TargetFramework net10.0 сам по себе не означает стандартную BCL или upstream runtime |
| Вход | EfiMain сохраняет AllocatePool/FreePool и вызывает managed_EfiMain | Очень короткая цепочка, но она не проходит наши RhInitialize/TypeManager/GC bootstrap |
| Память | Нативные malloc/free вызывают UEFI Boot Services | Firmware обеспечивает значительную часть системного слоя |
| Устройства | GOP, EFI filesystem, TCP/UDP и DXE-протоколы | Это готовая firmware-платформа; после ExitBootServices эти вызовы не становятся драйверами ОС |
| Исполнение | Пользовательская программа и runtime работают как EFI-приложение | Нет проверяемого аналога нашей ring-3 изоляции и process-local механизмов |
| Потоки | Thread содержит Sleep; Tasks используют pollers и EFI events | Параметр QEMU -smp 2 не доказывает многопоточный/SMP runtime |

Цепочка компиляции подтверждена в [Directory.Build.targets](https://github.com/nifanfa/BootTo.NET/blob/37297d3888fe28e64cd3b969d7ea288c9a6f28e8/Directory.Build.targets). Связь allocator с firmware видна непосредственно в [EfiApplication.c](https://github.com/nifanfa/BootTo.NET/blob/37297d3888fe28e64cd3b969d7ea288c9a6f28e8/EfiApplication/EfiApplication.c).

В просмотренном дереве ExitBootServices найден как объявление функции в UEFI binding, но не как вызов. Активные allocator, таймеры, scheduler и IO продолжают обращаться к Boot Services. У WitOS эта граница уже пройдена, память и исполнение принадлежат ядру.

Doom/Quake также не следует считать проверкой совместимости всей .NET: EFI-проект линкует отдельные нативные библиотеки игр, а C# предоставляет оболочки и адаптеры. Это полезные интеграционные нагрузки, но другая цель.

## Что действительно полезно

### 1. Проверки корней GC — высокая ценность сейчас

[GarbageCollectionValidation.cs](https://github.com/nifanfa/BootTo.NET/blob/37297d3888fe28e64cd3b969d7ea288c9a6f28e8/ConsoleApp1/GarbageCollectionValidation.cs) проверяет то, чего не видно по одному успешному new: ссылки в базовых классах и вложенных структурах, массивы структур и многомерные массивы, циклы, generic statics, коллекции и динамические строки после повторных сборок.

Это особенно полезно перед подключением настоящих NativeAOT GC-info и root enumeration. Для WitOS приоритетны пограничные случаи runtime: interior managed refs, корни в funclets, TLS, native/managed transitions. Полную матрицу IL-инструкций собственного компилятора переносить сейчас не нужно.

### 2. GC и исключения должны иметь согласованный жизненный цикл

CoreLib использует собственные GCFrame/GCRoot и глобальную цепочку зарегистрированных корней. В ExceptionRuntime.Throw перед longjmp выполняется GCHeap.UnwindTo. Это конкретная демонстрация того, что переход к обработчику должен согласованно убрать покинутые GC frames.

В [Methods.cs IL2LLVM](https://github.com/nifanfa/IL2LLVM/blob/e55e087845985aa812c9409cd54e35cef80aef34/IL2LLVM/Methods.cs) видна генерация GC push/pop вокруг промежуточных объектов; [Exceptions.cs](https://github.com/nifanfa/IL2LLVM/blob/e55e087845985aa812c9409cd54e35cef80aef34/IL2LLVM/Instructions/Exceptions.cs) генерирует rethrow/filter/leave/finally-переходы.

Для WitOS это требование к совместной проверке stack walking, exception funclets и GC roots. При переносе нельзя закрывать эти подсистемы независимыми успешными заглушками.

### 3. Минимальный профиль и одна сквозная нагрузка

BootTo.NET добивается наблюдаемого результата на ограниченном наборе возможностей. Полезно применить этот принцип к следующему этапу WitOS: явно выбрать bring-up профиль upstream runtime и пройти цепочку полного образа до реального managed entry.

Следует исследовать исключение необязательной диагностики/трассировки из первого профиля, сверив native/managed совместимость на закреплённых исходниках. Это исследовательская задача, не уже выполненное сокращение зависимостей. GC, необходимые transitions и unwind-инфраструктуру отключать ради видимости запуска нельзя.

Из наличия 99 неразрешённых символов не следует необходимость реализовать все Windows API: часть может относиться к отключаемым функциям, часть — к привязкам существующих PAL-операций. Проверять нужно реальный выбранный workload.

### 4. Firmware bindings и workloads — сохранить на будущее

UEFI bindings, чтение GOP/EFI-протоколов и узкие native file adapters полезны как справочник для будущих задач устройств и IO. NativeFileIO показывает практичность ограниченного адаптера под конкретную программу, но хранит файл целиком в памяти и не задаёт полноценную файловую семантику ОС.

Для нынешнего M3 это не критический путь. Новый firmware harness для графики/сети сейчас дублировал бы уже работающую инфраструктуру и потребовал бы ещё одного PAL.

## Что не переносить

- **GCHeap.** Это собственный неперемещающий tracing mark-and-sweep с compiler-owned root frames и таблицами смещений ссылок. MarkObject ищет содержащую указатель allocation в списке; это не реализация интерфейсов и GC-info upstream NativeAOT. Глобальные GC/exception цепочки не дают готового решения для наших потоков.
- **setjmp/longjmp как замену .NET unwinder.** [Runtime.asm](https://github.com/nifanfa/BootTo.NET/blob/37297d3888fe28e64cd3b969d7ea288c9a6f28e8/NativeLib/Runtime.asm) обслуживает конкретный протокол их компилятора. Это также не полный контекст произвольного прерывания для ядра.
- **__chkstk из этого файла.** Его тело — один ret. Такое закрытие символа обошло бы проверку роста стека и противоречило бы нашим guard pages и требованиям к настоящим compiler helpers.
- **TaskScheduler.Enter/Exit как Monitor.** [Scheduler](https://github.com/nifanfa/BootTo.NET/blob/37297d3888fe28e64cd3b969d7ea288c9a6f28e8/ConsoleApp1/TaskScheduler.cs) использует глобальную глубину блокировки и RaiseTPL/RestoreTPL, а не владение конкретным mutex потоком.
- **Сырые callback-context указатели.** System/Timers/Timer.cs передаёт firmware адрес поля объекта из fixed-блока. Перенос в среду с moving GC требует отдельного договора о закреплении/GCHandle, удержании объекта и снятии callbacks.
- **RTC как монотонные часы.** В fallback Stopwatch применяется DateTime.UtcNow.Ticks. Наш HPET/deadline-домен следует сохранить; календарные часы — отдельный контракт.

Источники GC/EH: [CoreLib.cs, GCHeap и ExceptionRuntime](https://github.com/nifanfa/BootTo.NET/blob/37297d3888fe28e64cd3b969d7ea288c9a6f28e8/ConsoleApp1/CoreLib.cs#L1788).

## Можно ли взять их стенд

Сейчас рекомендую сохранить WitOS.Dev и заимствовать только отдельные идеи.

| BootTo.NET | Уже имеющийся WitOS |
| --- | --- |
| F5/MSBuild, GUI, устройства, интерактивные демонстрации | Командный runner, headless QEMU и проверяемые сценарии |
| QEMU/firmware хранятся готовыми бинарниками | Пакет скачивается с проверкой закреплённого хэша |
| Firmware vars через snapshot | Свежий vars-файл для каждого теста |
| Writable FAT image и обратная распаковка в Drive | Изолированные ignored artifacts и неизменяемый boot disk |
| GUI/аудио/сеть/FTP и ожидание клавиши | Serial markers + exit status + panic/fault/timeout validation |

Конкретные поправки к draft:

1. README обещает автоматический fallback WHPX→TCG и проверку активного hypervisor. Изученный [Qemu.targets](https://github.com/nifanfa/BootTo.NET/blob/37297d3888fe28e64cd3b969d7ea288c9a6f28e8/Qemu.targets) задаёт WHPX и проверяет лишь его наличие в списке accelerators. Fallback в этом коде отсутствует.
2. SyncQemuDiskImage распаковывает изменённый диск обратно в Drive с перезаписью. Для наших регрессий это нежелательная зависимость теста от предыдущего запуска.
3. RunQemu публикует FTP-порты и запускает SDL/аудио. Это dev-demo профиль, не готовый CI-контракт.
4. В исходном снимке IL2LLVM — бинарник без указанной source revision. Наши compiler/source/package pins следует сохранить.
5. Предложенные в draft повторные M0 boot/image/QEMU эксперименты уже реализованы в WitOS. Возвращаться к ним ради этого проекта не требуется.

В будущем F5-профиль или ускоренный интерактивный запуск могут быть полезны отдельно от детерминированной TCG-регрессии. Они не разблокируют текущие GC/startup зависимости.

## Прямое копирование и лицензии

При проверке GitHub API 2026-09-27 у обоих проектов поле license было null; в локальном корне BootTo.NET общего LICENSE не найдено. В отдельных каталогах есть лицензии QEMU/7-Zip/драйверов и собственные notices. Они не устанавливают единые условия для CoreLib, компилятора и тестов.

Прямое включение этих исходников в WitOS отложено до проверки условий конкретных файлов. Новые тесты ниже написаны самостоятельно по общим семантическим требованиям; чужой код и бинарники в репозиторий WitOS не добавлены.

## Уже применённый результат

В стандартный Windows NativeAOT probe добавлены две собственные группы:

- GcCompositeRoots: отдельный корень в базовом поле, вложенная структура/цикл, многомерный массив структур, jagged arrays, раздельные statics двух generic instantiations и interior ref как единственная доступная ссылка на массив. NoInlining-фабрики отделяют временные ссылки; сборка вызывается с запросом compaction, но фактическое перемещение объекта не предполагается.
- GcRootsAcrossUnwind: сборка в exception filter, finally и catch; порядок first-pass filter перед cleanup, независимый корень локальной структуры в finally и rethrow после вложенного исключения.

Каждая принудительная сборка проверяет изменение счётчика коллекций. Это hosted-эталон на обычной CoreLib .NET 10.0.8, не доказательство гостевого managed execution.

Дополнительно исправлена обнаруженная при проверке зависимость NativeAOT publish от внешнего PATH: каталог vswhere передаётся только дочернему процессу, без изменения системного окружения.

Следующий runtime-этап остаётся RhInitialize/InitDLL, с явным bring-up профилем и сквозным workload. Отдельный firmware-runtime fork для этого не нужен.

Локальная проверка: Release build — без ошибок и предупреждений; runtime-audit — 48 файлов; runtime-probe — все 8 групп; runtime-target — все 4 группы и ожидаемые строгие ошибки неполной линковки; test — все 18 сценариев QEMU.
