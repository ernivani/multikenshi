#include "pch.h"
#include "charHijack.h"
#include "gameState.h"
#include "utils.h"
#include <set>
#include <iostream>

namespace charHijack {

    static std::vector<SpawnRequest> pendingSpawns;
    static std::set<std::string> knownChars; // chars we've seen before

    void queueSpawn(int playerId, const std::string& name, float x, float y, float z) {
        SpawnRequest req;
        req.playerId = playerId;
        req.charName = name;
        req.x = x;
        req.y = y;
        req.z = z;
        pendingSpawns.push_back(req);
        std::cout << utils::ts() << "Spawn queued: '" << name << "' for player " << playerId << std::endl;
    }

    bool hasPendingSpawns() {
        return !pendingSpawns.empty();
    }

    void clearPending() {
        pendingSpawns.clear();
        knownChars.clear();
    }

    // SEH-safe position writer (same as in gameStateSetters.cpp)
    __declspec(nothrow) static bool safeSetPos(structs::AnimationClassHuman* anim, float x, float y, float z) {
        __try {
            if (!anim || !anim->movement || !anim->movement->position) return false;
            anim->movement->position->x = x;
            anim->movement->position->y = y;
            anim->movement->position->z = z;
            return true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            return false;
        }
    }

    // SEH-safe name + pointer collector for detecting new chars
    struct CharInfo {
        const char* name;
        structs::AnimationClassHuman* anim;
    };

    __declspec(nothrow) static int safeCollectNewChars(
        std::map<std::string, structs::AnimationClassHuman*>* mapPtr,
        std::set<std::string>* known,
        CharInfo* out, int maxOut)
    {
        int count = 0;
        __try {
            for (auto it = mapPtr->begin(); it != mapPtr->end() && count < maxOut; ++it) {
                if (known->count(it->first) == 0) {
                    out[count].name = it->first.c_str();
                    out[count].anim = it->second;
                    count++;
                }
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {}
        return count;
    }

    int processHijacks() {
        if (pendingSpawns.empty()) return 0;

        // Find new characters that weren't in knownChars
        CharInfo newChars[64];
        int newCount = safeCollectNewChars(&gameState::charsByName, &knownChars, newChars, 64);

        int hijacked = 0;

        for (int i = 0; i < newCount && !pendingSpawns.empty(); ++i) {
            std::string charName(newChars[i].name);

            // Skip the player's own character
            if (gameState::player == newChars[i].anim) {
                knownChars.insert(charName);
                continue;
            }

            // Hijack this NPC for the pending spawn request
            SpawnRequest& req = pendingSpawns.front();

            if (safeSetPos(newChars[i].anim, req.x, req.y, req.z)) {
                // Mark as remote-controlled
                gameState::remoteChars[charName] = GetTickCount64();

                std::cout << utils::ts() << "HIJACKED: '" << charName
                          << "' -> player " << req.playerId
                          << " at (" << req.x << ", " << req.y << ", " << req.z << ")" << std::endl;

                pendingSpawns.erase(pendingSpawns.begin());
                hijacked++;
            }

            knownChars.insert(charName);
        }

        // Also add remaining new chars to known set (so we don't try to hijack them later)
        for (int i = 0; i < newCount; ++i) {
            knownChars.insert(std::string(newChars[i].name));
        }

        return hijacked;
    }
}
