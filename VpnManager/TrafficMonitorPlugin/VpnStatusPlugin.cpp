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
    std::wstring StatePath()
    {
        wchar_t buffer[MAX_PATH]{}; GetEnvironmentVariableW(L"LOCALAPPDATA", buffer, MAX_PATH);
        return std::wstring(buffer) + L"\\VpnManager\\vpn-status.json";
    }
    std::wstring SettingsPath()
    {
        wchar_t buffer[MAX_PATH]{}; GetEnvironmentVariableW(L"LOCALAPPDATA", buffer, MAX_PATH);
        return std::wstring(buffer) + L"\\VpnManager\\vpn-display-settings.ini";
    }
    std::wstring Utf8ToWide(const std::string& value)
    {
        if (value.empty()) return L"";
        const int length = MultiByteToWideChar(CP_UTF8, 0, value.data(), (int)value.size(), nullptr, 0);
        std::wstring result(length, L'\0'); MultiByteToWideChar(CP_UTF8, 0, value.data(), (int)value.size(), result.data(), length); return result;
    }
    std::string ReadFile(const std::wstring& path)
    {
        std::ifstream stream(std::filesystem::path(path), std::ios::binary); return { std::istreambuf_iterator<char>(stream), {} };
    }
    std::string JsonString(const std::string& json, const char* key)
    {
        const std::regex expression(std::string("\\\"") + key + "\\\"\\s*:\\s*\\\"([^\\\"]*)\\\""); std::smatch match;
        if (!std::regex_search(json, match, expression)) return "";
        const auto encoded = match[1].str(); std::string result;
        for (size_t i = 0; i < encoded.size(); ++i)
        {
            if (encoded[i] != '\\' || i + 1 >= encoded.size()) { result += encoded[i]; continue; }
            const char escaped = encoded[++i];
            if (escaped == 'n') { result += '\n'; continue; }
            if (escaped == 'r') { result += '\r'; continue; }
            if (escaped == 't') { result += '\t'; continue; }
            if (escaped != 'u' || i + 4 >= encoded.size()) { result += escaped; continue; }
            const auto hex = encoded.substr(i + 1, 4); unsigned int code = 0;
            if (sscanf_s(hex.c_str(), "%x", &code) != 1) { result += "\\u"; continue; }
            i += 4;
            if (code < 0x80) result += static_cast<char>(code);
            else if (code < 0x800) { result += static_cast<char>(0xC0 | (code >> 6)); result += static_cast<char>(0x80 | (code & 0x3F)); }
            else { result += static_cast<char>(0xE0 | (code >> 12)); result += static_cast<char>(0x80 | ((code >> 6) & 0x3F)); result += static_cast<char>(0x80 | (code & 0x3F)); }
        }
        return result;
    }
    bool JsonBool(const std::string& json, const char* key)
    {
        const std::regex expression(std::string("\\\"") + key + "\\\"\\s*:\\s*(true|false)"); std::smatch match;
        return std::regex_search(json, match, expression) && match[1].str() == "true";
    }
}

