#include "pch.h"
#include "offsets.h"
#include "patternScan.h"
#include <iostream>
#include <iomanip>

namespace offsets {
    uintptr_t factionString = 0;
    uintptr_t gameWorldOffset = 0;
    uintptr_t setPaused = 0;
    uintptr_t charUpdateHook = 0;
    uintptr_t buildingUpdateHook = 0;

    uintptr_t itemSpawningHand = 0;
    uintptr_t itemSpawningMagic = 0;
    uintptr_t spawnItemFunc = 0;
    uintptr_t getSectionFromInvByName = 0;

    uintptr_t GameDataManagerMain = 0;
    uintptr_t GameDataManagerFoliage = 0;
    uintptr_t GameDataManagerSquads = 0;

    uintptr_t spawnSquadBypass = 0;
    uintptr_t spawnSquadFuncCall = 0;
    uintptr_t squadSpawningHand = 0;

    bool resolveAllOffsets() {
        uintptr_t moduleBase = (uintptr_t)GetModuleHandle(NULL);
        patternScan::SectionInfo text = patternScan::getTextSection();
        if (text.size == 0) {
            std::cerr << "ERROR: Could not find .text section!\n";
            return false;
        }
        std::cout << ".text section: 0x" << std::hex << text.base
                  << " size: 0x" << text.size << std::dec << "\n";

        // charUpdateHook (14 bytes):
        //   mov rcx,[rbx+00000320]  mov [rbx+0000037C],sil
        {
            uint8_t pat[] = {
                0x48,0x8B,0x8B, 0x20,0x03,0x00,0x00,
                0x40,0x88,0xB3, 0x7C,0x03,0x00,0x00
            };
            uintptr_t addr = patternScan::scan(text.base, text.size, pat, "xxxxxxxxxxxxxx", 14);
            if (addr) charUpdateHook = addr - moduleBase;
        }

        // buildingUpdateHook (21 bytes):
        //   mov rax,[rbx+60]; mov r12,[rax+rbp]; mov rcx,r12;
        //   mov rax,[r12]; call qword ptr [rax+D8]
        {
            uint8_t pat[] = {
                0x48,0x8B,0x43,0x60,
                0x4C,0x8B,0x24,0x28,
                0x49,0x8B,0xCC,
                0x49,0x8B,0x04,0x24,
                0xFF,0x90, 0xD8,0x00,0x00,0x00
            };
            uintptr_t addr = patternScan::scan(text.base, text.size, pat, "xxxxxxxxxxxxxxxxxxxxx", 21);
            if (addr) buildingUpdateHook = addr - moduleBase;
        }

        // spawnSquadBypass (15 bytes) - function prologue:
        //   lea rbp,[rsp-000000D0]; sub rsp,000001D0
        {
            uint8_t pat[] = {
                0x48,0x8D,0xAC,0x24, 0x30,0xFF,0xFF,0xFF,
                0x48,0x81,0xEC, 0xD0,0x01,0x00,0x00
            };
            uintptr_t addr = patternScan::scan(text.base, text.size, pat, "xxxxxxxxxxxxxxx", 15);
            if (addr) {
                spawnSquadBypass = addr - moduleBase;

                // Try to find squadSpawningHand via MOV reg,[rip+disp32] (works for GOG)
                uintptr_t textEnd = text.base + text.size;
                for (uintptr_t pos = addr + 15; pos < addr + 4000 - 7; pos++) {
                    uint8_t b0 = *(uint8_t*)pos;
                    uint8_t b1 = *(uint8_t*)(pos+1);
                    uint8_t b2 = *(uint8_t*)(pos+2);

                    if ((b0 != 0x48 && b0 != 0x4C) || b1 != 0x8B)
                        continue;
                    if ((b2 & 0xC7) != 0x05)
                        continue;

                    int32_t rel32 = *(int32_t*)(pos + 3);
                    uintptr_t target = (pos + 7) + rel32;
                    if (target <= textEnd || target < moduleBase)
                        continue;

                    // Skip __security_cookie (followed by xor reg, rsp)
                    bool cookie = false;
                    for (int k = 0; k < 10; k++) {
                        uint8_t x0 = *(uint8_t*)(pos + 7 + k);
                        uint8_t x1 = *(uint8_t*)(pos + 7 + k + 1);
                        uint8_t x2 = *(uint8_t*)(pos + 7 + k + 2);
                        if (x0 == 0x48 && x1 == 0x33 && (x2 & 0x07) == 0x04) { cookie = true; break; }
                        if (x0 == 0x48 && x1 == 0x31 && (x2 & 0x38) == 0x20) { cookie = true; break; }
                    }
                    if (cookie) continue;

                    uint64_t val = *(uint64_t*)target;
                    if (val != 0) continue; // squadSpawningHand is NULL at startup

                    spawnSquadFuncCall = pos - moduleBase;
                    squadSpawningHand = target - moduleBase;
                    gameWorldOffset = squadSpawningHand - 0x4A0;
                    GameDataManagerMain = squadSpawningHand - 0x480;
                    std::cout << "  Found squadSpawningHand via MOV: RVA 0x"
                              << std::hex << squadSpawningHand << std::dec << "\n";
                    break;
                }

                // If MOV scan didn't find it, GameDataManagerMain stays 0
                // and dllmain will use runtime frequency discovery
                if (squadSpawningHand == 0) {
                    std::cout << "  squadSpawningHand not found in function (Steam binary?).\n";
                    std::cout << "  Will use runtime discovery for data offsets.\n";
                }
            }
        }

        // Hook offsets are always required.
        // Data offsets (GameDataManagerMain, squadSpawningHand, gameWorldOffset)
        // may be resolved at runtime if pattern scan can't find them.
        bool critical = charUpdateHook != 0
                     && buildingUpdateHook != 0
                     && spawnSquadBypass != 0;

        if (!charUpdateHook)     std::cerr << "  MISSING: charUpdateHook\n";
        if (!buildingUpdateHook) std::cerr << "  MISSING: buildingUpdateHook\n";
        if (!spawnSquadBypass)   std::cerr << "  MISSING: spawnSquadBypass\n";

        return critical;
    }
}
