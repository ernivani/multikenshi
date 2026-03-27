#pragma once
#include <cstdint>

namespace patternScan {
    struct SectionInfo {
        uintptr_t base;
        size_t size;
    };

    SectionInfo getTextSection();
    SectionInfo getDataSection();
    SectionInfo getRdataSection();
    uintptr_t scan(uintptr_t start, size_t size, const uint8_t* pattern, const char* mask, size_t patternLen);

    // Find a function by scanning for a unique string in .rdata, then tracing LEA xrefs back to .text
    // Returns the function entry point (0 if not found)
    uintptr_t findFunctionByString(const char* needle, size_t needleLen);
}
