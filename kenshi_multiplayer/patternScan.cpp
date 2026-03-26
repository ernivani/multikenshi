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
