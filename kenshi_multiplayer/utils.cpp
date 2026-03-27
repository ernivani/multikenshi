#include "pch.h"
#include "utils.h"
#include "offsets.h"
#include "gameState.h"
#include "gui_console.h"
#include <cstdio>
#include <vector>
#include <string>
#include <iostream>
#include <fstream>
#include <thread>

namespace utils {
    void spawn_console() {
        guiConsole::create();
    }
	std::vector<std::string> split(const std::string& str, const std::string& delim) {
		std::vector<std::string> rez;
		size_t wher = 0;
		size_t prev = 0;
		while (wher != -1) {
			wher = str.find(delim, wher + 1);
			rez.push_back(str.substr(prev, wher - prev));
			prev = wher + 1;
		}
		return rez;
	}
	__declspec(nothrow) bool isValidName(const char* name) {
		__try {
			if (!name || strlen(name) < 2) return false; // Must be at least 2 characters long
			for (size_t i = 0; name[i] != '\0'; ++i) {
				unsigned char c = (unsigned char)name[i];
				// Allow printable ASCII + common extended chars (accents etc)
				// Reject control chars (0-31) and DEL (127)
				if (c < 32 || c == 127) {
					return false;
				}
			}
			return true;
		}
		__except (EXCEPTION_EXECUTE_HANDLER) { return false; }
	}
	const char* pickMoreReadable(const char* derefName, const char* thisName) {
		bool valid1 = isValidName(derefName);
		bool valid2 = isValidName(thisName);

		if (valid1 && !valid2) { return derefName; }
		if (!valid1 && valid2) { return thisName;}

		if (!valid1 && !valid2) {
			return thisName; // Both invalid — silently return raw name
		}
		return (strlen(derefName) >= strlen(thisName)) ? derefName : thisName;
	}
	void pause() { int t;std::cin >> t; }
	void nop() {  }
	void log(std::string data) {
		std::ofstream file("C:\\Users\\sam\\Desktop\\logs.txt", std::ios::trunc); // Open in truncate mode (clears file)
		if (file.is_open()) {
			file << data;  // Write data to the file
			file.close();  // Close the file
		}
	}
	namespace asmb {
		std::vector<uint8_t> jmp = { 0xFF, 0x25, 0x00, 0x00, 0x00, 0x00 };
		std::vector<uint8_t> mov = { 0x48, 0x89, 0x1C, 0x25 };
		std::vector<uint8_t> ret = { 0xC3 };
		std::vector<uint8_t> nop = { 0x90 };
		std::vector<uint8_t> call = { 0xFF, 0x15, 0x02, 0x00, 0x00, 0x00, 0xEB, 0x08 };
		std::vector<uint8_t> saveRegisters = { 0x50,0x51,0x52,0x53,0x55,0x56,0x57,0x41,0x50,
			0x41,0x51,0x41,0x52,0x41,0x53,0x41,0x54,0x41,0x55,0x41,0x56,0x41,0x57 };
		std::vector<uint8_t> restoreRegisters = { 0x41,0x5F,0x41,0x5E,0x41,0x5D,0x41,0x5C,0x41,
			0x5B,0x41,0x5A,0x41,0x59,0x41,0x58,0x5F,0x5E,0x5D,0x5B,0x5A,0x59,0x58 };
	}
	void disableHook(long long hookLocation, const std::vector<uint8_t>& oldCode) {
		DWORD oldProtect;
		VirtualProtect(reinterpret_cast<void*>(hookLocation), oldCode.size(), PAGE_EXECUTE_READWRITE, &oldProtect);
		std::memcpy(reinterpret_cast<void*>(hookLocation), oldCode.data(), oldCode.size());
		VirtualProtect(reinterpret_cast<void*>(hookLocation), oldCode.size(), oldProtect, &oldProtect);
	}
	void createHook(long long hookLocation, const std::vector<uint8_t>& oldCode,
		const std::vector<uint8_t>& befMyFunc, void* myFunc, const std::vector<uint8_t>& aftMyFunc) {
		size_t minSize = (asmb::jmp.size() + sizeof(void*)); //14
		if (oldCode.size() < minSize) {
			std::cerr << "createHook: oldCode size must be at least: " << minSize;
			std::cin >> minSize;//pause execution
			return;
		}
		long long returnLocation = hookLocation + oldCode.size();

		// Trampoline layout:
		//   befMyFunc
		//   pushfq + push all 15 GP regs (save state)
		//   mov rbp,rsp; and rsp,-16; sub rsp,0x20 (align + shadow space)
		//   mov rax,<funcAddr>; call rax (call our callback)
		//   mov rsp,rbp (restore stack)
		//   pop all 15 GP regs + popfq (restore state)
		//   aftMyFunc
		//   oldCode (original replaced bytes)
		//   jmp [returnLocation]

		// Save: pushfq(1) + push rax,rcx,rdx,rbx,rbp,rsi,rdi(7) + push r8-r15(16) = 24 bytes
		uint8_t saveAll[] = {
			0x9C,                                                     // pushfq
			0x50, 0x51, 0x52, 0x53, 0x55, 0x56, 0x57,               // push rax-rdi (skip rsp)
			0x41,0x50, 0x41,0x51, 0x41,0x52, 0x41,0x53,             // push r8-r11
			0x41,0x54, 0x41,0x55, 0x41,0x56, 0x41,0x57              // push r12-r15
		};
		// Align stack + shadow space: 11 bytes
		uint8_t alignStack[] = {
			0x48, 0x89, 0xE5,       // mov rbp, rsp
			0x48, 0x83, 0xE4, 0xF0, // and rsp, -16
			0x48, 0x83, 0xEC, 0x20  // sub rsp, 0x20  (shadow space)
		};
		// Call: mov rax,imm64(10) + call rax(2) = 12 bytes
		uint8_t callPrefix[] = { 0x48, 0xB8 }; // mov rax, <8-byte addr follows>
		uint8_t callRax[] = { 0xFF, 0xD0 };    // call rax
		// Restore stack: 3 bytes
		uint8_t restoreRsp[] = { 0x48, 0x89, 0xEC }; // mov rsp, rbp
		// Restore: pop r15-r8(16) + pop rdi-rax(7) + popfq(1) = 24 bytes
		uint8_t restoreAll[] = {
			0x41,0x5F, 0x41,0x5E, 0x41,0x5D, 0x41,0x5C,             // pop r15-r12
			0x41,0x5B, 0x41,0x5A, 0x41,0x59, 0x41,0x58,             // pop r11-r8
			0x5F, 0x5E, 0x5D, 0x5B, 0x5A, 0x59, 0x58,              // pop rdi-rax (skip rsp)
			0x9D                                                      // popfq
		};

		size_t callBlockSize = sizeof(saveAll) + sizeof(alignStack)
			+ sizeof(callPrefix) + sizeof(void*) + sizeof(callRax)
			+ sizeof(restoreRsp) + sizeof(restoreAll);

		size_t size = befMyFunc.size() + callBlockSize + aftMyFunc.size()
			+ oldCode.size() + asmb::jmp.size() + sizeof(void*);

		char* data = reinterpret_cast<char*>(VirtualAlloc(nullptr, size, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE));
		if (data == 0)return;
		size_t ofst = 0;
		auto emit = [&](const void* src, size_t len) {
			std::memcpy(data + ofst, src, len); ofst += len;
		};

		emit(befMyFunc.data(), befMyFunc.size());
		emit(saveAll, sizeof(saveAll));
		emit(alignStack, sizeof(alignStack));
		emit(callPrefix, sizeof(callPrefix));
		emit(&myFunc, sizeof(void*));
		emit(callRax, sizeof(callRax));
		emit(restoreRsp, sizeof(restoreRsp));
		emit(restoreAll, sizeof(restoreAll));
		emit(aftMyFunc.data(), aftMyFunc.size());
		emit(oldCode.data(), oldCode.size());
		emit(asmb::jmp.data(), asmb::jmp.size());
		emit(&returnLocation, sizeof(returnLocation));

		DWORD oldProtect;
		VirtualProtect(reinterpret_cast<void*>(hookLocation), oldCode.size(), PAGE_EXECUTE_READWRITE, &oldProtect);
		std::memcpy(reinterpret_cast<void*>(hookLocation), asmb::jmp.data(), asmb::jmp.size());
		std::memcpy(reinterpret_cast<void*>(hookLocation + asmb::jmp.size()), &data, sizeof(void*));
		std::memset(reinterpret_cast<void*>(hookLocation + minSize), asmb::nop[0], oldCode.size() - minSize);
		VirtualProtect(reinterpret_cast<void*>(hookLocation), oldCode.size(), oldProtect, &oldProtect);
	}



