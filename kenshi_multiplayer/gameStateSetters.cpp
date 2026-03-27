#include "pch.h"
#include "offsets.h"
#include "gameState.h"
#include "gameStateSetters.h"
#include "gameStateGetters.h"
#include "network.h"
#include "utils.h"
#include "json.hpp"
#include <string>
#include <iostream>

using json = nlohmann::json;

// Defined in network.cpp — sync loop enforces this at 60Hz
extern float g_enforcedSpeed;

namespace gameState {
    std::map<std::string, long long> remoteChars;
    extern structs::GameWorldClass* gameWorld;
    using GameWorldOffset = void(*)(structs::GameWorldClass*, bool);
    static GameWorldOffset gamePauseFn = nullptr;

    void initSetPaused() {
        if (offsets::setPaused != 0)
            gamePauseFn = reinterpret_cast<GameWorldOffset>(moduleBase + offsets::setPaused);
    }

    void setSpeed(const std::string& data) {
        float desiredSpeed = std::stof(data);
        gameWorld->gameSpeed = desiredSpeed;
        if (!gamePauseFn) return;
        if (desiredSpeed == 0&& gameWorld->paused == false) {//pause
            gamePauseFn(gameWorld, true);
        }else if(desiredSpeed != 0 && gameWorld->paused == true) {//unpause
            gameWorld->paused = false;
            gamePauseFn(gameWorld, true);
            gamePauseFn(gameWorld, false);
        }
    }
    void setFaction(const std::string& data) {// can only be done before game is launched
        if (offsets::factionString == 0) return; // Not resolved on Steam
        char* factionStringPtr = reinterpret_cast<char*>(moduleBase + offsets::factionString);
        DWORD oldProtect;//location is not writable so we cheat
        if (!VirtualProtect(factionStringPtr, data.size() + 1, PAGE_EXECUTE_READWRITE, &oldProtect)) {
            std::cerr << "Failed to change memory protection!" << std::endl;
            return;
        }
        std::memcpy(factionStringPtr, data.c_str(), data.size() + 1);
        VirtualProtect(factionStringPtr, data.size() + 1, oldProtect, &oldProtect);
    }
    void setPlayers(const std::string& data) {
        if (otherplayers == 0)return;
        for (auto i : utils::split(data, ";")) {
            auto ii = utils::split(i, "=");
            if (ii[0] == getOtherCharName()) {
                otherplayers->movement->position->fromString(ii[1]);
            }
        }
    }
    void setPlayer1(const std::string& data) {
        if (!otherplayers)return;
        if ("Player 1" == getOtherCharName()) {
            otherplayers->movement->position->fromString(data);
        }
    }
    void setPlayer2(const std::string& data) {
        if (!otherplayers)return;
        if ("Player 2" == getOtherCharName()) {
            otherplayers->movement->position->fromString(data);
        }
    }

    // ---------- New: Apply world update from server JSON ----------

    // SEH-safe position writer
    __declspec(nothrow) static bool safeSetPosition(structs::AnimationClassHuman* anim, float x, float y, float z) {
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

    static void applySpeedFromWU(float speed) {
        if (!gameWorld) return;
        if (g_enforcedSpeed != speed) {
            g_enforcedSpeed = speed;
            std::cout << utils::ts() << "Speed enforced: " << speed << std::endl;
        }
    }

    // SEH-safe lookup: walk the map to find a key, return the value pointer.
    // Can't use std::string inside __try (MSVC SEH restriction), so we
    // manually compare c_str() of each entry.
    __declspec(nothrow) static structs::AnimationClassHuman* safeFindChar(
        std::map<std::string, structs::AnimationClassHuman*>* mapPtr,
        const char* name, size_t nameLen)
    {
        __try {
            for (auto it = mapPtr->begin(); it != mapPtr->end(); ++it) {
                if (it->first.size() == nameLen && memcmp(it->first.c_str(), name, nameLen) == 0)
                    return it->second;
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {}
        return nullptr;
    }

    static void applyCharacterPosition(const json& charData) {
        std::string name = charData.value("n", "");
        if (name.empty()) return;

        float x = charData.value("x", 0.0f);
        float y = charData.value("y", 0.0f);
        float z = charData.value("z", 0.0f);
        if (x == 0.0f && y == 0.0f && z == 0.0f) return;

        structs::AnimationClassHuman* anim = safeFindChar(&charsByName, name.c_str(), name.size());
        if (anim) {
            safeSetPosition(anim, x, y, z);
            remoteChars[name] = GetTickCount64(); // mark as remotely controlled
        }
    }

    void applyWorldUpdate(const json& wu) {

        // Apply speed — server sends "speed" only when it wants to control us
        // (guest syncing to host, or /speed override). No "speed" field = host controls naturally.
        if (wu.contains("speed")) {
            float speed = wu["speed"].get<float>();
            applySpeedFromWU(speed);
        } else if (g_enforcedSpeed >= 0.0f) {
            // Server stopped sending speed (override cleared) — release control
            g_enforcedSpeed = -1.0f;
            std::cout << utils::ts() << "Speed control released — local control restored." << std::endl;
        }

        // Apply other players' characters
        if (wu.contains("players")) {
            for (auto it = wu["players"].begin(); it != wu["players"].end(); ++it) {
                const json& playerData = *it;
                if (playerData.contains("chars")) {
                    for (auto ch = playerData["chars"].begin(); ch != playerData["chars"].end(); ++ch) {
                        applyCharacterPosition(*ch);
                    }
                }
            }
        }

        // Buildings: position is readonly in Kenshi, so we skip position updates
        // but could update condition in the future
    }
}
