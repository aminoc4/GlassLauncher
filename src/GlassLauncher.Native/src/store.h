// 数据与设置：与 WPF 版共用同一份 JSON（%LOCALAPPDATA%\GlassLauncher 或 GLASSLAUNCHER_DATA_DIR）。
#pragma once

#include <string>
#include <vector>
#include <windows.h>

namespace gl {

struct AppEntry
{
    std::wstring id;
    std::wstring displayName;
    std::wstring targetPath;
    std::wstring arguments;
    std::wstring workingDirectory;
    std::wstring iconCachePath;
    std::wstring sourcePath;
    std::wstring iconLocation;
    int sortOrder = 0;
    bool isBroken = false;

    // 运行期
    const wchar_t* initial() const { return displayName.empty() ? L"?" : displayName.c_str(); }
};

enum class ThemeMode { FollowSystem = 0, Light = 1, Dark = 2 };

struct Settings
{
    int version = 1;
    ThemeMode theme = ThemeMode::FollowSystem;
    double blurRadius = 12.0;
    double captureFps = 60.0;
    bool reduceBlurWhileDragging = false;
    bool topmost = false;
    bool checkBrokenOnStart = true;
    std::wstring hotkey = L"Alt+G";
};

class Store
{
public:
    static std::wstring DataDirectory();
    static std::wstring DataFilePath();
    static std::wstring SettingsFilePath();

    static bool LoadEntries(std::vector<AppEntry>& out, std::wstring* error);
    static bool SaveEntries(const std::vector<AppEntry>& entries, std::wstring* error);
    static Settings LoadSettings();
    static bool SaveSettings(const Settings& s, std::wstring* error);

    static bool ReadTextFile(const std::wstring& path, std::string& out);
    static bool WriteTextFileAtomic(const std::wstring& path, const std::string& text);
};

std::string ToUtf8(const std::wstring& w);
std::wstring FromUtf8(const std::string& s);

} // namespace gl
