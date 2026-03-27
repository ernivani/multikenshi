#pragma once
#include <string>
#include <vector>

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

    // Check if there are pending spawn requests
    bool hasPendingSpawns();

    // Clear all pending spawns (e.g., on disconnect)
    void clearPending();
}
