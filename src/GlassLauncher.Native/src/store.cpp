#include "store.h"
#include "json.h"

#include <shlobj.h>
#include <cstdio>
#include <algorithm>

namespace gl {

std::string ToUtf8(const std::wstring& w)
{
    if (w.empty()) return {};
    int len = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), nullptr, 0, nullptr, nullptr);
    std::string s((size_t)len, '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), s.data(), len, nullptr, nullptr);
    return s;
}

std::wstring FromUtf8(const std::string& s)
{
    if (s.empty()) return {};
    int len = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), nullptr, 0);
    std::wstring w((size_t)len, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), w.data(), len);
    return w;
}

std::wstring Store::DataDirectory()
{
    wchar_t buf[MAX_PATH] = {};
    DWORD n = GetEnvironmentVariableW(L"GLASSLAUNCHER_DATA_DIR", buf, MAX_PATH);
    if (n > 0 && n < MAX_PATH) return buf;

    PWSTR local = nullptr;
    std::wstring result;
    if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &local)))
    {
        result = local;
        result += L"\\GlassLauncher";
        CoTaskMemFree(local);
    }
    return result;
}

std::wstring Store::DataFilePath() { return DataDirectory() + L"\\launcher-data.json"; }
std::wstring Store::SettingsFilePath() { return DataDirectory() + L"\\settings.json"; }

bool Store::ReadTextFile(const std::wstring& path, std::string& out)
{
    out.clear();
    FILE* f = _wfopen(path.c_str(), L"rb");
    if (!f) return false;
    fseek(f, 0, SEEK_END);
    long size = ftell(f);
    fseek(f, 0, SEEK_SET);
    if (size > 0)
    {
        out.resize((size_t)size);
        size_t read = fread(out.data(), 1, (size_t)size, f);
        out.resize(read);
    }
    fclose(f);
    return true;
}

