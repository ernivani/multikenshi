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

#define DEFAULT_PORT 8080

void dllmain() {
    // 1. Create GUI console (redirects cout/cerr)
    guiConsole::create();

    std::cout << "=== Kenshi Multiplayer Mod ===" << std::endl;
    std::cout << "Scanning for offsets..." << std::endl;

    // 2. Resolve offsets via pattern scanning
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

    if (!offsetsOk) {
        std::cerr << "ERROR: Failed to resolve critical offsets! Mod cannot function." << std::endl;
        guiConsole::setStatus("ERROR: Pattern scan failed");
        return;
    }
    std::cout << "All critical offsets resolved." << std::endl;

    // 3. Initialize game world pointer
    gameState::initGameWorld();
    if (!gameState::gameWorld) {
        std::cerr << "Failed to locate GameWorldClass in memory!" << std::endl;
        guiConsole::setStatus("ERROR: GameWorld not found");
        return;
    }

    // 4. Initialize setPaused function pointer (may be 0 if pattern not found)
    gameState::initSetPaused();

    // 5. Initialize Winsock
    if (!network::initializeWinsock()) {
        std::cerr << "WSAStartup failed!" << std::endl;
        return;
    }

    // 6. Read config from kenshi_mp.ini next to the exe
    char serverIP[256] = "127.0.0.1";
    int serverPort = DEFAULT_PORT;
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
        }
    }
    std::cout << "Config: " << serverIP << ":" << serverPort << std::endl;

    // 7. Wait for game to load and scan heap
    std::cout << "Waiting for game to load..." << std::endl;
    guiConsole::setStatus("Waiting for game to load...");
    gameState::scanHeap();

    // 8. Setup hooks and tracked variables
    gameState::init();

    // 9. Initialize commands
    commands::init();
    std::cout << "[Console Ready]" << std::endl;
    guiConsole::setStatus("Ready - not connected");

    // 10. Network connect with retry
    std::cout << "Connecting to " << serverIP << ":" << serverPort << "..." << std::endl;
    SOCKET client_fd = INVALID_SOCKET;
    for (int attempt = 1; attempt <= 5 && client_fd == INVALID_SOCKET; ++attempt) {
        client_fd = network::connectToServer(serverIP, serverPort);
        if (client_fd == INVALID_SOCKET) {
            std::cout << "Connection attempt " << attempt << "/5 failed, retrying in 3s..." << std::endl;
            Sleep(3000);
        }
    }

    if (client_fd != INVALID_SOCKET) {
        std::cout << "Connected to server!" << std::endl;
        guiConsole::setStatus(std::string("Connected to ") + serverIP + ":" + std::to_string(serverPort));
        std::thread(network::receiveMessages, client_fd).join();
        network::cleanup(client_fd);
        guiConsole::setStatus("Disconnected");
    } else {
        std::cout << "Failed to connect to server after 5 attempts." << std::endl;
        guiConsole::setStatus("Disconnected - server not found");
    }
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