	// Plain C helper with SEH — scans a memory region safely.
	// Returns number of matches found. Cannot have C++ objects (MSVC SEH restriction).
	__declspec(nothrow) static size_t scanRegionSafe(
		uint64_t* ptr, size_t count, uint64_t targetValue,
		uintptr_t alignedStart, uintptr_t* outBuf, size_t outBufCap)
	{
		size_t found = 0;
		__try {
			for (size_t i = 0; i < count; ++i) {
				if (ptr[i] == targetValue && found < outBufCap) {
					outBuf[found++] = alignedStart + i * 8;
				}
			}
		}
		__except (EXCEPTION_EXECUTE_HANDLER) {
			// Region became invalid during scan — return what we found so far
		}
		return found;
	}

	std::vector<uintptr_t> scanMemoryForValue(uint64_t targetValue) {
		std::vector<uintptr_t> results;
		SYSTEM_INFO sysInfo;
		GetSystemInfo(&sysInfo);

		uintptr_t startAddr = reinterpret_cast<uintptr_t>(sysInfo.lpMinimumApplicationAddress);
		uintptr_t endAddr = reinterpret_cast<uintptr_t>(sysInfo.lpMaximumApplicationAddress);

		// Temp buffer for SEH-safe scan (4096 matches per region should be plenty)
		static const size_t MATCH_BUF = 4096;
		uintptr_t* matchBuf = (uintptr_t*)HeapAlloc(GetProcessHeap(), 0, MATCH_BUF * sizeof(uintptr_t));
		if (!matchBuf) return results;

		MEMORY_BASIC_INFORMATION memInfo;
		while (startAddr < endAddr) {
			if (VirtualQuery(reinterpret_cast<LPCVOID>(startAddr), &memInfo, sizeof(memInfo))) {
				// Check if the region is committed and readable
				if (memInfo.State == MEM_COMMIT &&
					(memInfo.Protect & (PAGE_READONLY | PAGE_READWRITE |
						PAGE_WRITECOPY | PAGE_EXECUTE_READ |
						PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY)) &&
					!(memInfo.Protect & PAGE_GUARD)) {

					uintptr_t regionStart = reinterpret_cast<uintptr_t>(memInfo.BaseAddress);
					uintptr_t regionEnd = regionStart + memInfo.RegionSize;

					// Align the start address to 8 bytes
					uintptr_t alignedStart = (regionStart + 7) & ~7ULL;
					if (alignedStart >= regionEnd) {
						startAddr = regionEnd;
						continue;
					}

					// Calculate end of aligned addresses
					uintptr_t alignedEnd = regionEnd - 8;
					if (alignedEnd < alignedStart) {
						startAddr += memInfo.RegionSize;
						continue;
					}

					uint64_t* ptr = reinterpret_cast<uint64_t*>(alignedStart);
					size_t count = (alignedEnd - alignedStart) / 8 + 1;

					size_t found = scanRegionSafe(ptr, count, targetValue, alignedStart, matchBuf, MATCH_BUF);
					for (size_t j = 0; j < found; ++j) {
						results.push_back(matchBuf[j]);
					}
				}
				startAddr += memInfo.RegionSize;
			}
			else {
				break; // Exit on error
			}
		}

		HeapFree(GetProcessHeap(), 0, matchBuf);
		return results;
	}

