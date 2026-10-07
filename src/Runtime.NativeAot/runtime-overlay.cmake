# Loaded only by CMAKE_PROJECT_CoreCLR_INCLUDE; upstream files stay unchanged.
# Defer until the upstream project has created the actual runtime target.
get_filename_component(WITOS_SOURCE_ROOT "${CMAKE_CURRENT_LIST_DIR}/../.." ABSOLUTE)
add_subdirectory("${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/config-probe" "${CMAKE_BINARY_DIR}/witos-config-build")
function(witos_select_gc_environment)
    if(NOT TARGET Runtime.WorkstationGC OR NOT CLR_CMAKE_TARGET_WIN32 OR NOT CLR_CMAKE_TARGET_ARCH_AMD64)
        message(FATAL_ERROR "WitOS source overlay requires the pinned Windows AMD64 workstation runtime")
    endif()
    # Explicit initial WitOS diagnostics profile; Windows reference is unchanged.
    # Use upstream's supported disable switch across all workstation objects.
    # Supported upstream target properties: no Windows CFG loader in this profile.
    set_property(TARGET Runtime.WorkstationGC aotminipal PROPERTY CLR_CONTROL_FLOW_GUARD OFF)
    set_property(TARGET Runtime.WorkstationGC aotminipal PROPERTY CLR_EH_CONTINUATION OFF)
    target_compile_definitions(Runtime.WorkstationGC PRIVATE NO_STRESS_LOG)
    target_include_directories(Runtime.WorkstationGC BEFORE PRIVATE "${WITOS_SOURCE_ROOT}/artifacts/runtime-config/include" "${WITOS_SOURCE_ROOT}/artifacts/runtime-unwind")
    get_target_property(sources Runtime.WorkstationGC SOURCES)
    set(windows_gc "${CLR_DIR}/gc/windows/gcenv.windows.cpp")
    set(matches 0)
    foreach(source IN LISTS sources)
        if(source STREQUAL windows_gc)
            math(EXPR matches "${matches} + 1")
        endif()
    endforeach()
    if(NOT matches EQUAL 1)
        message(FATAL_ERROR "Expected exactly one upstream Windows GC environment source")
    endif()
    list(REMOVE_ITEM sources "${windows_gc}")
    list(APPEND sources "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/X64/native_exception_x64.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/seh_security.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/seh_scope.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/seh_validation.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/failfast_exception.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/native_exception.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.Pal.Win32/X64/native_exception.asm"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/unwind_scope.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/unwind_guest.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/unwind_checked.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/unwind_validation.witos.cpp"
        "${WITOS_SOURCE_ROOT}/artifacts/runtime-unwind/unwinder.checked.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.Pal.Win32/X64/native_unwind.asm"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/pal_context.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/native_suspend.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.Pal.Win32/X64/native_suspend.asm"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/pal_context_storage.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/gc_policy.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/X64/gc_policy.asm"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/native_com.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.Pal.Win32/X64/native_com.asm"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/native_diagnostics.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.Pal.Win32/X64/native_diagnostics.asm"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/pal_thread_name.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/native_module.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.Pal.Win32/X64/native_module.asm"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/native_encoding.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.Pal.Win32/X64/native_encoding.asm"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/native_console.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/native_processor.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.Pal.Win32/X64/native_console.asm"
        "${WITOS_SOURCE_ROOT}/src/Runtime.Pal.Win32/X64/native_processor.asm"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/native_wait.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/native_activation.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.Pal.Win32/X64/native_wait.asm"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/native_thread_handles.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/native_thread_create.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.Pal.Win32/X64/native_thread_handles.asm"
        "${WITOS_SOURCE_ROOT}/src/Runtime.Pal.Win32/X64/native_thread_create.asm"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/native_services.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.Pal.Win32/X64/native_services.asm"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/native_memory.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.Pal.Win32/X64/native_memory.asm"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/native_random.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.Pal.Win32/X64/native_random.asm"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/security_cookie.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/security_handler.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/X64/security_cookie.asm"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/native_format.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/format_fixed.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/X64/native_format.asm"
        "${WITOS_SOURCE_ROOT}/artifacts/runtime-config/source/log.openlibm.c"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/native_math.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/gcenv.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/gc_affinity.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/gc_events.witos.cpp" "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/gc_time.witos.cpp")
    set(crst "${CLR_DIR}/nativeaot/Runtime/Crst.cpp")
    list(FIND sources "${crst}" crst_index)
    if(crst_index EQUAL -1)
        message(FATAL_ERROR "Pinned Crst source missing from workstation runtime")
    endif()
    list(REMOVE_ITEM sources "${crst}")
    list(APPEND sources "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/crst.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/native_new.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/tls.witos.cpp")
    foreach(pal_file IN ITEMS PalCommon.cpp PalMinWin.cpp)
        set(old_pal "${CLR_DIR}/nativeaot/Runtime/windows/${pal_file}")
        list(FIND sources "${old_pal}" pal_index)
        if(pal_index EQUAL -1)
            message(FATAL_ERROR "Pinned Windows PAL source missing: ${pal_file}")
        endif()
        list(REMOVE_ITEM sources "${old_pal}")
    endforeach()
    list(APPEND sources "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/pal.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/pal_memory.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/pal_init.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/pal_events.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/pal_threads.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/pal_attach.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/pal_hijack.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.Native/thread.c"
        "${WITOS_SOURCE_ROOT}/src/Runtime.Native/image.c"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/pal_module.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/pal_environment.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/pal_error.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/fatal.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/native_clock.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.Pal.Win32/X64/native_clock.asm"
        "${WITOS_SOURCE_ROOT}/src/Runtime.Pal.Win32/X64/native_error.asm"
        "${WITOS_SOURCE_ROOT}/src/Runtime.Pal.Win32/X64/native_environment.asm"
        "${WITOS_SOURCE_ROOT}/src/Kernel.Arch.X64/chkstk.asm")
    set_source_files_properties("${WITOS_SOURCE_ROOT}/src/Runtime.Native/thread.c"
        "${WITOS_SOURCE_ROOT}/src/Runtime.Native/image.c" TARGET_DIRECTORY Runtime.WorkstationGC PROPERTIES COMPILE_OPTIONS "/std:c17")
    file(STRINGS "${WITOS_SOURCE_ROOT}/src/Kernel/include/witos/user_abi.h" error_line
        REGEX "^#define WIT_TLS_LAST_ERROR_OFFSET [0-9]+U$")
    list(LENGTH error_line error_lines)
    if(NOT error_lines EQUAL 1)
        message(FATAL_ERROR "Missing or ambiguous native last-error ABI offset")
    endif()
    string(REGEX REPLACE "^#define WIT_TLS_LAST_ERROR_OFFSET ([0-9]+)U$" "\\1" error_offset "${error_line}")
    file(STRINGS "${WITOS_SOURCE_ROOT}/src/Kernel/include/witos/user_abi.h" fatal_line REGEX "^#define WIT_CALL_FATAL_ARM [0-9]+U$")
    list(LENGTH fatal_line fatal_lines)
    if(NOT fatal_lines EQUAL 1)
        message(FATAL_ERROR "Missing or ambiguous fatal arm ABI")
    endif()
    string(REGEX REPLACE "^#define WIT_CALL_FATAL_ARM ([0-9]+)U$" "\\1" fatal_call "${fatal_line}")
    # The header is a configure input: a changed constant regenerates the include, and the include changes only
    # when its content does, so the assembly objects that depend on it rebuild exactly then.
    set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${WITOS_SOURCE_ROOT}/src/Kernel/include/witos/user_abi.h")
    file(MAKE_DIRECTORY "${CMAKE_BINARY_DIR}/witos-abi")
    file(WRITE "${CMAKE_BINARY_DIR}/witos-abi/user_abi.inc.in"
        "WIT_TLS_LAST_ERROR_OFFSET EQU ${error_offset}\nWIT_CALL_FATAL_ARM EQU ${fatal_call}\n")
    configure_file("${CMAKE_BINARY_DIR}/witos-abi/user_abi.inc.in" "${CMAKE_BINARY_DIR}/witos-abi/user_abi.inc" COPYONLY)
    set_source_files_properties("${WITOS_SOURCE_ROOT}/src/Runtime.Pal.Win32/X64/native_exception.asm"
        "${WITOS_SOURCE_ROOT}/src/Runtime.Pal.Win32/X64/native_suspend.asm"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/X64/gc_policy.asm"
        "${WITOS_SOURCE_ROOT}/src/Runtime.Pal.Win32/X64/native_com.asm"
        "${WITOS_SOURCE_ROOT}/src/Runtime.Pal.Win32/X64/native_diagnostics.asm"
        "${WITOS_SOURCE_ROOT}/src/Runtime.Pal.Win32/X64/native_module.asm"
        "${WITOS_SOURCE_ROOT}/src/Runtime.Pal.Win32/X64/native_encoding.asm"
        "${WITOS_SOURCE_ROOT}/src/Runtime.Pal.Win32/X64/native_console.asm"
        "${WITOS_SOURCE_ROOT}/src/Runtime.Pal.Win32/X64/native_processor.asm"
        "${WITOS_SOURCE_ROOT}/src/Runtime.Pal.Win32/X64/native_wait.asm"
        "${WITOS_SOURCE_ROOT}/src/Runtime.Pal.Win32/X64/native_thread_handles.asm"
        "${WITOS_SOURCE_ROOT}/src/Runtime.Pal.Win32/X64/native_thread_create.asm"
        "${WITOS_SOURCE_ROOT}/src/Runtime.Pal.Win32/X64/native_services.asm"
        "${WITOS_SOURCE_ROOT}/src/Runtime.Pal.Win32/X64/native_memory.asm"
        "${WITOS_SOURCE_ROOT}/src/Runtime.Pal.Win32/X64/native_random.asm"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/X64/security_cookie.asm"
        "${WITOS_SOURCE_ROOT}/src/Runtime.Pal.Win32/X64/native_error.asm"
        "${WITOS_SOURCE_ROOT}/src/Runtime.Pal.Win32/X64/native_clock.asm"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/X64/native_format.asm"
        "${WITOS_SOURCE_ROOT}/src/Runtime.Pal.Win32/X64/native_environment.asm"
        "${WITOS_SOURCE_ROOT}/src/Kernel.Arch.X64/chkstk.asm"
        TARGET_DIRECTORY Runtime.WorkstationGC PROPERTIES LANGUAGE ASM_MASM
        COMPILE_OPTIONS "/I${CMAKE_BINARY_DIR}/witos-abi" OBJECT_DEPENDS "${CMAKE_BINARY_DIR}/witos-abi/user_abi.inc")
    set(old_config "${CLR_DIR}/nativeaot/Runtime/RhConfig.cpp")
    list(FIND sources "${old_config}" config_index)
    if(config_index EQUAL -1)
        message(FATAL_ERROR "Pinned RhConfig source missing")
    endif()
    list(REMOVE_ITEM sources "${old_config}")
    list(APPEND sources "${WITOS_SOURCE_ROOT}/artifacts/runtime-config/source/rhconfig.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/crt_config.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/crt_exit.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.Native/library_lifecycle.c"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/crt_memory.witos.c")
    set_source_files_properties("${WITOS_SOURCE_ROOT}/src/Runtime.Native/library_lifecycle.c" "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/crt_memory.witos.c"
        TARGET_DIRECTORY Runtime.WorkstationGC PROPERTIES COMPILE_OPTIONS "/std:c17;/O1")
    foreach(pair IN ITEMS "GCHelpers.cpp|gchelpers.witos.cpp" "FinalizerHelpers.cpp|finalizerhelpers.witos.cpp" "gcenv.ee.cpp|gcenv.ee.witos.cpp")
        string(REPLACE "|" ";" parts "${pair}")
        list(GET parts 0 original)
        list(GET parts 1 adapted)
        set(old_source "${CLR_DIR}/nativeaot/Runtime/${original}")
        list(FIND sources "${old_source}" source_index)
        if(source_index EQUAL -1)
            message(FATAL_ERROR "Pinned runtime startup diagnostic source missing: ${original}")
        endif()
        list(REMOVE_ITEM sources "${old_source}")
        list(APPEND sources "${WITOS_SOURCE_ROOT}/artifacts/runtime-config/source/${adapted}")
    endforeach()
    set(old_workstation "${CLR_DIR}/gc/gcwks.cpp")
    list(FIND sources "${old_workstation}" workstation_index)
    if(workstation_index EQUAL -1)
        message(FATAL_ERROR "Pinned workstation collector missing")
    endif()
    list(REMOVE_ITEM sources "${old_workstation}")
    list(APPEND sources "${WITOS_SOURCE_ROOT}/artifacts/runtime-config/source/gcwks.witos.cpp")
    target_include_directories(Runtime.WorkstationGC PRIVATE "${CLR_DIR}/gc")
    set(old_allocheap "${CLR_DIR}/nativeaot/Runtime/allocheap.cpp")
    list(FIND sources "${old_allocheap}" allocheap_index)
    if(allocheap_index EQUAL -1)
        message(FATAL_ERROR "Pinned AllocHeap source missing")
    endif()
    list(REMOVE_ITEM sources "${old_allocheap}")
    list(APPEND sources "${WITOS_SOURCE_ROOT}/artifacts/runtime-config/source/allocheap.witos.cpp")
    set(old_threadstore "${CLR_DIR}/nativeaot/Runtime/threadstore.cpp")
    list(FIND sources "${old_threadstore}" threadstore_index)
    if(threadstore_index EQUAL -1)
        message(FATAL_ERROR "Pinned ThreadStore source missing")
    endif()
    list(REMOVE_ITEM sources "${old_threadstore}")
    list(APPEND sources "${WITOS_SOURCE_ROOT}/artifacts/runtime-config/source/threadstore.witos.cpp")
    set(old_debugheader "${CLR_DIR}/nativeaot/Runtime/DebugHeader.cpp")
    list(FIND sources "${old_debugheader}" debugheader_index)
    if(debugheader_index EQUAL -1)
        message(FATAL_ERROR "Pinned DebugHeader source missing")
    endif()
    list(REMOVE_ITEM sources "${old_debugheader}")
    list(APPEND sources "${WITOS_SOURCE_ROOT}/artifacts/runtime-config/source/debugheader.witos.cpp")
    set(old_thread "${CLR_DIR}/nativeaot/Runtime/thread.cpp")
    list(FIND sources "${old_thread}" thread_index)
    if(thread_index EQUAL -1)
        message(FATAL_ERROR "Pinned Thread source missing")
    endif()
    list(REMOVE_ITEM sources "${old_thread}")
    list(APPEND sources "${WITOS_SOURCE_ROOT}/artifacts/runtime-config/source/thread.witos.cpp")
    set(old_startup "${CLR_DIR}/nativeaot/Runtime/startup.cpp")
    list(FIND sources "${old_startup}" startup_index)
    if(startup_index EQUAL -1)
        message(FATAL_ERROR "Pinned startup source missing")
    endif()
    list(REMOVE_ITEM sources "${old_startup}")
    list(APPEND sources "${WITOS_SOURCE_ROOT}/artifacts/runtime-config/source/startup.witos.cpp")
    set_property(TARGET Runtime.WorkstationGC PROPERTY SOURCES "${sources}")

    # Cookie initialization and its check/handler cannot themselves depend on
    # an initialized cookie or recursively invoke GS while validating a frame.
    set_source_files_properties("${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/native_console.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/native_processor.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/native_wait.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/native_activation.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/native_thread_handles.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/native_thread_create.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/native_services.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/native_memory.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/native_random.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/security_cookie.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/security_handler.witos.cpp"
        TARGET_DIRECTORY Runtime.WorkstationGC PROPERTIES COMPILE_OPTIONS "/Od;/GS-")
    set_source_files_properties("${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/failfast_exception.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/fatal.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/native_clock.witos.cpp"
        TARGET_DIRECTORY Runtime.WorkstationGC PROPERTIES COMPILE_OPTIONS "/Od;/GS-")
    # Preserve production GS while avoiding optimizer-generated chained unwind
    # in the exact native heap object executed by the bounded guest fixtures.
    set_source_files_properties("${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/pal_context.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/native_suspend.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/pal_context_storage.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/gc_policy.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/native_com.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/native_diagnostics.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/native_new.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/pal_thread_name.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/native_module.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/native_encoding.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/pal_events.witos.cpp"
        TARGET_DIRECTORY Runtime.WorkstationGC PROPERTIES COMPILE_OPTIONS "/Od")
    set_source_files_properties("${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/pal_context.witos.cpp" TARGET_DIRECTORY WitOS.ConfigProbe PROPERTIES COMPILE_OPTIONS "/Od")
    get_target_property(minipal_sources aotminipal SOURCES)
    list(FIND minipal_sources "mutex.c" mutex_index)
    if(mutex_index EQUAL -1)
        message(FATAL_ERROR "Pinned mutex source missing from aotminipal")
    endif()
    list(REMOVE_ITEM minipal_sources "mutex.c")
    list(APPEND minipal_sources "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/mutex.witos.cpp")
    list(FIND minipal_sources "time.c" time_index)
    if(time_index EQUAL -1)
        message(FATAL_ERROR "Pinned time source missing from aotminipal")
    endif()
    list(REMOVE_ITEM minipal_sources "time.c")
    list(APPEND minipal_sources "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/minipal_time.witos.cpp")
    # MSVC optimized time functions emit shrink-wrapped chained unwind records.
    # Keep this bootstrap object in the current plain-unwind guest profile.
    set_source_files_properties("${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/minipal_time.witos.cpp"
        TARGET_DIRECTORY aotminipal PROPERTIES COMPILE_OPTIONS "/Od")
    list(FIND minipal_sources "cpufeatures.c" cpu_index)
    if(cpu_index EQUAL -1)
        message(FATAL_ERROR "Pinned CPU features source missing from aotminipal")
    endif()
    list(REMOVE_ITEM minipal_sources "cpufeatures.c")
    list(APPEND minipal_sources "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/X64/minipal_cpu.witos.cpp")
    set_source_files_properties("${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/X64/minipal_cpu.witos.cpp"
        TARGET_DIRECTORY aotminipal PROPERTIES COMPILE_OPTIONS "/Od;/GS-")
    set_property(TARGET aotminipal PROPERTY SOURCES "${minipal_sources}")
    target_include_directories(aotminipal PRIVATE "${WITOS_SOURCE_ROOT}/src/Runtime.Native"
        "${WITOS_SOURCE_ROOT}/src/Kernel/include")
    set_source_files_properties("${WITOS_SOURCE_ROOT}/artifacts/runtime-config/source/log.openlibm.c"
        TARGET_DIRECTORY Runtime.WorkstationGC PROPERTIES COMPILE_OPTIONS "/std:c17;/fp:strict;/Od;/GS-")
    set_source_files_properties("${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/native_math.witos.cpp"
        TARGET_DIRECTORY Runtime.WorkstationGC PROPERTIES COMPILE_OPTIONS "/fp:strict;/Od;/GS-")
    target_include_directories(Runtime.WorkstationGC PRIVATE "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot"
        "${WITOS_SOURCE_ROOT}/src/Runtime.Native" "${WITOS_SOURCE_ROOT}/src/Kernel/include")
    file(WRITE "${CMAKE_BINARY_DIR}/witos-runtime-sources.txt" "${sources}\n")
    message(STATUS "WitOS: replaced workstation GC environment, Release Crst and minipal mutex; missing methods remain undefined")
    # Create the probe in an isolated directory with the actual NativeAOT
    # directory settings; the hook itself runs in CoreCLR's parent scope.
    get_target_property(WITOS_CONFIG_INCLUDES Runtime.WorkstationGC INCLUDE_DIRECTORIES)
    get_target_property(config_directory Runtime.WorkstationGC SOURCE_DIR)
    get_target_property(config_binary_directory Runtime.WorkstationGC BINARY_DIR)
    list(PREPEND WITOS_CONFIG_INCLUDES "${config_binary_directory}" "${config_directory}")
    get_directory_property(WITOS_CONFIG_DEFINITIONS DIRECTORY "${config_directory}" COMPILE_DEFINITIONS)
    list(APPEND WITOS_CONFIG_DEFINITIONS NO_STRESS_LOG)
    get_target_property(WITOS_CONFIG_OPTIONS Runtime.WorkstationGC COMPILE_OPTIONS)
    get_directory_property(WITOS_CONFIG_FLAGS DIRECTORY "${config_directory}" DEFINITION CMAKE_CXX_FLAGS)
    get_directory_property(WITOS_CONFIG_RELEASE_FLAGS DIRECTORY "${config_directory}" DEFINITION CMAKE_CXX_FLAGS_RELEASE)
    if(NOT "FEATURE_NATIVEAOT" IN_LIST WITOS_CONFIG_DEFINITIONS OR "FEATURE_CORECLR" IN_LIST WITOS_CONFIG_DEFINITIONS)
        message(FATAL_ERROR "Configuration probe requires the actual NativeAOT compile profile")
    endif()
    set(config_sources "${WITOS_SOURCE_ROOT}/artifacts/runtime-config/source/rhconfig.witos.cpp"
        "${WITOS_SOURCE_ROOT}/artifacts/runtime-config/source/gcconfig.slice.cpp"
        "${WITOS_SOURCE_ROOT}/artifacts/runtime-config/source/gcaffinity.slice.cpp"
        "${WITOS_SOURCE_ROOT}/artifacts/runtime-config/source/gcenv.config.slice.cpp"
        "${WITOS_SOURCE_ROOT}/tests/User.X64/runtime_config.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/pal_init.witos.cpp"
        "${WITOS_SOURCE_ROOT}/artifacts/runtime-config/source/allocheap.witos.cpp"
        "${WITOS_SOURCE_ROOT}/artifacts/runtime-config/source/dispatch.shared.slice.cpp"
        "${WITOS_SOURCE_ROOT}/artifacts/runtime-config/source/dispatch.aot.slice.cpp"
        "${WITOS_SOURCE_ROOT}/tests/User.X64/runtime_allocator.cpp"
        "${WITOS_SOURCE_ROOT}/artifacts/runtime-config/source/startup.objects.slice.cpp"
        "${WITOS_SOURCE_ROOT}/tests/User.X64/runtime_instance.cpp"
        "${WITOS_SOURCE_ROOT}/tests/User.X64/runtime_barrier.cpp"
        "${WITOS_SOURCE_ROOT}/tests/User.X64/runtime_time.cpp"
        "${WITOS_SOURCE_ROOT}/tests/User.X64/runtime_crt.cpp"
        "${WITOS_SOURCE_ROOT}/tests/User.X64/runtime_stack.cpp"
        "${WITOS_SOURCE_ROOT}/tests/User.X64/runtime_cpu.cpp"
        "${WITOS_SOURCE_ROOT}/tests/User.X64/runtime_clock.cpp"
        "${WITOS_SOURCE_ROOT}/tests/User.X64/runtime_fatal.cpp"
        "${WITOS_SOURCE_ROOT}/tests/User.X64/runtime_affinity.cpp"
        "${WITOS_SOURCE_ROOT}/tests/User.X64/runtime_math.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/native_format.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/format_fixed.witos.cpp"
        "${WITOS_SOURCE_ROOT}/tests/User.X64/runtime_format.cpp"
        "${WITOS_SOURCE_ROOT}/tests/User.X64/runtime_security.cpp"
        "${WITOS_SOURCE_ROOT}/tests/User.X64/runtime_random.cpp"
        "${WITOS_SOURCE_ROOT}/tests/User.X64/runtime_memory.cpp"
        "${WITOS_SOURCE_ROOT}/tests/User.X64/runtime_services.cpp"
        "${WITOS_SOURCE_ROOT}/tests/User.X64/runtime_thread_references.cpp"
        "${WITOS_SOURCE_ROOT}/tests/User.X64/runtime_object_wait.cpp"
        "${WITOS_SOURCE_ROOT}/tests/User.X64/runtime_console.cpp"
        "${WITOS_SOURCE_ROOT}/tests/User.X64/runtime_encoding.cpp"
        "${WITOS_SOURCE_ROOT}/tests/User.X64/runtime_module_names.cpp"
        "${WITOS_SOURCE_ROOT}/tests/User.X64/runtime_thread_names.cpp"
        "${WITOS_SOURCE_ROOT}/tests/User.X64/runtime_diagnostics.cpp"
        "${WITOS_SOURCE_ROOT}/tests/User.X64/runtime_com.cpp"
        "${WITOS_SOURCE_ROOT}/tests/User.X64/runtime_gc_policy.cpp"
        "${WITOS_SOURCE_ROOT}/tests/User.X64/runtime_context_set.cpp"
        "${WITOS_SOURCE_ROOT}/tests/User.X64/runtime_stack_lease.cpp"
        "${WITOS_SOURCE_ROOT}/tests/User.X64/runtime_unwind.cpp"
        "${WITOS_SOURCE_ROOT}/tests/User.X64/runtime_exception.cpp"
        "${WITOS_SOURCE_ROOT}/tests/User.X64/runtime_vectored.cpp"
        "${WITOS_SOURCE_ROOT}/tests/User.X64/runtime_raise.cpp"
        "${WITOS_SOURCE_ROOT}/tests/User.X64/runtime_failfast.cpp"
        "${WITOS_SOURCE_ROOT}/tests/User.X64/runtime_seh.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/unwind_scope.witos.cpp"
        "${WITOS_SOURCE_ROOT}/tests/User.X64/runtime_suspend.cpp"
        "${WITOS_SOURCE_ROOT}/tests/User.X64/runtime_context_capture.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/pal_context.witos.cpp"
        "${WITOS_SOURCE_ROOT}/tests/User.X64/runtime_context_storage.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/pal_context_storage.witos.cpp")
    set_source_files_properties("${WITOS_SOURCE_ROOT}/tests/User.X64/runtime_stack.cpp"
        TARGET_DIRECTORY WitOS.ConfigProbe PROPERTIES COMPILE_OPTIONS "/Gs4096")
    set_source_files_properties("${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/native_format.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/format_fixed.witos.cpp"
        TARGET_DIRECTORY WitOS.ConfigProbe PROPERTIES COMPILE_OPTIONS "/Od")
    foreach(config_source IN LISTS config_sources)
        if(NOT EXISTS "${config_source}")
            message(FATAL_ERROR "Run runtime-source to prepare the pinned configuration sources")
        endif()
    endforeach()
    get_target_property(config_runtime_library Runtime.WorkstationGC MSVC_RUNTIME_LIBRARY)
    list(FILTER WITOS_CONFIG_OPTIONS EXCLUDE REGEX "[/-]guard:")
    separate_arguments(config_flags WINDOWS_COMMAND "${WITOS_CONFIG_FLAGS} ${WITOS_CONFIG_RELEASE_FLAGS}")
    set_target_properties(WitOS.ConfigProbe PROPERTIES SOURCES "${config_sources}"
        INCLUDE_DIRECTORIES "${WITOS_CONFIG_INCLUDES};${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot;${WITOS_SOURCE_ROOT}/tests/User.X64;${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/X64"
        MSVC_RUNTIME_LIBRARY "${config_runtime_library}"
        COMPILE_DEFINITIONS "${WITOS_CONFIG_DEFINITIONS}"
        COMPILE_OPTIONS "${WITOS_CONFIG_OPTIONS};${config_flags};/GS-;/O1;/Zl;/Gy;/Gw;/EHa-s-")
    add_dependencies(WitOS.ConfigProbe aot_eventing_headers)
    add_dependencies(Runtime.WorkstationGC WitOS.ConfigProbe)
endfunction()
cmake_language(DEFER CALL witos_select_gc_environment)
