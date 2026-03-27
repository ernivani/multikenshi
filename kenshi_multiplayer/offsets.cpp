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
    uintptr_t characterCreate = 0;

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

        // characterCreate: find via string anchor "[RootObjectFactory::process] Character"
        {
            const char* anchor = "[RootObjectFactory::process] Character";
            uintptr_t funcAddr = patternScan::findFunctionByString(anchor, strlen(anchor));
            if (funcAddr) {
                characterCreate = funcAddr - moduleBase;
                // Check if it has mov rax, rsp prologue
                bool hasMovRaxRsp = (*(uint8_t*)funcAddr == 0x48 &&
                                     *(uint8_t*)(funcAddr+1) == 0x8B &&
                                     *(uint8_t*)(funcAddr+2) == 0xC4);
                std::cout << "  characterCreate:     0x" << std::hex << characterCreate << std::dec
                          << (hasMovRaxRsp ? " [mov rax,rsp]" : "") << "\n";
            } else {
                std::cout << "  characterCreate: not found (NPC hijacking unavailable)\n";
            }
        }

        // setPaused: find function that writes to GameWorldClass->paused (offset 0x8B9)
        // Scan .text for any instruction with displacement 0x8B9 (bytes: B9 08 00 00)
        // Accept any MOV byte or CMP byte instruction encoding
        {
            uint8_t dispBytes[] = { 0xB9, 0x08, 0x00, 0x00 };
            uintptr_t textEnd = text.base + text.size - 16;
            uintptr_t bestCandidate = 0;
            int hitCount = 0;

            for (uintptr_t pos = text.base + 4; pos < textEnd; pos++) {
                if (memcmp((void*)pos, dispBytes, 4) != 0) continue;
                hitCount++;

                // Check if this is a memory operand with mod=10 (disp32)
                // The byte before the displacement is the ModRM byte
                uint8_t modrm = *(uint8_t*)(pos - 1);
                uint8_t mod = (modrm >> 6) & 3;
                if (mod != 2) continue; // mod=10 means [reg+disp32]

                // Check the opcode byte(s) before ModRM
                uint8_t opcode = *(uint8_t*)(pos - 2);
                uint8_t prefix = *(uint8_t*)(pos - 3);

                bool isMemWrite = false;
                // 88 ModRM = mov [reg+disp32], r8 (byte register store)
                if (opcode == 0x88) isMemWrite = true;
                // C6 ModRM = mov byte [reg+disp32], imm8
                if (opcode == 0xC6) isMemWrite = true;
                // With REX prefix (40-4F): REX 88 ModRM or REX C6 ModRM
                if ((prefix >= 0x40 && prefix <= 0x4F) && (opcode == 0x88 || opcode == 0xC6))
                    isMemWrite = true;

                if (!isMemWrite) continue;

                // Found a byte write to offset 0x8B9. Walk backwards to find function start.
                uintptr_t instrAddr = pos - 2;
                if (prefix >= 0x40 && prefix <= 0x4F) instrAddr = pos - 3;

                uintptr_t funcStart = 0;
                for (uintptr_t back = instrAddr - 1; back > instrAddr - 2048; back--) {
                    // Look for INT3 padding (CC CC) indicating function boundary
                    if (*(uint8_t*)back == 0xCC && *(uint8_t*)(back - 1) == 0xCC) {
                        funcStart = back + 1;
                        break;
                    }
                }

                if (funcStart != 0) {
                    bestCandidate = funcStart;
                    std::cout << "  setPaused candidate at 0x" << std::hex
                              << (instrAddr - moduleBase) << " -> func 0x"
                              << (funcStart - moduleBase) << std::dec << "\n";
                    break;
                }
            }

            if (bestCandidate) {
                setPaused = bestCandidate - moduleBase;
                std::cout << "  setPaused:           0x" << std::hex << setPaused << std::dec << "\n";
            } else {
                std::cout << "  setPaused: not found (" << hitCount
                          << " disp hits, no write match)\n";
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

    bool resolveSquadSpawnLate() {
        if (spawnSquadFuncCall != 0) return true; // already found
        if (squadSpawningHand == 0) return false; // no target

        uintptr_t moduleBase = (uintptr_t)GetModuleHandle(NULL);
        uintptr_t targetAddr = moduleBase + squadSpawningHand;
        patternScan::SectionInfo text = patternScan::getTextSection();
        if (text.size == 0) return false;

        std::cout << "  Late scan: searching .text for refs to squadSpawningHand (0x"
                  << std::hex << targetAddr << std::dec << ")...\n";

        uintptr_t textEnd = text.base + text.size - 7;
        int refCount = 0;

        for (uintptr_t pos = text.base; pos < textEnd; pos++) {
            uint8_t b0 = *(uint8_t*)pos;
            uint8_t b1 = *(uint8_t*)(pos + 1);
            uint8_t b2 = *(uint8_t*)(pos + 2);

            // Check for MOV/LEA reg, [rip+disp32]
            bool isRipRelative = false;
            int instrLen = 0;

            // 48/4C 8B xx (MOV with REX.W) where ModRM has mod=00, rm=101 (RIP-relative)
            if ((b0 == 0x48 || b0 == 0x4C) && b1 == 0x8B && (b2 & 0xC7) == 0x05) {
                isRipRelative = true;
                instrLen = 7;
            }
            // 48/4C 8D xx (LEA with REX.W)
            if ((b0 == 0x48 || b0 == 0x4C) && b1 == 0x8D && (b2 & 0xC7) == 0x05) {
                isRipRelative = true;
                instrLen = 7;
            }

            if (!isRipRelative) continue;

            int32_t disp = *(int32_t*)(pos + 3);
            uintptr_t resolved = (pos + instrLen) + disp;

            if (resolved != targetAddr) continue;
            refCount++;

            // Skip __security_cookie refs (followed by xor reg, rsp)
            bool cookie = false;
            for (int k = 0; k < 10; k++) {
                uint8_t x0 = *(uint8_t*)(pos + instrLen + k);
                uint8_t x1 = *(uint8_t*)(pos + instrLen + k + 1);
                uint8_t x2 = *(uint8_t*)(pos + instrLen + k + 2);
                if (x0 == 0x48 && x1 == 0x33 && (x2 & 0x07) == 0x04) { cookie = true; break; }
                if (x0 == 0x48 && x1 == 0x31 && (x2 & 0x38) == 0x20) { cookie = true; break; }
            }
            if (cookie) continue;

            // Found a real reference to squadSpawningHand
            spawnSquadFuncCall = pos - moduleBase;

            // Also derive gameWorldOffset from squadSpawningHand
            if (gameWorldOffset == 0)
                gameWorldOffset = squadSpawningHand - 0x4A0;

            std::cout << "  Late scan: found spawnSquadFuncCall at 0x" << std::hex
                      << spawnSquadFuncCall << std::dec << " (ref #" << refCount << ")\n";
            return true;
        }

        std::cout << "  Late scan: " << refCount << " refs found, none usable for spawnSquadFuncCall\n";
        return false;
    }
}