	// SEH-safe helper: count data-section references in a memory region.
	__declspec(nothrow) static void countRefsInRegion(
		uint64_t* ptr, size_t count,
		uint64_t rangeStart, uint64_t rangeEnd,
		int* freq, size_t numSlots)
	{
		__try {
			for (size_t i = 0; i < count; ++i) {
				uint64_t v = ptr[i];
				if (v >= rangeStart && v < rangeEnd) {
					size_t idx = (size_t)(v - rangeStart) / 8;
					if (idx < numSlots)
						freq[idx]++;
				}
			}
		}
		__except (EXCEPTION_EXECUTE_HANDLER) {}
	}

	DataGlobalResult findMostReferencedGlobal(uintptr_t dataBase, size_t dataSize) {
		DataGlobalResult result = { 0, 0 };

		size_t numSlots = dataSize / 8;
		if (numSlots == 0) return result;

		int* freq = (int*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, numSlots * sizeof(int));
		if (!freq) return result;

		SYSTEM_INFO sysInfo;
		GetSystemInfo(&sysInfo);
		uintptr_t startAddr = reinterpret_cast<uintptr_t>(sysInfo.lpMinimumApplicationAddress);
		uintptr_t endAddr = reinterpret_cast<uintptr_t>(sysInfo.lpMaximumApplicationAddress);

		MEMORY_BASIC_INFORMATION memInfo;
		while (startAddr < endAddr) {
			if (VirtualQuery(reinterpret_cast<LPCVOID>(startAddr), &memInfo, sizeof(memInfo))) {
				if (memInfo.State == MEM_COMMIT &&
					(memInfo.Protect & (PAGE_READONLY | PAGE_READWRITE |
						PAGE_WRITECOPY | PAGE_EXECUTE_READ |
						PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY)) &&
					!(memInfo.Protect & PAGE_GUARD)) {

					uintptr_t regionStart = reinterpret_cast<uintptr_t>(memInfo.BaseAddress);
					uintptr_t regionEnd = regionStart + memInfo.RegionSize;
					uintptr_t aligned = (regionStart + 7) & ~7ULL;

					if (aligned < regionEnd - 8) {
						uint64_t* ptr = reinterpret_cast<uint64_t*>(aligned);
						size_t count = (regionEnd - aligned) / 8;
						countRefsInRegion(ptr, count, dataBase, dataBase + dataSize, freq, numSlots);
					}
				}
				startAddr += memInfo.RegionSize;
			}
			else {
				break;
			}
		}

		// Find the slot with the highest count
		for (size_t i = 0; i < numSlots; i++) {
			if ((size_t)freq[i] > result.hitCount) {
				result.hitCount = freq[i];
				result.address = dataBase + i * 8;
			}
		}

		HeapFree(GetProcessHeap(), 0, freq);
		return result;
	}
}
