#include "RadarScorePlugin.h"
#include <windows.h>
#include <shellapi.h>
#include <filesystem>
#include <algorithm>
#include <ctime>
#include <cwchar>

namespace
{
    std::wstring LocalAppData()
    {
        wchar_t profile[MAX_PATH]{};
        if (GetEnvironmentVariableW(L"USERPROFILE", profile, MAX_PATH) > 0)
            return std::wstring(profile) + L"\\AppData\\Local";
        wchar_t local[MAX_PATH]{};
        GetEnvironmentVariableW(L"LOCALAPPDATA", local, MAX_PATH);
        return local;
    }

    std::wstring StateDirectory()
    {
        return LocalAppData() + L"\\CodexRadarTrafficMonitor";
    }

    std::wstring HostPath()
    {
        return StateDirectory() + L"\\CodexRadarHost.exe";
    }

    bool HostExists()
    {
        HANDLE mutex = OpenMutexW(SYNCHRONIZE, FALSE, L"Local\\CodexRadarTrafficMonitor.SingleInstance");
        if (!mutex) return false;
        CloseHandle(mutex);
        return true;
    }

    std::wstring ReadIniValue(const wchar_t* key, const wchar_t* fallback)
    {
        wchar_t buffer[4096]{};
        GetPrivateProfileStringW(L"status", key, fallback, buffer, static_cast<DWORD>(std::size(buffer)), (StateDirectory() + L"\\status.ini").c_str());
        return buffer;
    }
}

const wchar_t* RadarScoreItem::GetItemName() const { return L"GPT 雷达评分"; }
const wchar_t* RadarScoreItem::GetItemId() const { return L"codex-radar-score-v1"; }
const wchar_t* RadarScoreItem::GetItemLableText() const { return L""; }
const wchar_t* RadarScoreItem::GetItemValueText() const
{
    std::lock_guard<std::mutex> lock(m_mutex);
    thread_local std::wstring value;
    value = m_value;
    return value.c_str();
}
const wchar_t* RadarScoreItem::GetItemValueSampleText() const { return L"! A100x S100x L100x"; }
int RadarScoreItem::GetItemWidthEx(void* hDC) const
{
    const auto dc = static_cast<HDC>(hDC);
    return dc ? MulDiv(200, GetDeviceCaps(dc, LOGPIXELSX), 96) : 200;
}
void RadarScoreItem::DrawItem(void* hDC, int x, int y, int w, int h, bool)
{
    const auto dc = static_cast<HDC>(hDC);
    if (!dc || w <= 6 || h <= 0) return;
    const auto saved = SaveDC(dc);
    IntersectClipRect(dc, x, y, x + w, y + h);
    SetBkMode(dc, TRANSPARENT);
    const auto value = GetItemValueText();
    RECT rect{ x + 3, y, x + w - 3, y + h };
    DrawTextW(dc, value, -1, &rect, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX);
    RestoreDC(dc, saved);
}

void RadarScoreItem::ReadSnapshot()
{
    std::error_code error;
    const auto path = StateDirectory() + L"\\status.ini";
    if (!std::filesystem::exists(path, error))
    {
        m_value = L"GPT 雷达同步中";
        m_tooltip = L"后台正在读取众测雷达公开数据。";
        return;
    }
    const auto statusPath = StateDirectory() + L"\\status.ini";
    WritePrivateProfileStringW(nullptr, nullptr, nullptr, statusPath.c_str());
    auto value = ReadIniValue(L"compact", L"");
    if (value.empty()) value = ReadIniValue(L"value", L"");
    auto tooltip = ReadIniValue(L"tooltip", L"");
    const auto updated = ReadIniValue(L"updatedUnixSeconds", L"0");
    wchar_t* end{};
    const auto updatedSeconds = _wcstoi64(updated.c_str(), &end, 10);
    const auto nowSeconds = static_cast<long long>(std::time(nullptr));
    const bool tooOld = end != updated.c_str() && updatedSeconds > 0 && nowSeconds - updatedSeconds > 300;
    const bool markedStale = ReadIniValue(L"stale", L"1") == L"1";
    if (value.empty()) value = L"GPT 雷达同步中";
    if (tooltip.empty()) tooltip = L"后台正在读取众测雷达公开数据。";
    if ((tooOld || (markedStale && updatedSeconds > 0)) && value.rfind(L"! ", 0) != 0) value = L"! " + value;
    if (tooOld && tooltip.find(L"过期") == std::wstring::npos) tooltip += L" | 本机缓存超过 5 分钟未成功更新。";
    m_value = std::move(value);
    m_tooltip = std::move(tooltip);
}

void RadarScoreItem::EnsureBackgroundHost()
{
    const auto now = std::chrono::steady_clock::now();
    if (now - m_last_launch_attempt < std::chrono::seconds(8)) return;
    m_last_launch_attempt = now;
    if (HostExists()) return;
    const auto app = HostPath();
    std::error_code error;
    if (!std::filesystem::exists(app, error)) return;
    const auto arguments = L"--background --parent-pid=" + std::to_wstring(GetCurrentProcessId());
    ShellExecuteW(nullptr, L"open", app.c_str(), arguments.c_str(), StateDirectory().c_str(), SW_HIDE);
}

bool RadarScoreItem::OpenDetails()
{
    const auto app = HostPath();
    std::error_code error;
    if (!std::filesystem::exists(app, error)) return false;
    const auto arguments = L"--show --parent-pid=" + std::to_wstring(GetCurrentProcessId());
    const auto result = reinterpret_cast<INT_PTR>(ShellExecuteW(nullptr, L"open", app.c_str(), arguments.c_str(), StateDirectory().c_str(), SW_SHOWNORMAL));
    return result > 32;
}

void RadarScoreItem::Refresh(bool force)
{
    std::lock_guard<std::mutex> lock(m_mutex);
    const auto now = std::chrono::steady_clock::now();
    if (!force && now - m_last_read < std::chrono::seconds(1)) return;
    m_last_read = now;
    EnsureBackgroundHost();
    ReadSnapshot();
}

std::wstring RadarScoreItem::Tooltip() const
{
    std::lock_guard<std::mutex> lock(m_mutex);
    return m_tooltip;
}

int RadarScoreItem::OnMouseEvent(MouseEventType type, int, int, void*, int)
{
    if (type == MT_LCLICKED) return 1;
    if (type == MT_DBCLICKED)
    {
        OpenDetails();
        return 1;
    }
    return 0;
}

RadarScorePlugin& RadarScorePlugin::Instance() { static RadarScorePlugin instance; return instance; }
IPluginItem* RadarScorePlugin::GetItem(int index) { return index == 0 ? &m_item : nullptr; }
void RadarScorePlugin::DataRequired() { m_item.Refresh(); }
const wchar_t* RadarScorePlugin::GetTooltipInfo()
{
    thread_local std::wstring tooltip;
    tooltip = m_item.Tooltip();
    return tooltip.c_str();
}
const wchar_t* RadarScorePlugin::GetInfo(PluginInfoIndex index)
{
    switch (index)
    {
    case TMI_NAME: return L"GPT 雷达评分";
    case TMI_DESCRIPTION: return L"预先缓存众测雷达的 GPT 模型评分；双击打开完整详情。";
    case TMI_AUTHOR: return L"Local";
    case TMI_VERSION: return L"1.0.0";
    case TMI_URL: return L"https://deng.codexradar.com/";
    default: return L"";
    }
}
extern "C" __declspec(dllexport) ITMPlugin* TMPluginGetInstance() { return &RadarScorePlugin::Instance(); }
