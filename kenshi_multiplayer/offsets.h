#pragma once
#include <cstdint>

namespace offsets {
    extern uintptr_t factionString;
    extern uintptr_t gameWorldOffset;
    extern uintptr_t setPaused;
    extern uintptr_t charUpdateHook;
    extern uintptr_t buildingUpdateHook;

    extern uintptr_t itemSpawningHand;
    extern uintptr_t itemSpawningMagic;
    extern uintptr_t spawnItemFunc;
    extern uintptr_t getSectionFromInvByName;

    extern uintptr_t GameDataManagerMain;
    extern uintptr_t GameDataManagerFoliage;
    extern uintptr_t GameDataManagerSquads;

    extern uintptr_t spawnSquadBypass;
    extern uintptr_t spawnSquadFuncCall;
    extern uintptr_t squadSpawningHand;
    extern uintptr_t characterCreate; // RootObjectFactory::process

    bool resolveAllOffsets();
}
