#include "VpnStatusPlugin.h"
#include <windows.h>
#include <shellapi.h>
#include <filesystem>
#include <fstream>
#include <regex>

namespace
{
    std::wstring StatePath()
    {
        wchar_t buffer[MAX_PATH]{}; GetEnvironmentVariableW(L"LOCALAPPDATA", buffer, MAX_PATH);
        return std::wstring(buffer) + L"\\VpnManager\\vpn-status.json";
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
        return std::regex_search(json, match, expression) ? match[1].str() : "";
    }
    bool JsonBool(const std::string& json, const char* key)
    {
        const std::regex expression(std::string("\\\"") + key + "\\\"\\s*:\\s*(true|false)"); std::smatch match;
        return std::regex_search(json, match, expression) && match[1].str() == "true";
    }
}

void VpnStatusItem::Refresh()
{
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
const wchar_t* VpnStatusItem::GetItemValueSampleText() const { return L"美国 · HTTP/SOCKS5 :7890 · Clash"; }
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
