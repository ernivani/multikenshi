#include "pch.h"
#include "offsets.h"
#include "patternScan.h"
#include <iostream>

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

                // Search within ~2000 bytes for the funcCall sub-pattern:
                //   mov r9,[rsi+30]; lea r8,[rbp-60]; mov rcx,[rip+rel32]
                uint8_t funcPat[] = {
                    0x4C,0x8B,0x4E,0x30,
                    0x4C,0x8D,0x45,0xA0,
                    0x48,0x8B,0x0D
                };
                uintptr_t funcAddr = patternScan::scan(addr, 2000, funcPat, "xxxxxxxxxxx", 11);
                if (funcAddr) {
                    spawnSquadFuncCall = funcAddr - moduleBase;

                    // Extract RIP-relative offset from: 48 8B 0D [rel32]
                    // 48 8B 0D starts at funcAddr+8, rel32 at funcAddr+11
                    // instruction ends at funcAddr+15
                    int32_t rel32 = *(int32_t*)(funcAddr + 11);
                    uintptr_t target = (funcAddr + 15) + rel32;
                    squadSpawningHand = target - moduleBase;

                    // Derived offsets
                    gameWorldOffset = squadSpawningHand - 0x4A0;
                    GameDataManagerMain = squadSpawningHand - 0x480;
                }
            }
        }

        bool critical = charUpdateHook != 0
                     && buildingUpdateHook != 0
                     && spawnSquadBypass != 0
                     && spawnSquadFuncCall != 0
                     && squadSpawningHand != 0
                     && gameWorldOffset != 0
                     && GameDataManagerMain != 0;

        if (!charUpdateHook)     std::cerr << "  MISSING: charUpdateHook\n";
        if (!buildingUpdateHook) std::cerr << "  MISSING: buildingUpdateHook\n";
        if (!spawnSquadBypass)   std::cerr << "  MISSING: spawnSquadBypass\n";
        if (!spawnSquadFuncCall) std::cerr << "  MISSING: spawnSquadFuncCall\n";
        if (!squadSpawningHand)  std::cerr << "  MISSING: squadSpawningHand\n";

        return critical;
    }
}