void VpnStatusItem::Refresh()
{
    LoadDisplaySettings();
    const auto path = StatePath();
    std::error_code error;
    if (!std::filesystem::exists(path, error) || std::filesystem::last_write_time(path, error) < std::filesystem::file_time_type::clock::now() - std::chrono::seconds(10))
    {
        m_value = L"VPN 状态过期"; m_tooltip = L"VPN 管理器未在最近 10 秒更新状态。单击打开管理器。"; return;
    }
    const auto json = ReadFile(path);
    const auto display = JsonString(json, "displayText"); const auto tooltip = JsonString(json, "tooltip");
    if (display.empty() || !JsonBool(json, "isFresh")) { m_value = L"VPN 状态过期"; m_tooltip = L"状态快照无效或已过期。"; return; }
    m_value = Utf8ToWide(display); m_tooltip = Utf8ToWide(tooltip);
}
const wchar_t* VpnStatusItem::GetItemName() const { return L"VPN 状态"; }
const wchar_t* VpnStatusItem::GetItemId() const { return L"vpn-manager-status-v1"; }
const wchar_t* VpnStatusItem::GetItemLableText() const { return L""; }
const wchar_t* VpnStatusItem::GetItemValueText() const { return m_value.c_str(); }
const wchar_t* VpnStatusItem::GetItemValueSampleText() const { return L"美国 加利福尼亚州 洛杉矶\nHTTP/SOCKS5 :7890 · Clash"; }
void VpnStatusItem::LoadDisplaySettings()
{
    const auto path = SettingsPath(); wchar_t font[LF_FACESIZE]{}, color[16]{}, align[16]{};
    GetPrivateProfileStringW(L"display", L"font_name", L"Microsoft YaHei UI", font, LF_FACESIZE, path.c_str());
    GetPrivateProfileStringW(L"display", L"color", L"#1E77CF", color, 16, path.c_str());
    GetPrivateProfileStringW(L"display", L"alignment", L"left", align, 16, path.c_str());
    m_settings.font_name = font; m_settings.font_size = std::clamp(static_cast<int>(GetPrivateProfileIntW(L"display", L"font_size", 13, path.c_str())), 8, 28);
    unsigned int red = 30, green = 119, blue = 207; if (swscanf_s(color, L"#%02x%02x%02x", &red, &green, &blue) == 3) m_settings.color = RGB(red, green, blue);
    const std::wstring value = align; m_settings.alignment = value == L"center" ? IPluginDrawer::CENTER : value == L"right" ? IPluginDrawer::RIGHT : IPluginDrawer::LEFT;
}
int VpnStatusItem::GetItemWidthEx(void* hDC) const
{
    const auto dc = static_cast<HDC>(hDC); const int height = -MulDiv(m_settings.font_size, GetDeviceCaps(dc, LOGPIXELSY), 72);
    const auto font = CreateFontW(height, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, m_settings.font_name.c_str());
    const auto previous = SelectObject(dc, font); SIZE size{}; int widest = 0; size_t start = 0;
    while (start <= m_value.size()) { const auto end = m_value.find(L'\n', start); const auto length = (end == std::wstring::npos ? m_value.size() : end) - start; GetTextExtentPoint32W(dc, m_value.c_str() + start, static_cast<int>(length), &size); widest = widest > size.cx ? widest : size.cx; if (end == std::wstring::npos) break; start = end + 1; }
    SelectObject(dc, previous); DeleteObject(font); return widest + 12;
}
void VpnStatusItem::DrawItem(void* hDC, int x, int y, int w, int h, bool)
{
    const auto dc = static_cast<HDC>(hDC); const int height = -MulDiv(m_settings.font_size, GetDeviceCaps(dc, LOGPIXELSY), 72);
    const auto font = CreateFontW(height, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, m_settings.font_name.c_str());
    const auto previous = SelectObject(dc, font); const auto old_color = SetTextColor(dc, m_settings.color); const auto old_mode = SetBkMode(dc, TRANSPARENT);
    UINT format = DT_TOP | DT_WORDBREAK | DT_NOPREFIX | (m_settings.alignment == IPluginDrawer::CENTER ? DT_CENTER : m_settings.alignment == IPluginDrawer::RIGHT ? DT_RIGHT : DT_LEFT); RECT rect{ x + 6, y, x + w - 6, y + h }; DrawTextW(dc, m_value.c_str(), -1, &rect, format);
    SetBkMode(dc, old_mode); SetTextColor(dc, old_color); SelectObject(dc, previous); DeleteObject(font);
}
int VpnStatusItem::OnMouseEvent(MouseEventType type, int, int, void*, int)
{
    if (type != MT_LCLICKED) return 0;
    wchar_t localAppData[MAX_PATH]{}; GetEnvironmentVariableW(L"LOCALAPPDATA", localAppData, MAX_PATH);
    const std::wstring app = std::wstring(localAppData) + L"\\VpnManager\\VpnManager.exe";
    if (std::filesystem::exists(app)) ShellExecuteW(nullptr, L"open", app.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
    return 1;
}

VpnStatusPlugin& VpnStatusPlugin::Instance() { static VpnStatusPlugin instance; return instance; }
IPluginItem* VpnStatusPlugin::GetItem(int index) { return index == 0 ? &m_item : nullptr; }
void VpnStatusPlugin::DataRequired() { m_item.Refresh(); }
const wchar_t* VpnStatusPlugin::GetTooltipInfo() { return m_item.Tooltip().c_str(); }
const wchar_t* VpnStatusPlugin::GetInfo(PluginInfoIndex index)
{
    switch (index) { case TMI_NAME: return L"VPN 状态"; case TMI_DESCRIPTION: return L"读取 VPN 管理器状态快照并显示在任务栏。"; case TMI_AUTHOR: return L"Local"; case TMI_VERSION: return L"1.0"; case TMI_URL: return L""; default: return L""; }
}
extern "C" __declspec(dllexport) ITMPlugin* TMPluginGetInstance() { return &VpnStatusPlugin::Instance(); }
