// Troubleshooting only (Debug Skip contains "crashstacks"): when any thread raises an access violation, write
// every thread's call stack to crash_stacks.txt in the lab folder, before Unity's crash handler runs.
#include "Bridge.h"
#include <dbghelp.h>
#include <tlhelp32.h>
#include <cstdio>
#include <string>
#pragma comment(lib, "dbghelp.lib")

namespace {

std::string g_path;
volatile LONG g_done = 0;

void Walk(FILE* f, HANDLE proc, HANDLE thread, CONTEXT c) {
    STACKFRAME64 s{};
    s.AddrPC.Offset = c.Eip; s.AddrPC.Mode = AddrModeFlat;
    s.AddrFrame.Offset = c.Ebp; s.AddrFrame.Mode = AddrModeFlat;
    s.AddrStack.Offset = c.Esp; s.AddrStack.Mode = AddrModeFlat;
    for (int i = 0; i < 40; i++) {
        if (!StackWalk64(IMAGE_FILE_MACHINE_I386, proc, thread, &s, &c, nullptr, SymFunctionTableAccess64, SymGetModuleBase64, nullptr)) break;
        DWORD64 a = s.AddrPC.Offset;
        if (!a) break;
        char buf[sizeof(SYMBOL_INFO) + 512];
        auto* si = (SYMBOL_INFO*)buf;
        si->SizeOfStruct = sizeof(SYMBOL_INFO);
        si->MaxNameLen = 512;
        DWORD64 disp = 0;
        IMAGEHLP_MODULE64 m{sizeof m};
        const char* mod = SymGetModuleInfo64(proc, a, &m) ? m.ModuleName : "?";
        if (SymFromAddr(proc, a, &disp, si)) fprintf(f, "  %s!%s+0x%llx\n", mod, si->Name, disp);
        else fprintf(f, "  %s+0x%llx\n", mod, m.BaseOfImage ? a - m.BaseOfImage : a);
    }
}

LONG CALLBACK OnException(EXCEPTION_POINTERS* e) {
    if (e->ExceptionRecord->ExceptionCode != EXCEPTION_ACCESS_VIOLATION) return EXCEPTION_CONTINUE_SEARCH;
    if (InterlockedExchange(&g_done, 1)) return EXCEPTION_CONTINUE_SEARCH;
    FILE* f = nullptr;
    fopen_s(&f, g_path.c_str(), "w");
    if (!f) return EXCEPTION_CONTINUE_SEARCH;
    HANDLE proc = GetCurrentProcess();
    char dir[MAX_PATH] = "";
    HMODULE self = nullptr;
    GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCSTR)&OnException, &self);
    GetModuleFileNameA(self, dir, MAX_PATH);
    if (char* slash = strrchr(dir, '\\')) *slash = 0;
    SymSetOptions(SYMOPT_UNDNAME | SYMOPT_DEFERRED_LOADS | SYMOPT_FAIL_CRITICAL_ERRORS);
    SymInitialize(proc, dir, TRUE);
    DWORD me = GetCurrentThreadId();
    fprintf(f, "access violation in thread %lu at %p reading/writing %p\n", me, e->ExceptionRecord->ExceptionAddress,
            (void*)e->ExceptionRecord->ExceptionInformation[1]);
    fprintf(f, "=== faulting thread %lu\n", me);
    Walk(f, proc, GetCurrentThread(), *e->ContextRecord);
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    THREADENTRY32 te{sizeof te};
    for (BOOL ok = Thread32First(snap, &te); ok; ok = Thread32Next(snap, &te)) {
        if (te.th32OwnerProcessID != GetCurrentProcessId() || te.th32ThreadID == me) continue;
        HANDLE t = OpenThread(THREAD_ALL_ACCESS, FALSE, te.th32ThreadID);
        if (!t) continue;
        SuspendThread(t);
        CONTEXT c{};
        c.ContextFlags = CONTEXT_FULL;
        if (GetThreadContext(t, &c)) {
            fprintf(f, "=== thread %lu\n", te.th32ThreadID);
            Walk(f, proc, t, c);
        }
        ResumeThread(t);
        CloseHandle(t);
    }
    CloseHandle(snap);
    fclose(f);
    return EXCEPTION_CONTINUE_SEARCH;
}

}  // namespace

namespace bridge {
void InstallCrashStacks(const char* path) {
    static bool installed = false;
    if (installed) return;
    installed = true;
    g_path = path;
    AddVectoredExceptionHandler(1, OnException);
    Log("crash stacks go to %s", path);
}
}  // namespace bridge
