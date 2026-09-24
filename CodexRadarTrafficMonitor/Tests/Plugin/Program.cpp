#include "../../Plugin/RadarScorePlugin.h"
#include <windows.h>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <stdexcept>

void Check(bool value, const char* message) { if (!value) throw std::runtime_error(message); }

int main()
{
    wchar_t temp[MAX_PATH]{};
    GetTempPathW(MAX_PATH, temp);
    const auto root = std::filesystem::path(temp) / (L"CodexRadarPluginTests-" + std::to_wstring(GetCurrentProcessId()));
    std::filesystem::create_directories(root / L"AppData/Local/CodexRadarTrafficMonitor");
    SetEnvironmentVariableW(L"USERPROFILE", root.c_str());
    const auto file = root / L"AppData/Local/CodexRadarTrafficMonitor/status.ini";
    auto cleanup = [&] { std::error_code error; std::filesystem::remove_all(root, error); };
    try
    {
        RadarScoreItem item;
        item.Refresh(true);
        Check(std::wstring(item.GetItemValueText()) == L"GPT 雷达同步中", "missing cache should show sync state");
        Check(item.OnMouseEvent(IPluginItem::MT_LCLICKED, 0, 0, nullptr, 0) == 1, "single click should be consumed without an action");
        Check(item.OnMouseEvent(IPluginItem::MT_DBCLICKED, 0, 0, nullptr, 0) == 1, "double click should be consumed after opening or attempting the detail host");
        Check(std::wstring(item.GetItemId()) == L"codex-radar-score-v1", "plugin id must remain stable");
        Check(item.IsCustomDraw() && item.GetItemWidthEx(nullptr) == 200,
            "Radar must reserve its own fixed taskbar column independently of the VPN item.");

        std::wstring ini = L"\xFEFF[status]\r\nvalue=! Astra 107.3 | Sol 93.8 | Luna 54.5\r\ncompact=! 综 A91.3(medium) · S92.3(high) · L84.7(high)\r\ntooltip=众测雷达 · DeepSWE | GPT-6 Astra：107.3 IQ · 样本 700 / 980 | 数据已过期\r\nstale=1\r\n";
        std::ofstream output(file, std::ios::binary);
        output.write(reinterpret_cast<const char*>(ini.data()), static_cast<std::streamsize>(ini.size() * sizeof(wchar_t)));
        output.close();
        item.Refresh(true);
        Check(std::wstring(item.GetItemValueText()) == L"! 综 A91.3(medium) · S92.3(high) · L84.7(high)", "plugin should prefer compact weighted scores with reasoning efforts");
        Check(item.Tooltip().find(L"样本 700 / 980") != std::wstring::npos, "plugin tooltip should expose the score sample count");
        Check(item.Tooltip().find(L"已过期") != std::wstring::npos, "plugin tooltip should explain stale status");

        RadarScorePlugin::Instance().DataRequired();
        Check(std::wstring(RadarScorePlugin::Instance().GetTooltipInfo()).find(L"GPT-6 Astra") != std::wstring::npos,
            "TrafficMonitor plugin tooltip should return cached detail text");
        Check(RadarScorePlugin::Instance().GetItem(0) != nullptr && RadarScorePlugin::Instance().GetItem(1) == nullptr,
            "plugin should expose one display item");
        cleanup();
        std::cout << "PASS plugin ID, cached taskbar text, tooltip, single-click no-op, and double-click dispatch.\n";
        return 0;
    }
    catch (...)
    {
        cleanup();
        throw;
    }
}
