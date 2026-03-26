#include "pch.h"
#include "gui_console.h"
#include "commands.h"
#include <iostream>
#include <string>
#include <streambuf>

// Custom window messages
#define WM_APPENDLOG  (WM_APP + 1)
#define WM_SETSTATUS  (WM_APP + 2)
#define WM_CLEARLOG   (WM_APP + 3)

// Control IDs
#define IDC_LOG       101
#define IDC_INPUT     102
#define IDC_SEND      103
#define IDC_STATUS    104

namespace guiConsole {

    static HWND hWindow = NULL;
    static HWND hLogEdit = NULL;
    static HWND hInputEdit = NULL;
    static HWND hSendButton = NULL;
    static HWND hStatusBar = NULL;
    static HANDLE hThread = NULL;
    static WNDPROC oldInputProc = NULL;

    // Forward declarations
    static LRESULT CALLBACK WndProc(HWND, UINT, WPARAM, LPARAM);
    static LRESULT CALLBACK InputProc(HWND, UINT, WPARAM, LPARAM);
    static DWORD WINAPI guiThread(LPVOID);

    // ---- Custom streambuf that redirects to GUI log ----
    class GuiConsoleBuf : public std::streambuf {
        CRITICAL_SECTION cs;
        std::string buffer;
    public:
        GuiConsoleBuf() { InitializeCriticalSection(&cs); }
        ~GuiConsoleBuf() { DeleteCriticalSection(&cs); }
    protected:
        int overflow(int c) override {
            if (c == EOF) return c;
            EnterCriticalSection(&cs);
            buffer += (char)c;
            if (c == '\n') flushBuffer();
            LeaveCriticalSection(&cs);
            return c;
        }

        std::streamsize xsputn(const char* s, std::streamsize count) override {
            EnterCriticalSection(&cs);
            buffer.append(s, (size_t)count);
            if (buffer.find('\n') != std::string::npos) flushBuffer();
            LeaveCriticalSection(&cs);
            return count;
        }

        int sync() override {
            EnterCriticalSection(&cs);
            if (!buffer.empty()) flushBuffer();
            LeaveCriticalSection(&cs);
            return 0;
        }
    private:
        void flushBuffer() {
            if (buffer.empty() || !hWindow) return;
            // Convert \n to \r\n for Win32 EDIT control
            std::string converted;
            converted.reserve(buffer.size() + 16);
            for (size_t i = 0; i < buffer.size(); ++i) {
                if (buffer[i] == '\n' && (i == 0 || buffer[i - 1] != '\r')) {
                    converted += '\r';
                }
                converted += buffer[i];
            }
            char* copy = _strdup(converted.c_str());
            PostMessage(hWindow, WM_APPENDLOG, 0, (LPARAM)copy);
            buffer.clear();
        }
    };

    static GuiConsoleBuf* consoleBuf = nullptr;
    static std::streambuf* oldCoutBuf = nullptr;
    static std::streambuf* oldCerrBuf = nullptr;

    // ---- Command submission from GUI input ----
    static void submitCommand() {
        char text[512];
        GetWindowTextA(hInputEdit, text, sizeof(text));
        if (text[0] == '\0') return;
        std::cout << "> " << text << "\n";
        commands::dispatch(std::string(text));
        SetWindowTextA(hInputEdit, "");
    }

    // ---- Input edit subclass (Enter key) ----
    static LRESULT CALLBACK InputProc(HWND hWnd, UINT uMsg, WPARAM wParam, LPARAM lParam) {
        if (uMsg == WM_KEYDOWN && wParam == VK_RETURN) {
            submitCommand();
            return 0;
        }
        return CallWindowProc(oldInputProc, hWnd, uMsg, wParam, lParam);
    }

    // ---- Main window procedure ----
    static LRESULT CALLBACK WndProc(HWND hWnd, UINT uMsg, WPARAM wParam, LPARAM lParam) {
        switch (uMsg) {

        case WM_APPENDLOG: {
            char* text = (char*)lParam;
            if (text && hLogEdit) {
                int len = GetWindowTextLength(hLogEdit);
                // Truncate if log gets too long (keep last ~50000 chars)
                if (len > 60000) {
                    SendMessageA(hLogEdit, EM_SETSEL, 0, len - 50000);
                    SendMessageA(hLogEdit, EM_REPLACESEL, FALSE, (LPARAM)"");
                    len = GetWindowTextLength(hLogEdit);
                }
                SendMessageA(hLogEdit, EM_SETSEL, len, len);
                SendMessageA(hLogEdit, EM_REPLACESEL, FALSE, (LPARAM)text);
            }
            free(text);
            return 0;
        }

        case WM_SETSTATUS: {
            char* text = (char*)lParam;
            if (text && hStatusBar) {
                SetWindowTextA(hStatusBar, text);
            }
            free(text);
            return 0;
        }

        case WM_CLEARLOG:
            if (hLogEdit) SetWindowTextA(hLogEdit, "");
            return 0;

        case WM_COMMAND:
            if (LOWORD(wParam) == IDC_SEND && HIWORD(wParam) == BN_CLICKED) {
                submitCommand();
                SetFocus(hInputEdit);
                return 0;
            }
            break;

        case WM_SIZE: {
            RECT rc;
            GetClientRect(hWnd, &rc);
            int w = rc.right;
            int h = rc.bottom;
            int statusH = 20;
            int inputH = 24;
            int sendW = 60;
            int m = 4; // margin

            MoveWindow(hLogEdit,    m, m, w - 2*m, h - inputH - statusH - 3*m, TRUE);
            MoveWindow(hInputEdit,  m, h - inputH - statusH - m, w - sendW - 3*m, inputH, TRUE);
            MoveWindow(hSendButton, w - sendW - m, h - inputH - statusH - m, sendW, inputH, TRUE);
            MoveWindow(hStatusBar,  m, h - statusH, w - 2*m, statusH, TRUE);
            return 0;
        }

        case WM_CLOSE:
            ShowWindow(hWnd, SW_MINIMIZE);
            return 0; // Don't destroy, just minimize

        case WM_DESTROY:
            PostQuitMessage(0);
            return 0;
        }
        return DefWindowProc(hWnd, uMsg, wParam, lParam);
    }

