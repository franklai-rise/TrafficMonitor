#include "VpnStatusPlugin.h"
#include <windows.h>
#include <shellapi.h>
#include <filesystem>
#include <fstream>
#include <regex>
#include <cstdio>
#include <algorithm>

namespace
{
    std::wstring UserLocalAppData()
    {
        // TrafficMonitor may have been started by a process with a stale LOCALAPPDATA.
        // USERPROFILE is stable for the interactive user and matches the manager's snapshot path.
        wchar_t profile[MAX_PATH]{};
        if (GetEnvironmentVariableW(L"USERPROFILE", profile, MAX_PATH) > 0)
            return std::wstring(profile) + L"\\AppData\\Local";

        wchar_t local[MAX_PATH]{};
        GetEnvironmentVariableW(L"LOCALAPPDATA", local, MAX_PATH);
        return local;
    }
    std::wstring StatePath()
    {
        return UserLocalAppData() + L"\\VpnManager\\vpn-status.json";
    }
    std::wstring SettingsPath()
    {
        return UserLocalAppData() + L"\\VpnManager\\vpn-display-settings.ini";
    }
    std::wstring Utf8ToWide(const std::string& value)
    {
        if (value.empty()) return L"";
        const int length = MultiByteToWideChar(CP_UTF8, 0, value.data(), (int)value.size(), nullptr, 0);
        std::wstring result(length, L'\0'); MultiByteToWideChar(CP_UTF8, 0, value.data(), (int)value.size(), result.data(), length); return result;
    }
    std::string ReadFile(const std::wstring& path)
    {
        const auto file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) return {};
        LARGE_INTEGER size{};
        if (!GetFileSizeEx(file, &size) || size.QuadPart < 0 || size.QuadPart > 65536) { CloseHandle(file); return {}; }
        std::string value(static_cast<size_t>(size.QuadPart), '\0'); DWORD read{};
        const auto ok = ::ReadFile(file, value.data(), static_cast<DWORD>(value.size()), &read, nullptr);
        CloseHandle(file); if (!ok) return {}; value.resize(read); return value;
    }
    void AppendUtf8(std::string& out, unsigned int code)
    {
        if (code < 0x80) out += static_cast<char>(code);
        else if (code < 0x800) { out += static_cast<char>(0xC0 | (code >> 6)); out += static_cast<char>(0x80 | (code & 63)); }
        else if (code < 0x10000) { out += static_cast<char>(0xE0 | (code >> 12)); out += static_cast<char>(0x80 | ((code >> 6) & 63)); out += static_cast<char>(0x80 | (code & 63)); }
        else { out += static_cast<char>(0xF0 | (code >> 18)); out += static_cast<char>(0x80 | ((code >> 12) & 63)); out += static_cast<char>(0x80 | ((code >> 6) & 63)); out += static_cast<char>(0x80 | (code & 63)); }
    }
    std::string JsonString(const std::string& json, const char* key)
    {
        const auto property = std::string("\"") + key + "\"";
        auto pos = json.find(property);
        if (pos == std::string::npos) return {};
        pos += property.size();
        while (pos < json.size() && std::isspace(static_cast<unsigned char>(json[pos]))) ++pos;
        if (pos >= json.size() || json[pos++] != ':') return {};
        while (pos < json.size() && std::isspace(static_cast<unsigned char>(json[pos]))) ++pos;
        if (pos >= json.size() || json[pos++] != '"') return {};
        const auto start = pos;
        bool escaped = false;
        for (; pos < json.size(); ++pos) {
            if (!escaped && json[pos] == '"') break;
            if (!escaped && json[pos] == '\\') escaped = true;
            else escaped = false;
        }
        if (pos == json.size()) return {};
        const auto encoded = json.substr(start, pos - start); std::string result;
        for (size_t i = 0; i < encoded.size(); ++i)
        {
            if (encoded[i] != '\\') { result += encoded[i]; continue; }
            if (++i >= encoded.size()) return {};
            const char c = encoded[i];
            if (c == 'n') result += '\n';
            else if (c == 'r') result += '\r';
            else if (c == 't') result += '\t';
            else if (c == 'b') result += '\b';
            else if (c == 'f') result += '\f';
            else if (c == '"' || c == '\\' || c == '/') result += c;
            else if (c == 'u')
            {
                if (i + 4 >= encoded.size()) return {};
                const auto hex = encoded.substr(i + 1, 4);
                if (hex.find_first_not_of("0123456789abcdefABCDEF") != std::string::npos) return {};
                unsigned int code = std::stoul(hex, nullptr, 16); i += 4;
                if (code >= 0xD800 && code <= 0xDBFF)
                {
                    if (i + 6 >= encoded.size() || encoded.substr(i + 1, 2) != "\\u") return {};
                    const auto lowHex = encoded.substr(i + 3, 4);
                    if (lowHex.find_first_not_of("0123456789abcdefABCDEF") != std::string::npos) return {};
                    const auto low = std::stoul(lowHex, nullptr, 16);
                    if (low < 0xDC00 || low > 0xDFFF) return {};
                    code = 0x10000 + ((code - 0xD800) << 10) + (low - 0xDC00); i += 6;
                }
                else if (code >= 0xDC00 && code <= 0xDFFF) return {};
                AppendUtf8(result, code);
            }
            else return {};
        }
        return result;
    }
    bool RecentObservation(const std::string& iso)
    {
        // Manager writes ISO 8601 DateTimeOffset with Z or an explicit UTC offset.
        const std::regex pattern(R"(^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.\d+)?(Z|[+-]\d{2}:\d{2})$)");
        std::smatch parts; if (!std::regex_match(iso, parts, pattern)) return false;
        SYSTEMTIME st{}; st.wYear = static_cast<WORD>(std::stoi(parts[1])); st.wMonth = static_cast<WORD>(std::stoi(parts[2]));
        st.wDay = static_cast<WORD>(std::stoi(parts[3])); st.wHour = static_cast<WORD>(std::stoi(parts[4]));
        st.wMinute = static_cast<WORD>(std::stoi(parts[5])); st.wSecond = static_cast<WORD>(std::stoi(parts[6]));
        FILETIME ft{}, now{}; if (!SystemTimeToFileTime(&st, &ft)) return false; GetSystemTimeAsFileTime(&now);
        ULARGE_INTEGER thenTicks{}, nowTicks{}; thenTicks.LowPart = ft.dwLowDateTime; thenTicks.HighPart = ft.dwHighDateTime;
        nowTicks.LowPart = now.dwLowDateTime; nowTicks.HighPart = now.dwHighDateTime;
        const auto zone = parts[7].str(); long long offset = 0;
        if (zone != "Z") offset = (std::stoll(zone.substr(1, 2)) * 60 + std::stoll(zone.substr(4, 2))) * 60 * (zone[0] == '+' ? 1 : -1);
        const auto age = (static_cast<long long>(nowTicks.QuadPart) - static_cast<long long>(thenTicks.QuadPart)) / 10000000 + offset;
        return age >= -5 && age <= 10;
    }    bool JsonBool(const std::string& json, const char* key)
    {
        const std::regex expression(std::string("\\\"") + key + "\\\"\\s*:\\s*(true|false)"); std::smatch match;
        return std::regex_search(json, match, expression) && match[1].str() == "true";
    }
}

