#include "pch.h"
#include "crashlog.h"
#include <windows.h>
#include <cstdio>
#include <cstring>
#include <iostream>
#include <exception>

namespace crashlog {
    static char logPath[MAX_PATH] = {};
    static char tempLogPath[MAX_PATH] = {};
    static char lastPhase[256] = "startup";

    static void writeToAll(const char* buf) {
        std::cerr << buf;
        std::cerr.flush();

        FILE* f = nullptr;
        fopen_s(&f, logPath, "a");
        if (f) { fputs(buf, f); fclose(f); }
        fopen_s(&f, tempLogPath, "a");
        if (f) { fputs(buf, f); fclose(f); }
    }

    // VEH: only catches real hardware exceptions (NOT C++ throws)
    static LONG WINAPI vehHandler(EXCEPTION_POINTERS* ex) {
        DWORD code = ex->ExceptionRecord->ExceptionCode;

        // Only access violations, stack overflow, etc. — NOT 0xE06D7363
        if (code != EXCEPTION_ACCESS_VIOLATION
            && code != EXCEPTION_STACK_OVERFLOW
            && code != EXCEPTION_ILLEGAL_INSTRUCTION
            && code != EXCEPTION_INT_DIVIDE_BY_ZERO)
            return EXCEPTION_CONTINUE_SEARCH;

        uintptr_t moduleBase = (uintptr_t)GetModuleHandle(NULL);
        char buf[2048];
        int len = 0;
        len += sprintf_s(buf + len, sizeof(buf) - len, "\n=== CRASH (VEH) ===\n");
        len += sprintf_s(buf + len, sizeof(buf) - len, "Phase: %s\n", lastPhase);
        len += sprintf_s(buf + len, sizeof(buf) - len, "Exception: 0x%08X\n", code);
        len += sprintf_s(buf + len, sizeof(buf) - len, "RIP: 0x%llX (offset: 0x%llX)\n",
                (unsigned long long)ex->ContextRecord->Rip,
                (unsigned long long)(ex->ContextRecord->Rip - moduleBase));
        len += sprintf_s(buf + len, sizeof(buf) - len, "RAX: 0x%llX  RBX: 0x%llX\n",
                (unsigned long long)ex->ContextRecord->Rax,
                (unsigned long long)ex->ContextRecord->Rbx);
        len += sprintf_s(buf + len, sizeof(buf) - len, "RCX: 0x%llX  RDX: 0x%llX\n",
                (unsigned long long)ex->ContextRecord->Rcx,
                (unsigned long long)ex->ContextRecord->Rdx);
        if (code == EXCEPTION_ACCESS_VIOLATION
            && ex->ExceptionRecord->NumberParameters >= 2) {
            len += sprintf_s(buf + len, sizeof(buf) - len, "AV %s address: 0x%llX\n",
                    ex->ExceptionRecord->ExceptionInformation[0] ? "writing" : "reading",
                    (unsigned long long)ex->ExceptionRecord->ExceptionInformation[1]);
        }
        writeToAll(buf);
        return EXCEPTION_CONTINUE_SEARCH;
    }

    // UEF: catches unhandled exceptions (including C++ throws that aren't caught)
    static LONG WINAPI uefHandler(EXCEPTION_POINTERS* ex) {
        uintptr_t moduleBase = (uintptr_t)GetModuleHandle(NULL);
        DWORD code = ex->ExceptionRecord->ExceptionCode;
        char buf[2048];
        int len = 0;
        len += sprintf_s(buf + len, sizeof(buf) - len, "\n=== UNHANDLED EXCEPTION ===\n");
        len += sprintf_s(buf + len, sizeof(buf) - len, "Phase: %s\n", lastPhase);
        len += sprintf_s(buf + len, sizeof(buf) - len, "Exception: 0x%08X%s\n", code,
                code == 0xE06D7363 ? " (C++ exception)" : "");
        len += sprintf_s(buf + len, sizeof(buf) - len, "RIP: 0x%llX (offset: 0x%llX)\n",
                (unsigned long long)ex->ContextRecord->Rip,
                (unsigned long long)(ex->ContextRecord->Rip - moduleBase));
        len += sprintf_s(buf + len, sizeof(buf) - len, "RAX: 0x%llX  RBX: 0x%llX\n",
                (unsigned long long)ex->ContextRecord->Rax,
                (unsigned long long)ex->ContextRecord->Rbx);
        len += sprintf_s(buf + len, sizeof(buf) - len, "RCX: 0x%llX  RDX: 0x%llX\n",
                (unsigned long long)ex->ContextRecord->Rcx,
                (unsigned long long)ex->ContextRecord->Rdx);
        writeToAll(buf);
        return EXCEPTION_CONTINUE_SEARCH;
    }

    // Catches std::terminate (unhandled C++ exceptions, pure virtual calls, etc.)
    static void terminateHandler() {
        char buf[512];
        sprintf_s(buf, "\n=== std::terminate CALLED ===\nPhase: %s\n", lastPhase);
        writeToAll(buf);

        // Try to get current exception info
        std::exception_ptr ep = std::current_exception();
        if (ep) {
            try { std::rethrow_exception(ep); }
            catch (const std::exception& e) {
                char buf2[512];
                sprintf_s(buf2, "what(): %s\n", e.what());
                writeToAll(buf2);
            }
            catch (...) {
                writeToAll("Unknown exception type\n");
            }
        }
        abort();
    }

    // Detects graceful exit vs crash
    static void atexitHandler() {
        char buf[256];
        sprintf_s(buf, "\n--- Process exiting (atexit) at phase: %s ---\n", lastPhase);
        writeToAll(buf);
    }

    void init() {
        GetModuleFileNameA(NULL, logPath, MAX_PATH);
        char* lastSlash = strrchr(logPath, '\\');
        if (lastSlash)
            strcpy_s(lastSlash + 1,
                     (size_t)(MAX_PATH - (lastSlash + 1 - logPath)),
                     "kenshi_mp_crash.log");

        GetTempPathA(MAX_PATH, tempLogPath);
        strcat_s(tempLogPath, "kenshi_mp_crash.log");

        FILE* f = nullptr;
        fopen_s(&f, logPath, "w");
        if (f) { fprintf(f, "=== Kenshi MP Crash Log ===\n"); fclose(f); }
        fopen_s(&f, tempLogPath, "w");
        if (f) { fprintf(f, "=== Kenshi MP Crash Log ===\n"); fclose(f); }

        AddVectoredExceptionHandler(1, vehHandler);
        SetUnhandledExceptionFilter(uefHandler);
        std::set_terminate(terminateHandler);
        atexit(atexitHandler);
    }

    void phase(const char* msg) {
        strcpy_s(lastPhase, msg);
        FILE* f = nullptr;
        fopen_s(&f, logPath, "a");
        if (f) { fprintf(f, "PHASE: %s\n", msg); fclose(f); }
    }

    const char* getLogPath() {
        return logPath;
    }
}
