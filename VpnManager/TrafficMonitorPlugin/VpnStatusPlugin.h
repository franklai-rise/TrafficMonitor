#pragma once
#include "PluginInterface.h"
#include <chrono>
#include <string>
#include <mutex>

struct VpnDisplaySettings
{
    std::wstring font_name{ L"Microsoft YaHei UI" };
    int font_size{ 13 };
    unsigned long color{ 0x00CF771E };
    IPluginDrawer::Alignment alignment{ IPluginDrawer::LEFT };
};

class VpnStatusItem final : public IPluginItem
{
public:
    const wchar_t* GetItemName() const override;
    const wchar_t* GetItemId() const override;
    const wchar_t* GetItemLableText() const override;
    const wchar_t* GetItemValueText() const override;
    const wchar_t* GetItemValueSampleText() const override;
    bool IsCustomDraw() const override { return true; }
    int GetItemWidth() const override { return 260; }
    int GetItemWidthEx(void* hDC) const override;
    void DrawItem(void* hDC, int x, int y, int w, int h, bool dark_mode) override;
    int IsDoubleLineExclusive() const override { return 1; }
    int OnMouseEvent(MouseEventType type, int x, int y, void* hWnd, int flag) override;
    void Refresh(bool force = false);
    std::wstring Tooltip() const { std::lock_guard<std::recursive_mutex> lock(m_mutex); return m_tooltip; }
private:
    mutable std::recursive_mutex m_mutex;
    std::wstring m_value{ L"VPN 状态过期" };
    std::wstring m_tooltip{ L"VPN 管理器尚未写入状态快照。" };
    VpnDisplaySettings m_settings;
    std::chrono::steady_clock::time_point m_last_snapshot_read{};
    void LoadDisplaySettings();
};

class VpnStatusPlugin final : public ITMPlugin
{
public:
    static VpnStatusPlugin& Instance();
    IPluginItem* GetItem(int index) override;
    void DataRequired() override;
    const wchar_t* GetInfo(PluginInfoIndex index) override;
    const wchar_t* GetTooltipInfo() override;
private:
    VpnStatusItem m_item;
};

extern "C" __declspec(dllexport) ITMPlugin* TMPluginGetInstance();