bool Store::WriteTextFileAtomic(const std::wstring& path, const std::string& text)
{
    std::wstring dir = path.substr(0, path.find_last_of(L'\\'));
    if (!dir.empty()) CreateDirectoryW(dir.c_str(), nullptr);

    std::wstring temp = path + L".tmp";
    FILE* f = _wfopen(temp.c_str(), L"wb");
    if (!f) return false;
    size_t written = fwrite(text.data(), 1, text.size(), f);
    fclose(f);
    if (written != text.size()) return false;

    // MoveFileEx 原子替换；失败时退化为覆盖
    if (!MoveFileExW(temp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
    {
        MoveFileExW(temp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING);
    }
    return true;
}

bool Store::LoadEntries(std::vector<AppEntry>& out, std::wstring* error)
{
    out.clear();
    std::string raw;
    if (!ReadTextFile(DataFilePath(), raw)) return true; // 文件不存在 = 空列表

    std::wstring text = FromUtf8(raw);
    if (text.empty()) return true;
    if (text[0] == L'\xFEFF') text.erase(0, 1);

    std::wstring parseError;
    gl::JsonPtr root = gl::JsonParser::Parse(text, &parseError);
    if (!root || root->type != gl::JsonValue::Type::Array)
    {
        if (error) *error = L"数据文件解析失败：" + parseError;
        return false;
    }

    for (const auto& item : root->array)
    {
        if (item->type != gl::JsonValue::Type::Object) continue;
        AppEntry e;
        e.id = item->GetString(L"Id");
        e.displayName = item->GetString(L"DisplayName");
        e.targetPath = item->GetString(L"TargetPath");
        e.arguments = item->GetString(L"Arguments");
        e.workingDirectory = item->GetString(L"WorkingDirectory");
        e.iconCachePath = item->GetString(L"IconCachePath");
        e.sourcePath = item->GetString(L"SourcePath");
        e.iconLocation = item->GetString(L"IconLocation");
        e.sortOrder = item->GetInt(L"SortOrder");
        const gl::JsonPtr* broken = item->Find(L"IsBroken");
        e.isBroken = broken && (*broken)->type == gl::JsonValue::Type::Bool && (*broken)->boolean;
        out.push_back(std::move(e));
    }

    std::stable_sort(out.begin(), out.end(), [](const AppEntry& a, const AppEntry& b) {
        return a.sortOrder < b.sortOrder;
    });
    return true;
}

bool Store::SaveEntries(const std::vector<AppEntry>& entries, std::wstring* error)
{
    std::wstring json = L"[\n";
    for (size_t i = 0; i < entries.size(); i++)
    {
        const AppEntry& e = entries[i];
        json += L"  {\n";
        json += L"    \"Id\": ";                 gl::JsonEscape(e.id, json);
        json += L",\n    \"DisplayName\": ";      gl::JsonEscape(e.displayName, json);
        json += L",\n    \"TargetPath\": ";       gl::JsonEscape(e.targetPath, json);
        json += L",\n    \"Arguments\": ";        gl::JsonEscape(e.arguments, json);
        json += L",\n    \"WorkingDirectory\": "; gl::JsonEscape(e.workingDirectory, json);
        json += L",\n    \"IconCachePath\": ";    gl::JsonEscape(e.iconCachePath, json);
        json += L",\n    \"SourcePath\": ";       gl::JsonEscape(e.sourcePath, json);
        json += L",\n    \"IconLocation\": ";     gl::JsonEscape(e.iconLocation, json);
        json += L",\n    \"SortOrder\": " + std::to_wstring(e.sortOrder);
        json += L",\n    \"IsBroken\": " + std::wstring(e.isBroken ? L"true" : L"false");
        json += L"\n  }";
        if (i + 1 < entries.size()) json += L",";
        json += L"\n";
    }
    json += L"]\n";

    if (!WriteTextFileAtomic(DataFilePath(), ToUtf8(json)))
    {
        if (error) *error = L"写入数据文件失败";
        return false;
    }
    return true;
}

Settings Store::LoadSettings()
{
    Settings s;
    std::string raw;
    if (!ReadTextFile(SettingsFilePath(), raw)) return s;
    std::wstring text = FromUtf8(raw);
    if (text.empty()) return s;
    gl::JsonPtr root = gl::JsonParser::Parse(text);
    if (!root || root->type != gl::JsonValue::Type::Object) return s;

    s.version = root->GetInt(L"SettingsVersion", 1);
    s.theme = (ThemeMode)root->GetInt(L"Theme", 0);
    s.blurRadius = root->GetNumber(L"BlurRadius", 12.0);
    s.captureFps = root->GetNumber(L"CaptureFps", 60.0);
    s.reduceBlurWhileDragging = root->GetBool(L"ReduceBlurWhileDragging", false);
    s.topmost = root->GetBool(L"Topmost", false);
    s.checkBrokenOnStart = root->GetBool(L"CheckBrokenOnStart", true);
    std::wstring hk = root->GetString(L"Hotkey");
    if (!hk.empty()) s.hotkey = hk;
    if (s.blurRadius < 2) s.blurRadius = 2;
    if (s.blurRadius > 32) s.blurRadius = 32;
    return s;
}

bool Store::SaveSettings(const Settings& s, std::wstring* error)
{
    std::wstring json = L"{\n";
    json += L"  \"SettingsVersion\": 1,\n";
    json += L"  \"Theme\": " + std::to_wstring((int)s.theme) + L",\n";
    json += L"  \"BlurRadius\": " + std::to_wstring((int)(s.blurRadius + 0.5)) + L",\n";
    json += L"  \"CaptureFps\": " + std::to_wstring((int)(s.captureFps + 0.5)) + L",\n";
    json += L"  \"ReduceBlurWhileDragging\": " + std::wstring(s.reduceBlurWhileDragging ? L"true" : L"false") + L",\n";
    json += L"  \"Topmost\": " + std::wstring(s.topmost ? L"true" : L"false") + L",\n";
    json += L"  \"CheckBrokenOnStart\": " + std::wstring(s.checkBrokenOnStart ? L"true" : L"false") + L",\n";
    json += L"  \"Hotkey\": ";
    gl::JsonEscape(s.hotkey, json);
    json += L"\n}\n";

    if (!WriteTextFileAtomic(SettingsFilePath(), ToUtf8(json)))
    {
        if (error) *error = L"写入设置失败";
        return false;
    }
    return true;
}

} // namespace gl
