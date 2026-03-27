#pragma once
#include <string>
#include <vector>
#include "structs.h"

namespace charHijack {

    struct SpawnRequest {
        int playerId;
        std::string charName;
        float x, y, z;
    };

    // Queue a spawn request — next new NPC will be hijacked
    void queueSpawn(int playerId, const std::string& name, float x, float y, float z);

    // Called from sync loop — checks for new characters and hijacks if spawn pending
    // Returns number of successful hijacks this cycle
    int processHijacks();

    // Look up a hijacked NPC by remote character name
    structs::AnimationClassHuman* findHijacked(const std::string& name);

    // Check if there are pending spawn requests
    bool hasPendingSpawns();

    // Clear all pending spawns (e.g., on disconnect)
    void clearPending();
}
