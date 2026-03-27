#include "pch.h"
#include "patternScan.h"

namespace patternScan {

    SectionInfo getTextSection() {
        SectionInfo info = { 0, 0 };
        HMODULE hModule = GetModuleHandle(NULL);
        if (!hModule) return info;

        uintptr_t base = (uintptr_t)hModule;
        IMAGE_DOS_HEADER* dosHeader = (IMAGE_DOS_HEADER*)base;
        if (dosHeader->e_magic != IMAGE_DOS_SIGNATURE) return info;

        IMAGE_NT_HEADERS* ntHeaders = (IMAGE_NT_HEADERS*)(base + dosHeader->e_lfanew);
        if (ntHeaders->Signature != IMAGE_NT_SIGNATURE) return info;

        IMAGE_SECTION_HEADER* sections = IMAGE_FIRST_SECTION(ntHeaders);
        for (WORD i = 0; i < ntHeaders->FileHeader.NumberOfSections; ++i) {
            if (memcmp(sections[i].Name, ".text", 5) == 0) {
                info.base = base + sections[i].VirtualAddress;
                info.size = sections[i].Misc.VirtualSize;
                return info;
            }
        }
        return info;
    }

    SectionInfo getDataSection() {
        SectionInfo info = { 0, 0 };
        HMODULE hModule = GetModuleHandle(NULL);
        if (!hModule) return info;

        uintptr_t base = (uintptr_t)hModule;
        IMAGE_DOS_HEADER* dosHeader = (IMAGE_DOS_HEADER*)base;
        if (dosHeader->e_magic != IMAGE_DOS_SIGNATURE) return info;

        IMAGE_NT_HEADERS* ntHeaders = (IMAGE_NT_HEADERS*)(base + dosHeader->e_lfanew);
        if (ntHeaders->Signature != IMAGE_NT_SIGNATURE) return info;

        IMAGE_SECTION_HEADER* sections = IMAGE_FIRST_SECTION(ntHeaders);
        for (WORD i = 0; i < ntHeaders->FileHeader.NumberOfSections; ++i) {
            if (memcmp(sections[i].Name, ".data", 5) == 0) {
                info.base = base + sections[i].VirtualAddress;
                info.size = sections[i].Misc.VirtualSize;
                return info;
            }
        }
        return info;
    }

    SectionInfo getRdataSection() {
        SectionInfo info = { 0, 0 };
        HMODULE hModule = GetModuleHandle(NULL);
        if (!hModule) return info;

        uintptr_t base = (uintptr_t)hModule;
        IMAGE_DOS_HEADER* dosHeader = (IMAGE_DOS_HEADER*)base;
        if (dosHeader->e_magic != IMAGE_DOS_SIGNATURE) return info;

        IMAGE_NT_HEADERS* ntHeaders = (IMAGE_NT_HEADERS*)(base + dosHeader->e_lfanew);
        if (ntHeaders->Signature != IMAGE_NT_SIGNATURE) return info;

        IMAGE_SECTION_HEADER* sections = IMAGE_FIRST_SECTION(ntHeaders);
        for (WORD i = 0; i < ntHeaders->FileHeader.NumberOfSections; ++i) {
            if (memcmp(sections[i].Name, ".rdata", 6) == 0) {
                info.base = base + sections[i].VirtualAddress;
                info.size = sections[i].Misc.VirtualSize;
                return info;
            }
        }
        return info;
    }

    // Find a raw byte sequence in memory (no mask, exact match)
    static uintptr_t findBytes(uintptr_t start, size_t size, const char* needle, size_t needleLen) {
        if (size < needleLen) return 0;
        const uint8_t* mem = (const uint8_t*)start;
        size_t limit = size - needleLen;
        for (size_t i = 0; i <= limit; ++i) {
            if (memcmp(mem + i, needle, needleLen) == 0)
                return start + i;
        }
        return 0;
    }

    uintptr_t findFunctionByString(const char* needle, size_t needleLen) {
        // Step 1: Find the string in .rdata
        SectionInfo rdata = getRdataSection();
        if (rdata.size == 0) return 0;

        uintptr_t strAddr = findBytes(rdata.base, rdata.size, needle, needleLen);
        if (strAddr == 0) return 0;

        // Step 2: Find a LEA instruction in .text that references this string
        SectionInfo text = getTextSection();
        if (text.size == 0) return 0;

        uintptr_t textEnd = text.base + text.size - 7;
        for (uintptr_t pos = text.base; pos < textEnd; pos++) {
            uint8_t b0 = *(uint8_t*)pos;
            uint8_t b1 = *(uint8_t*)(pos + 1);
            uint8_t b2 = *(uint8_t*)(pos + 2);

            // Check for LEA reg, [rip+disp32]
            // Encodings: 48 8D 05, 48 8D 0D, 48 8D 15, 4C 8D 05, 4C 8D 0D, etc.
            bool isLea = (b0 == 0x48 || b0 == 0x4C) && b1 == 0x8D && (b2 & 0xC7) == 0x05;
            if (!isLea) continue;

            int32_t disp = *(int32_t*)(pos + 3);
            uintptr_t target = (pos + 7) + disp;
            if (target != strAddr) continue;

            // Found xref! Walk backwards to find function entry
            for (uintptr_t back = pos - 1; back > pos - 4096; back--) {
                // Look for INT3 padding (CC CC) or mov rax, rsp (48 8B C4)
                if (*(uint8_t*)back == 0xCC && *(uint8_t*)(back - 1) == 0xCC)
                    return back + 1;
                // Also check for common function prologue: 48 8B C4 (mov rax, rsp)
                if (*(uint8_t*)back == 0x48 && *(uint8_t*)(back + 1) == 0x8B && *(uint8_t*)(back + 2) == 0xC4) {
                    // Verify this is actually a function start (preceded by CC or 00)
                    uint8_t prev = *(uint8_t*)(back - 1);
                    if (prev == 0xCC || prev == 0x00 || prev == 0xC3)
                        return back;
                }
            }
        }
        return 0;
    }

    uintptr_t scan(uintptr_t start, size_t size, const uint8_t* pattern, const char* mask, size_t patternLen) {
        if (size < patternLen) return 0;
        size_t limit = size - patternLen;
        const uint8_t* mem = (const uint8_t*)start;

        for (size_t i = 0; i <= limit; ++i) {
            bool found = true;
            for (size_t j = 0; j < patternLen; ++j) {
                if (mask[j] == 'x' && mem[i + j] != pattern[j]) {
                    found = false;
                    break;
                }
            }
            if (found) return start + i;
        }
        return 0;
    }

}
