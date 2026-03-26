#include "pch.h"
#include "crashlog.h"
#include <windows.h>
#include <cstdio>
#include <cstring>

namespace crashlog {
    static char logPath[MAX_PATH] = {};
    static char lastPhase[256] = "startup";

    static LONG WINAPI crashHandler(EXCEPTION_POINTERS* ex) {
        FILE* f = nullptr;
        fopen_s(&f, logPath, "a");
        if (f) {
            fprintf(f, "\n=== CRASH ===\n");
            fprintf(f, "Phase: %s\n", lastPhase);
            fprintf(f, "Exception: 0x%08X\n", ex->ExceptionRecord->ExceptionCode);
            fprintf(f, "Address: 0x%p\n", ex->ExceptionRecord->ExceptionAddress);
            fprintf(f, "RIP: 0x%llX\n", (unsigned long long)ex->ContextRecord->Rip);
            fprintf(f, "RSP: 0x%llX\n", (unsigned long long)ex->ContextRecord->Rsp);
            fprintf(f, "RAX: 0x%llX\n", (unsigned long long)ex->ContextRecord->Rax);
            fprintf(f, "RBX: 0x%llX\n", (unsigned long long)ex->ContextRecord->Rbx);
            fprintf(f, "RCX: 0x%llX\n", (unsigned long long)ex->ContextRecord->Rcx);
            fprintf(f, "RDX: 0x%llX\n", (unsigned long long)ex->ContextRecord->Rdx);
            fclose(f);
        }
        return EXCEPTION_CONTINUE_SEARCH;
    }

    void init() {
        GetModuleFileNameA(NULL, logPath, MAX_PATH);
        char* lastSlash = strrchr(logPath, '\\');
        if (lastSlash)
            strcpy_s(lastSlash + 1,
                     (size_t)(MAX_PATH - (lastSlash + 1 - logPath)),
                     "kenshi_mp_crash.log");

        // Clear old log
        FILE* f = nullptr;
        fopen_s(&f, logPath, "w");
        if (f) {
            fprintf(f, "=== Kenshi MP Crash Log ===\n");
            fclose(f);
        }

        SetUnhandledExceptionFilter(crashHandler);
    }

    void phase(const char* msg) {
        strcpy_s(lastPhase, msg);
        FILE* f = nullptr;
        fopen_s(&f, logPath, "a");
        if (f) {
            fprintf(f, "PHASE: %s\n", msg);
            fclose(f);
        }
    }

    const char* getLogPath() {
        return logPath;
    }
}
