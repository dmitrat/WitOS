# Loaded only by CMAKE_PROJECT_CoreCLR_INCLUDE; upstream files stay unchanged.
# Defer until the upstream project has created the actual runtime target.
get_filename_component(WITOS_SOURCE_ROOT "${CMAKE_CURRENT_LIST_DIR}/../.." ABSOLUTE)
add_subdirectory("${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/config-probe" "${CMAKE_BINARY_DIR}/witos-config-build")
function(witos_select_gc_environment)
    if(NOT TARGET Runtime.WorkstationGC OR NOT CLR_CMAKE_TARGET_WIN32 OR NOT CLR_CMAKE_TARGET_ARCH_AMD64)
        message(FATAL_ERROR "WitOS source overlay requires the pinned Windows AMD64 workstation runtime")
    endif()
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
    list(APPEND sources "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/gcenv.witos.cpp"
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
        "${WITOS_SOURCE_ROOT}/src/System.Native/thread.c"
        "${WITOS_SOURCE_ROOT}/src/System.Native/image.c"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/pal_module.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/pal_environment.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/pal_error.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Kernel.Arch.X64/native_error.asm"
        "${WITOS_SOURCE_ROOT}/src/Kernel.Arch.X64/native_environment.asm")
    set_source_files_properties("${WITOS_SOURCE_ROOT}/src/System.Native/thread.c"
        "${WITOS_SOURCE_ROOT}/src/System.Native/image.c" TARGET_DIRECTORY Runtime.WorkstationGC PROPERTIES COMPILE_OPTIONS "/std:c17")
    file(STRINGS "${WITOS_SOURCE_ROOT}/src/Kernel/include/witos/user_abi.h" error_line
        REGEX "^#define WIT_TLS_LAST_ERROR_OFFSET [0-9]+U$")
    list(LENGTH error_line error_lines)
    if(NOT error_lines EQUAL 1)
        message(FATAL_ERROR "Missing or ambiguous native last-error ABI offset")
    endif()
    string(REGEX REPLACE "^#define WIT_TLS_LAST_ERROR_OFFSET ([0-9]+)U$" "\\1" error_offset "${error_line}")
    file(MAKE_DIRECTORY "${CMAKE_BINARY_DIR}/witos-abi")
    file(WRITE "${CMAKE_BINARY_DIR}/witos-abi/user_abi.inc" "WIT_TLS_LAST_ERROR_OFFSET EQU ${error_offset}\n")
    set_source_files_properties("${WITOS_SOURCE_ROOT}/src/Kernel.Arch.X64/native_error.asm"
        "${WITOS_SOURCE_ROOT}/src/Kernel.Arch.X64/native_environment.asm"
        TARGET_DIRECTORY Runtime.WorkstationGC PROPERTIES LANGUAGE ASM_MASM
        COMPILE_OPTIONS "/I${CMAKE_BINARY_DIR}/witos-abi")
    set(old_config "${CLR_DIR}/nativeaot/Runtime/RhConfig.cpp")
    list(FIND sources "${old_config}" config_index)
    if(config_index EQUAL -1)
        message(FATAL_ERROR "Pinned RhConfig source missing")
    endif()
    list(REMOVE_ITEM sources "${old_config}")
    list(APPEND sources "${WITOS_SOURCE_ROOT}/artifacts/runtime-config/source/rhconfig.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/crt_config.witos.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/crt_exit.witos.cpp")
    set(old_startup "${CLR_DIR}/nativeaot/Runtime/startup.cpp")
    list(FIND sources "${old_startup}" startup_index)
    if(startup_index EQUAL -1)
        message(FATAL_ERROR "Pinned startup source missing")
    endif()
    list(REMOVE_ITEM sources "${old_startup}")
    list(APPEND sources "${WITOS_SOURCE_ROOT}/artifacts/runtime-config/source/startup.witos.cpp")
    set_property(TARGET Runtime.WorkstationGC PROPERTY SOURCES "${sources}")

    get_target_property(minipal_sources aotminipal SOURCES)
    list(FIND minipal_sources "mutex.c" mutex_index)
    if(mutex_index EQUAL -1)
        message(FATAL_ERROR "Pinned mutex source missing from aotminipal")
    endif()
    list(REMOVE_ITEM minipal_sources "mutex.c")
    list(APPEND minipal_sources "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/mutex.witos.cpp")
    set_property(TARGET aotminipal PROPERTY SOURCES "${minipal_sources}")
    target_include_directories(aotminipal PRIVATE "${WITOS_SOURCE_ROOT}/src/System.Native"
        "${WITOS_SOURCE_ROOT}/src/Kernel/include")
    target_include_directories(Runtime.WorkstationGC PRIVATE
        "${WITOS_SOURCE_ROOT}/src/System.Native" "${WITOS_SOURCE_ROOT}/src/Kernel/include")
    file(WRITE "${CMAKE_BINARY_DIR}/witos-runtime-sources.txt" "${sources}\n")
    message(STATUS "WitOS: replaced workstation GC environment, Release Crst and minipal mutex; missing methods remain undefined")
    # Create the probe in an isolated directory with the actual NativeAOT
    # directory settings; the hook itself runs in CoreCLR's parent scope.
    get_target_property(WITOS_CONFIG_INCLUDES Runtime.WorkstationGC INCLUDE_DIRECTORIES)
    get_target_property(config_directory Runtime.WorkstationGC SOURCE_DIR)
    get_target_property(config_binary_directory Runtime.WorkstationGC BINARY_DIR)
    list(PREPEND WITOS_CONFIG_INCLUDES "${config_binary_directory}" "${config_directory}")
    get_directory_property(WITOS_CONFIG_DEFINITIONS DIRECTORY "${config_directory}" COMPILE_DEFINITIONS)
    get_target_property(WITOS_CONFIG_OPTIONS Runtime.WorkstationGC COMPILE_OPTIONS)
    get_directory_property(WITOS_CONFIG_FLAGS DIRECTORY "${config_directory}" DEFINITION CMAKE_CXX_FLAGS)
    get_directory_property(WITOS_CONFIG_RELEASE_FLAGS DIRECTORY "${config_directory}" DEFINITION CMAKE_CXX_FLAGS_RELEASE)
    if(NOT "FEATURE_NATIVEAOT" IN_LIST WITOS_CONFIG_DEFINITIONS OR "FEATURE_CORECLR" IN_LIST WITOS_CONFIG_DEFINITIONS)
        message(FATAL_ERROR "Configuration probe requires the actual NativeAOT compile profile")
    endif()
    set(config_sources "${WITOS_SOURCE_ROOT}/artifacts/runtime-config/source/rhconfig.witos.cpp"
        "${WITOS_SOURCE_ROOT}/artifacts/runtime-config/source/gcconfig.slice.cpp"
        "${WITOS_SOURCE_ROOT}/artifacts/runtime-config/source/gcenv.config.slice.cpp"
        "${WITOS_SOURCE_ROOT}/tests/User.X64/runtime_config.cpp"
        "${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot/pal_init.witos.cpp")
    foreach(config_source IN LISTS config_sources)
        if(NOT EXISTS "${config_source}")
            message(FATAL_ERROR "Run runtime-source to prepare the pinned configuration sources")
        endif()
    endforeach()
    get_target_property(config_runtime_library Runtime.WorkstationGC MSVC_RUNTIME_LIBRARY)
    list(FILTER WITOS_CONFIG_OPTIONS EXCLUDE REGEX "[/-]guard:")
    separate_arguments(config_flags WINDOWS_COMMAND "${WITOS_CONFIG_FLAGS} ${WITOS_CONFIG_RELEASE_FLAGS}")
    set_target_properties(WitOS.ConfigProbe PROPERTIES SOURCES "${config_sources}"
        INCLUDE_DIRECTORIES "${WITOS_CONFIG_INCLUDES};${WITOS_SOURCE_ROOT}/src/Runtime.NativeAot;${WITOS_SOURCE_ROOT}/tests/User.X64"
        MSVC_RUNTIME_LIBRARY "${config_runtime_library}"
        COMPILE_DEFINITIONS "${WITOS_CONFIG_DEFINITIONS}"
        COMPILE_OPTIONS "${WITOS_CONFIG_OPTIONS};${config_flags};/GS-;/O1;/Zl;/Gy;/Gw;/EHa-s-")
    add_dependencies(WitOS.ConfigProbe aot_eventing_headers)
    add_dependencies(Runtime.WorkstationGC WitOS.ConfigProbe)
endfunction()
cmake_language(DEFER CALL witos_select_gc_environment)
