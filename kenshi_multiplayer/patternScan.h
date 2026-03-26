#pragma once
#include <cstdint>

namespace patternScan {
    struct SectionInfo {
        uintptr_t base;
        size_t size;
    };

    SectionInfo getTextSection();
    uintptr_t scan(uintptr_t start, size_t size, const uint8_t* pattern, const char* mask, size_t patternLen);
}
