#include "pch.h"
#include "charHijack.h"
#include "gameState.h"
#include "utils.h"
#include <set>
#include <map>
#include <iostream>

namespace charHijack {

    static std::vector<SpawnRequest> pendingSpawns;
    static std::set<std::string> knownChars;

    // Maps remote char name -> hijacked NPC's AnimationClassHuman pointer
    // This is how applyCharacterPosition finds the NPC to move
    static std::map<std::string, structs::AnimationClassHuman*> hijackedChars;

    void queueSpawn(int playerId, const std::string& name, float x, float y, float z) {
        // Don't queue if already pending for this name
        for (auto it = pendingSpawns.begin(); it != pendingSpawns.end(); ++it) {
            if (it->charName == name) {
                // Just update position
                it->x = x; it->y = y; it->z = z;
                return;
            }
        }

        // Don't queue if already hijacked
        if (hijackedChars.count(name) > 0) return;

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
        hijackedChars.clear();
    }

    // Look up a hijacked NPC by its remote character name
    static long long lastHijackLog = 0;

    structs::AnimationClassHuman* findHijacked(const std::string& name) {
        auto it = hijackedChars.find(name);
        if (it != hijackedChars.end()) {
            // Log every 10s to confirm position updates are flowing
            long long now = GetTickCount64();
            if (now - lastHijackLog > 10000) {
                lastHijackLog = now;
                std::cout << utils::ts() << "Updating hijacked '" << name << "'" << std::endl;
            }
            return it->second;
        }
        return nullptr;
    }

    // SEH-safe position writer
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

        CharInfo newChars[64];
        int newCount = safeCollectNewChars(&gameState::charsByName, &knownChars, newChars, 64);

        int hijacked = 0;

        for (int i = 0; i < newCount && !pendingSpawns.empty(); ++i) {
            std::string npcName(newChars[i].name);

            // Skip the player's own character
            if (gameState::player == newChars[i].anim) {
                knownChars.insert(npcName);
                continue;
            }

            // Hijack this NPC for the pending spawn request
            SpawnRequest req = pendingSpawns.front();

            if (safeSetPos(newChars[i].anim, req.x, req.y, req.z)) {
                // Map remote name -> hijacked NPC pointer
                hijackedChars[req.charName] = newChars[i].anim;
                gameState::remoteChars[npcName] = GetTickCount64();

                std::cout << utils::ts() << "HIJACKED: '" << npcName
                          << "' -> '" << req.charName << "' (player " << req.playerId
                          << ") at (" << req.x << ", " << req.y << ", " << req.z << ")" << std::endl;

                pendingSpawns.erase(pendingSpawns.begin());
                hijacked++;
            }

            knownChars.insert(npcName);
        }

        // Add remaining new chars to known set
        for (int i = 0; i < newCount; ++i) {
            knownChars.insert(std::string(newChars[i].name));
        }

        return hijacked;
    }
}