void VpnStatusItem::Refresh(bool force)
{
    std::lock_guard<std::recursive_mutex> lock(m_mutex);
    try {
    const auto now = std::chrono::steady_clock::now();
    if (!force && now - m_last_snapshot_read < std::chrono::seconds(1)) return;
    m_last_snapshot_read = now;
    LoadDisplaySettings();
    const auto path = StatePath();
    std::error_code error;
    if (!std::filesystem::exists(path, error) || std::filesystem::last_write_time(path, error) < std::filesystem::file_time_type::clock::now() - std::chrono::seconds(10))
    {
        m_value = L"VPN 状态过期"; m_tooltip = L"VPN 管理器未在最近 10 秒更新状态。单击打开管理器。"; return;
    }
    const auto json = ReadFile(path);
    if (!std::regex_search(json, std::regex("\"schemaVersion\"\\s*:\\s*1\\s*[,}]")) || !RecentObservation(JsonString(json, "observedAt"))) { m_value = L"VPN 状态过期"; m_tooltip = L"快照版本不支持或采集时间已过期。"; return; }
    const auto display = JsonString(json, "displayText"); const auto tooltip = JsonString(json, "tooltip");
    if (display.empty() || !JsonBool(json, "isFresh")) { m_value = L"VPN 状态过期"; m_tooltip = L"状态快照无效或已过期。"; return; }
    m_value = Utf8ToWide(display); m_tooltip = Utf8ToWide(tooltip);
    } catch (...) { m_value = L"VPN 状态过期"; m_tooltip = L"无法读取状态快照，请打开管理器查看。"; }
}
const wchar_t* VpnStatusItem::GetItemName() const { return m_lineIndex == 0 ? L"VPN 地区" : L"VPN 接入方式"; }
const wchar_t* VpnStatusItem::GetItemId() const { return m_lineIndex == 0 ? L"vpn-manager-status-v1" : L"vpn-manager-status-line2-v1"; }
const wchar_t* VpnStatusItem::GetItemLableText() const { return L""; }
std::wstring VpnStatusItem::CurrentLine() const
{
    const auto split = m_value.find(L'\n');
    if (m_lineIndex == 0) return m_value.substr(0, split);
    if (split == std::wstring::npos) return {};
    auto line = m_value.substr(split + 1);
    std::replace(line.begin(), line.end(), L'\n', L' ');
    return line;
}
const wchar_t* VpnStatusItem::GetItemValueText() const { std::lock_guard<std::recursive_mutex> lock(m_mutex); thread_local std::wstring value; value = CurrentLine(); return value.c_str(); }
const wchar_t* VpnStatusItem::GetItemValueSampleText() const { return m_lineIndex == 0 ? L"美国 加利福尼亚州 洛杉矶" : L"HTTP/SOCKS5 :7890 · Clash"; }
void VpnStatusItem::LoadDisplaySettings()
{
    const auto path = SettingsPath(); wchar_t font[LF_FACESIZE]{}, color[16]{}, align[16]{};
    GetPrivateProfileStringW(L"display", L"font_name", L"Microsoft YaHei UI", font, LF_FACESIZE, path.c_str());
    GetPrivateProfileStringW(L"display", L"color", L"#1E77CF", color, 16, path.c_str());
    GetPrivateProfileStringW(L"display", L"alignment", L"left", align, 16, path.c_str());
    m_settings.font_name = font; m_settings.font_size = std::clamp(static_cast<int>(GetPrivateProfileIntW(L"display", L"font_size", 13, path.c_str())), 8, 28);
    unsigned int red = 30, green = 119, blue = 207; m_settings.color = RGB(red, green, blue);
    if (swscanf_s(color, L"#%02x%02x%02x", &red, &green, &blue) == 3) m_settings.color = RGB(red, green, blue);
    const std::wstring value = align; m_settings.alignment = value == L"center" ? IPluginDrawer::CENTER : value == L"right" ? IPluginDrawer::RIGHT : IPluginDrawer::LEFT;
}
int VpnStatusItem::GetItemWidthEx(void* hDC) const
{
    const_cast<VpnStatusItem*>(this)->Refresh();
    std::lock_guard<std::recursive_mutex> lock(m_mutex);
    const auto dc = static_cast<HDC>(hDC); if (!dc) return 260; const int height = -MulDiv(m_settings.font_size, GetDeviceCaps(dc, LOGPIXELSY), 72);
    const auto font = CreateFontW(height, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, m_settings.font_name.c_str());
    const auto previous = SelectObject(dc, font); SIZE size{}; int widest = 0; size_t start = 0;
    while (start <= m_value.size()) { const auto end = m_value.find(L'\n', start); const auto length = (end == std::wstring::npos ? m_value.size() : end) - start; GetTextExtentPoint32W(dc, m_value.c_str() + start, static_cast<int>(length), &size); widest = widest > size.cx ? widest : size.cx; if (end == std::wstring::npos) break; start = end + 1; }
    SelectObject(dc, previous); DeleteObject(font);
    return (std::max)(320, widest + 20);
}
void VpnStatusItem::DrawItem(void* hDC, int x, int y, int w, int h, bool)
{
    Refresh();
    std::lock_guard<std::recursive_mutex> lock(m_mutex);
    const auto dc = static_cast<HDC>(hDC); if (!dc || w <= 12 || h <= 0) return;
    const int pixels = (std::min)(MulDiv(m_settings.font_size, GetDeviceCaps(dc, LOGPIXELSY), 72), (std::max)(1, h - 1));
    const auto font = CreateFontW(-pixels, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, m_settings.font_name.c_str());
    const int saved = SaveDC(dc);
    IntersectClipRect(dc, x, y, x + w, y + h);
    SelectObject(dc, font); SetTextColor(dc, m_settings.color); SetBkMode(dc, TRANSPARENT);
    const UINT format = DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX |
        (m_settings.alignment == IPluginDrawer::CENTER ? DT_CENTER : m_settings.alignment == IPluginDrawer::RIGHT ? DT_RIGHT : DT_LEFT);
    RECT lineRect{ x + 6, y, x + w - 6, y + h };
    const auto line = CurrentLine();
    DrawTextW(dc, line.c_str(), -1, &lineRect, format);
    RestoreDC(dc, saved); DeleteObject(font);
}int VpnStatusItem::OnMouseEvent(MouseEventType type, int, int, void*, int)
{
    if (type != MT_LCLICKED) return 0;
    const std::wstring app = UserLocalAppData() + L"\\VpnManager\\VpnManager.exe";
    std::error_code error;
    if (std::filesystem::exists(app, error)) ShellExecuteW(nullptr, L"open", app.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
    return 1;
}

VpnStatusPlugin& VpnStatusPlugin::Instance() { static VpnStatusPlugin instance; return instance; }
IPluginItem* VpnStatusPlugin::GetItem(int index) { return index == 0 ? &m_item : index == 1 ? &m_second : nullptr; }
void VpnStatusPlugin::DataRequired() { m_item.Refresh(); m_second.Refresh(); }
const wchar_t* VpnStatusPlugin::GetTooltipInfo() { thread_local std::wstring tooltip; tooltip = m_item.Tooltip(); return tooltip.c_str(); }
const wchar_t* VpnStatusPlugin::GetInfo(PluginInfoIndex index)
{
    switch (index) { case TMI_NAME: return L"VPN 状态"; case TMI_DESCRIPTION: return L"仅读取 VPN 管理器快照；两个独立行显示地区和接入方式。"; case TMI_AUTHOR: return L"Local"; case TMI_VERSION: return L"1.2.0"; case TMI_URL: return L""; default: return L""; }
}
extern "C" __declspec(dllexport) ITMPlugin* TMPluginGetInstance() { return &VpnStatusPlugin::Instance(); }
