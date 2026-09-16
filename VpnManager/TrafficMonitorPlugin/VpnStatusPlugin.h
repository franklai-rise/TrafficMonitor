#pragma once
#include "PluginInterface.h"
#include <string>

class VpnStatusItem final : public IPluginItem
{
public:
    const wchar_t* GetItemName() const override;
    const wchar_t* GetItemId() const override;
    const wchar_t* GetItemLableText() const override;
    const wchar_t* GetItemValueText() const override;
    const wchar_t* GetItemValueSampleText() const override;
    int OnMouseEvent(MouseEventType type, int x, int y, void* hWnd, int flag) override;
    void Refresh();
    const std::wstring& Tooltip() const { return m_tooltip; }
private:
    std::wstring m_value{ L"VPN 状态过期" };
    std::wstring m_tooltip{ L"VPN 管理器尚未写入状态快照。" };
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
