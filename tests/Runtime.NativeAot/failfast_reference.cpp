#include <windows.h>
#include <stdio.h>
#include <stdlib.h>

static LONG CALLBACK forbidden(EXCEPTION_POINTERS *)
{
    ExitProcess(99);
}

static int child(int mode)
{
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);
    AddVectoredExceptionHandler(1, forbidden);
    EXCEPTION_RECORD record = {};
    record.ExceptionCode = 0xE0426789;
    record.ExceptionAddress = (void *)0x12345678;
    record.NumberParameters = 1;
    record.ExceptionInformation[0] = 0xABCDEF;
    CONTEXT context = {};
    RtlCaptureContext(&context);
    context.Rip = 0x23456789;
    context.R12 = 0x1122334455667788ULL;
    if (mode == 3) {
        record.ExceptionAddress = nullptr;
    }
    __try {
        RaiseFailFastException(mode ? &record : nullptr, mode == 4 ? &context : nullptr,
            (mode == 2 || mode == 3) ? FAIL_FAST_GENERATE_EXCEPTION_ADDRESS : 0);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        ExitProcess(98);
    }
    return 97;
}

int main(int argc, char **argv)
{
    if (argc == 2) {
        return child(atoi(argv[1]));
    }
    wchar_t executable[MAX_PATH];
    if (!GetModuleFileNameW(nullptr, executable, MAX_PATH)) {
        return 1;
    }
    for (int mode = 0; mode < 5; ++mode) {
        wchar_t command[MAX_PATH + 32];
        if (swprintf_s(command, L"\"%s\" %d", executable, mode) < 0) {
            return 2;
        }
        STARTUPINFOW startup = {};
        startup.cb = sizeof(startup);
        PROCESS_INFORMATION process = {};
        if (!CreateProcessW(nullptr, command, nullptr, nullptr, FALSE, DEBUG_ONLY_THIS_PROCESS | CREATE_NO_WINDOW,
                nullptr, nullptr, &startup, &process)) {
            return 3;
        }
        bool observed = false, exited = false;
        DWORD code = 0;
        ULONG64 address = 0, rip = 0, r12 = 0;
        const ULONGLONG end = GetTickCount64() + 10000;
        while (!exited && GetTickCount64() < end) {
            DEBUG_EVENT event = {};
            if (!WaitForDebugEvent(&event, 100)) {
                continue;
            }
            DWORD disposition = DBG_CONTINUE;
            if (event.dwDebugEventCode == CREATE_PROCESS_DEBUG_EVENT && event.u.CreateProcessInfo.hFile) {
                CloseHandle(event.u.CreateProcessInfo.hFile);
            }
            if (event.dwDebugEventCode == LOAD_DLL_DEBUG_EVENT && event.u.LoadDll.hFile) {
                CloseHandle(event.u.LoadDll.hFile);
            }
            if (event.dwDebugEventCode == EXCEPTION_DEBUG_EVENT) {
                auto &e = event.u.Exception;
                if (!e.dwFirstChance) {
                    code = e.ExceptionRecord.ExceptionCode;
                    address = (ULONG64)e.ExceptionRecord.ExceptionAddress;
                    HANDLE thread = OpenThread(THREAD_GET_CONTEXT, FALSE, event.dwThreadId);
                    CONTEXT c = {};
                    c.ContextFlags = CONTEXT_FULL;
                    if (!thread || !GetThreadContext(thread, &c)) {
                        return 4;
                    }
                    rip = c.Rip;
                    r12 = c.R12;
                    CloseHandle(thread);
                    observed = true;
                }
                if (e.ExceptionRecord.ExceptionCode != EXCEPTION_BREAKPOINT || !e.dwFirstChance) {
                    disposition = DBG_EXCEPTION_NOT_HANDLED;
                }
            }
            if (event.dwDebugEventCode == EXIT_PROCESS_DEBUG_EVENT) {
                exited = true;
                if (event.u.ExitProcess.dwExitCode == 98 || event.u.ExitProcess.dwExitCode == 99) {
                    return 5;
                }
            }
            if (!ContinueDebugEvent(event.dwProcessId, event.dwThreadId, disposition)) {
                return 6;
            }
        }
        if (!exited) {
            TerminateProcess(process.hProcess, 96);
        }
        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
        if (!observed || !exited || code != (mode ? 0xE0426789U : 0xC0000602U)) {
            return 7;
        }
        printf("failfast mode=%d code=%08lx address=%llx rip=%llx r12=%llx\n", mode, code, address, rip, r12);
        if ((mode == 0 || mode == 2 || mode == 3) && (!address || address == 0x12345678)) {
            return 10;
        }
        if ((mode == 1 || mode == 4) && address != 0x12345678) {
            return 8;
        }
        if (mode == 4 && (rip != 0x23456789 || r12 != 0x1122334455667788ULL)) {
            return 9;
        }
    }
    puts("PASS: Windows fail-fast second-chance record/context and handler bypass (HOSTED only)");
    return 0;
}
