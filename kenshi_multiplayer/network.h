#ifndef NETWORK_H
#define NETWORK_H

#include <winsock2.h>
#include <ws2tcpip.h>
#include <string>
#include <thread>
#include <atomic>

#pragma comment(lib, "ws2_32.lib")

namespace network {
	extern std::atomic<bool> running;
	extern int playerId;
	extern bool isHost;
	extern std::string steamName;
	extern std::string steamId;

	bool initializeWinsock();
	SOCKET connectToServer(const std::string& ip, int port);
	void receiveMessages(SOCKET client_fd);
	void sendLine(SOCKET client_fd, const std::string& json);
	void cleanup(SOCKET client_fd);
}
#endif // NETWORK_H
