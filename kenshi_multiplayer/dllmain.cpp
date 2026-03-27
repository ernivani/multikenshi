#include "pch.h"
#include <windows.h>
#include <iostream>
#include <string>
#include <thread>
#include "network.h"
#include "utils.h"
#include "gameState.h"
#include "gameStateSetters.h"
#include "commands.h"
#include "offsets.h"
#include "gui_console.h"
#include "crashlog.h"
#include "patternScan.h"

#define DEFAULT_PORT 8080

void dllmain() {
    // 0. Init crash logging (before anything else)
    crashlog::init();
    crashlog::phase("gui_console_create");

    // 1. Create GUI console (redirects cout/cerr)
    guiConsole::create();

    std::cout << utils::ts() << "=== Kenshi Multiplayer Mod ===" << std::endl;
    std::cout << utils::ts() << "Scanning for offsets..." << std::endl;

    // 2. Resolve offsets via pattern scanning
    crashlog::phase("pattern_scan");
    bool offsetsOk = offsets::resolveAllOffsets();

    std::cout << std::hex;
    std::cout << "  charUpdateHook:      0x" << offsets::charUpdateHook << std::endl;
    std::cout << "  buildingUpdateHook:  0x" << offsets::buildingUpdateHook << std::endl;
    std::cout << "  spawnSquadBypass:    0x" << offsets::spawnSquadBypass << std::endl;
    std::cout << "  spawnSquadFuncCall:  0x" << offsets::spawnSquadFuncCall << std::endl;
    std::cout << "  squadSpawningHand:   0x" << offsets::squadSpawningHand << std::endl;
    std::cout << "  gameWorldOffset:     0x" << offsets::gameWorldOffset << std::endl;
    std::cout << "  GameDataManagerMain: 0x" << offsets::GameDataManagerMain << std::endl;
    std::cout << std::dec;

    // Check minimum required offsets (hooks for char/building)
    bool hooksOk = offsets::charUpdateHook != 0
                && offsets::buildingUpdateHook != 0
                && offsets::spawnSquadBypass != 0;

    if (!hooksOk) {
        std::cerr << utils::ts() << "ERROR: Failed to resolve hook offsets! Mod cannot function." << std::endl;
        guiConsole::setStatus("ERROR: Pattern scan failed");
        crashlog::phase("ABORT: pattern_scan_failed");
        return;
    }

    bool needRuntimeDiscovery = (offsets::GameDataManagerMain == 0);
    if (needRuntimeDiscovery) {
        std::cout << utils::ts() << "GameDataManagerMain not found by pattern scan." << std::endl;
        std::cout << utils::ts() << "Will discover at runtime after game loads." << std::endl;
    } else {
        std::cout << utils::ts() << "All critical offsets resolved." << std::endl;
    }

    // 3. Initialize Winsock early (doesn't depend on game offsets)
    crashlog::phase("winsock_init");
    if (!network::initializeWinsock()) {
        std::cerr << utils::ts() << "WSAStartup failed!" << std::endl;
        crashlog::phase("ABORT: winsock_failed");
        return;
    }

    // 4. Read config from kenshi_mp.ini next to the exe
    crashlog::phase("read_config");
    char serverIP[256] = "127.0.0.1";
    int serverPort = DEFAULT_PORT;
    char steamName[256] = "Player";
    char steamId[256] = "";
    {
        char iniPath[MAX_PATH];
        GetModuleFileNameA(NULL, iniPath, MAX_PATH);
        char* lastSlash = strrchr(iniPath, '\\');
        if (lastSlash) {
            strcpy_s(lastSlash + 1,
                     (size_t)(MAX_PATH - (lastSlash + 1 - iniPath)),
                     "kenshi_mp.ini");
            GetPrivateProfileStringA("Server", "IP", "127.0.0.1",
                                     serverIP, sizeof(serverIP), iniPath);
            serverPort = GetPrivateProfileIntA("Server", "Port",
                                               DEFAULT_PORT, iniPath);
            GetPrivateProfileStringA("Identity", "SteamName", "Player",
                                     steamName, sizeof(steamName), iniPath);
            GetPrivateProfileStringA("Identity", "SteamId", "",
                                     steamId, sizeof(steamId), iniPath);
        }
    }
    std::cout << utils::ts() << "Config: " << serverIP << ":" << serverPort << std::endl;
    std::cout << utils::ts() << "Identity: " << steamName << " (" << steamId << ")" << std::endl;
    network::steamName = steamName;
    network::steamId = steamId;

    // 5. Runtime discovery of GameDataManagerMain if pattern scan didn't find it
    if (needRuntimeDiscovery) {
        crashlog::phase("runtime_discovery");
        std::cout << utils::ts() << "Waiting for game to load (runtime discovery)..." << std::endl;
        guiConsole::setStatus("Waiting for game to load...");

        patternScan::SectionInfo data = patternScan::getDataSection();
        if (data.size == 0) {
            std::cerr << utils::ts() << "ERROR: Could not find .data section!" << std::endl;
            crashlog::phase("ABORT: no_data_section");
            return;
        }
        std::cout << utils::ts() << ".data section: 0x" << std::hex << data.base
                  << " size: 0x" << data.size << std::dec << std::endl;

        uintptr_t moduleBase = (uintptr_t)GetModuleHandle(NULL);
        int scanPass = 0;
        while (true) {
            Sleep(3000);
            scanPass++;
            crashlog::phase("runtime_discovery: frequency scan");
            auto result = utils::findMostReferencedGlobal(data.base, data.size);

            std::cout << utils::ts() << "  Discovery pass " << scanPass << ": best="
                      << result.hitCount << " hits at 0x" << std::hex
                      << result.address << std::dec << std::endl;

            if (result.hitCount >= 54949) {
                offsets::GameDataManagerMain = result.address - moduleBase;
                offsets::squadSpawningHand = offsets::GameDataManagerMain + 0x480;
                offsets::gameWorldOffset = offsets::GameDataManagerMain - 0x20;

                std::cout << utils::ts() << "  Discovered GameDataManagerMain: 0x" << std::hex
                          << offsets::GameDataManagerMain << std::endl;
                std::cout << utils::ts() << "  Derived squadSpawningHand:      0x"
                          << offsets::squadSpawningHand << std::endl;
                std::cout << utils::ts() << "  Derived gameWorldOffset:        0x"
                          << offsets::gameWorldOffset << std::dec << std::endl;
                break;
            }

            if (scanPass >= 120) { // 6 minutes max
                std::cerr << utils::ts() << "ERROR: Timed out waiting for game data." << std::endl;
                crashlog::phase("ABORT: discovery_timeout");
                return;
            }
        }
    }

    // 6. Initialize game world pointer
    crashlog::phase("init_game_world");
    gameState::initGameWorld();
    if (!gameState::gameWorld) {
        std::cerr << utils::ts() << "Failed to locate GameWorldClass in memory!" << std::endl;
        guiConsole::setStatus("ERROR: GameWorld not found");
        crashlog::phase("ABORT: gameworld_null");
        return;
    }

    // 7. Initialize setPaused function pointer (may be 0 if pattern not found)
    crashlog::phase("init_set_paused");
    gameState::initSetPaused();

    // 8. Wait for game to load and scan heap
    crashlog::phase("heap_scan_wait");
    std::cout << utils::ts() << "Scanning heap..." << std::endl;
    guiConsole::setStatus("Scanning game data...");
    gameState::scanHeap();
    std::cout << utils::ts() << "Game loaded." << std::endl;

    // 9. Late offset resolution (now that we have runtime-discovered addresses)
    crashlog::phase("late_offsets");
    if (offsets::spawnSquadFuncCall == 0 && offsets::squadSpawningHand != 0) {
        offsets::resolveSquadSpawnLate();
    }

    // 10. Setup hooks and tracked variables
    crashlog::phase("setup_hooks");
    gameState::init();
    std::cout << utils::ts() << "Hooks installed." << std::endl;

    // 10. Initialize commands
    crashlog::phase("commands_init");
    commands::init();
    std::cout << utils::ts() << "[Console Ready]" << std::endl;
    guiConsole::setStatus("Ready - not connected");

    // 11. Network connect with retry
    crashlog::phase("network_connect");
    std::cout << utils::ts() << "Connecting to " << serverIP << ":" << serverPort << "..." << std::endl;
    SOCKET client_fd = INVALID_SOCKET;
    for (int attempt = 1; attempt <= 5 && client_fd == INVALID_SOCKET; ++attempt) {
        client_fd = network::connectToServer(serverIP, serverPort);
        if (client_fd == INVALID_SOCKET) {
            std::cout << utils::ts() << "Connection attempt " << attempt << "/5 failed, retrying in 3s..." << std::endl;
            Sleep(3000);
        }
    }

    if (client_fd != INVALID_SOCKET) {
        crashlog::phase("connected");
        std::cout << utils::ts() << "Connected to server!" << std::endl;
        guiConsole::setStatus(std::string("Connected to ") + serverIP + ":" + std::to_string(serverPort));
        std::thread(network::receiveMessages, client_fd).join();
        network::cleanup(client_fd);
        guiConsole::setStatus("Disconnected");
    } else {
        std::cout << utils::ts() << "Failed to connect to server after 5 attempts." << std::endl;
        guiConsole::setStatus("Disconnected - server not found");
    }
    crashlog::phase("shutdown");
}

DWORD WINAPI threadWrapper(LPVOID param) {
    dllmain();
    return 0;
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID reserved) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(hModule);
        CreateThread(NULL, 0, threadWrapper, NULL, 0, 0);
    }
    return TRUE;
}
