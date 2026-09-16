#pragma once
#include "PluginInterface.h"
#include <string>

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
    void Refresh();
    const std::wstring& Tooltip() const { return m_tooltip; }
private:
    std::wstring m_value{ L"VPN 状态过期" };
    std::wstring m_tooltip{ L"VPN 管理器尚未写入状态快照。" };
    VpnDisplaySettings m_settings;
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