    // ---- GUI thread ----
    static DWORD WINAPI guiThread(LPVOID param) {
        HANDLE readyEvent = (HANDLE)param;
        HINSTANCE hInst = GetModuleHandle(NULL);

        // Register window class
        WNDCLASSEX wc = {};
        wc.cbSize = sizeof(WNDCLASSEX);
        wc.style = CS_HREDRAW | CS_VREDRAW;
        wc.lpfnWndProc = WndProc;
        wc.hInstance = hInst;
        wc.hCursor = LoadCursor(NULL, IDC_ARROW);
        wc.hbrBackground = (HBRUSH)(COLOR_WINDOW + 1);
        wc.lpszClassName = L"KenshiMPConsole";
        RegisterClassEx(&wc);

        // Create main window
        hWindow = CreateWindowEx(
            0, L"KenshiMPConsole", L"Kenshi Multiplayer",
            WS_OVERLAPPEDWINDOW,
            CW_USEDEFAULT, CW_USEDEFAULT, 700, 500,
            NULL, NULL, hInst, NULL);

        // Log area: multiline, readonly, vertical scroll
        hLogEdit = CreateWindowEx(
            WS_EX_CLIENTEDGE, L"EDIT", L"",
            WS_CHILD | WS_VISIBLE | WS_VSCROLL | ES_MULTILINE | ES_READONLY | ES_AUTOVSCROLL,
            4, 4, 684, 400,
            hWindow, (HMENU)IDC_LOG, hInst, NULL);

        // Command input
        hInputEdit = CreateWindowEx(
            WS_EX_CLIENTEDGE, L"EDIT", L"",
            WS_CHILD | WS_VISIBLE | ES_AUTOHSCROLL,
            4, 408, 616, 24,
            hWindow, (HMENU)IDC_INPUT, hInst, NULL);

        // Subclass input edit for Enter key handling
        oldInputProc = (WNDPROC)SetWindowLongPtr(hInputEdit, GWLP_WNDPROC, (LONG_PTR)InputProc);

        // Send button
        hSendButton = CreateWindowEx(
            0, L"BUTTON", L"Send",
            WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON,
            624, 408, 60, 24,
            hWindow, (HMENU)IDC_SEND, hInst, NULL);

        // Status bar
        hStatusBar = CreateWindowEx(
            0, L"STATIC", L"Initializing...",
            WS_CHILD | WS_VISIBLE | SS_LEFT,
            4, 436, 684, 20,
            hWindow, (HMENU)IDC_STATUS, hInst, NULL);

        // Set monospace font on all controls
        HFONT hFont = CreateFont(14, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
            DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
            DEFAULT_QUALITY, FIXED_PITCH | FF_MODERN, L"Consolas");
        SendMessage(hLogEdit,    WM_SETFONT, (WPARAM)hFont, TRUE);
        SendMessage(hInputEdit,  WM_SETFONT, (WPARAM)hFont, TRUE);
        SendMessage(hSendButton, WM_SETFONT, (WPARAM)hFont, TRUE);
        SendMessage(hStatusBar,  WM_SETFONT, (WPARAM)hFont, TRUE);

        ShowWindow(hWindow, SW_SHOW);
        UpdateWindow(hWindow);

        // Signal that window is ready
        SetEvent(readyEvent);

        // Message loop
        MSG msg;
        while (GetMessage(&msg, NULL, 0, 0)) {
            TranslateMessage(&msg);
            DispatchMessage(&msg);
        }
        return 0;
    }

    // ---- Public API ----

    void create() {
        HANDLE readyEvent = CreateEvent(NULL, TRUE, FALSE, NULL);
        hThread = CreateThread(NULL, 0, guiThread, readyEvent, 0, NULL);
        WaitForSingleObject(readyEvent, 5000);
        CloseHandle(readyEvent);

        // Redirect cout and cerr to the GUI log
        consoleBuf = new GuiConsoleBuf();
        oldCoutBuf = std::cout.rdbuf(consoleBuf);
        oldCerrBuf = std::cerr.rdbuf(consoleBuf);
    }

    void destroy() {
        if (oldCoutBuf) std::cout.rdbuf(oldCoutBuf);
        if (oldCerrBuf) std::cerr.rdbuf(oldCerrBuf);
        delete consoleBuf;
        consoleBuf = nullptr;
        if (hWindow) PostMessage(hWindow, WM_DESTROY, 0, 0);
    }

    void appendLog(const std::string& text) {
        if (!hWindow) return;
        char* copy = _strdup(text.c_str());
        PostMessage(hWindow, WM_APPENDLOG, 0, (LPARAM)copy);
    }

    void setStatus(const std::string& text) {
        if (!hWindow) return;
        char* copy = _strdup(text.c_str());
        PostMessage(hWindow, WM_SETSTATUS, 0, (LPARAM)copy);
    }

    void clearLog() {
        if (!hWindow) return;
        PostMessage(hWindow, WM_CLEARLOG, 0, 0);
    }

}
