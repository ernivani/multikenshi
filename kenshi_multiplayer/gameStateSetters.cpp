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

namespace gameState {
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
        setSpeed(std::to_string(speed));
    }

    static void applyCharacterPosition(const json& charData) {
        std::string name = charData.value("n", "");
        if (name.empty()) return;

        float x = charData.value("x", 0.0f);
        float y = charData.value("y", 0.0f);
        float z = charData.value("z", 0.0f);
        if (x == 0.0f && y == 0.0f && z == 0.0f) return;

        auto it = charsByName.find(name);
        if (it != charsByName.end()) {
            safeSetPosition(it->second, x, y, z);
        }
    }

    void applyWorldUpdate(const json& wu) {
        // Apply speed
        if (wu.contains("speed")) {
            float speed = wu["speed"].get<float>();
            applySpeedFromWU(speed);
        }

        // Apply other players' squads
        if (wu.contains("players")) {
            for (auto it = wu["players"].begin(); it != wu["players"].end(); ++it) {
                const json& playerData = *it;
                if (playerData.contains("squad")) {
                    for (auto sq = playerData["squad"].begin(); sq != playerData["squad"].end(); ++sq) {
                        applyCharacterPosition(*sq);
                    }
                }
            }
        }

        // Apply NPCs (from host)
        if (wu.contains("npcs")) {
            for (auto it = wu["npcs"].begin(); it != wu["npcs"].end(); ++it) {
                applyCharacterPosition(*it);
            }
        }

        // Buildings: position is readonly in Kenshi, so we skip position updates
        // but could update condition in the future
    }
}
