#pragma once
#include "PluginInterface.h"
#include <chrono>
#include <mutex>
#include <string>

class RadarScoreItem final : public IPluginItem
{
public:
    const wchar_t* GetItemName() const override;
    const wchar_t* GetItemId() const override;
    const wchar_t* GetItemLableText() const override;
    const wchar_t* GetItemValueText() const override;
    const wchar_t* GetItemValueSampleText() const override;
    bool IsCustomDraw() const override { return true; }
    int GetItemWidth() const override { return 200; }
    int GetItemWidthEx(void* hDC) const override;
    void DrawItem(void* hDC, int x, int y, int w, int h, bool dark_mode) override;
    int OnMouseEvent(MouseEventType type, int x, int y, void* hWnd, int flag) override;
    void Refresh(bool force = false);
    std::wstring Tooltip() const;

private:
    mutable std::mutex m_mutex;
    std::wstring m_value{ L"GPT 雷达同步中" };
    std::wstring m_tooltip{ L"后台正在读取众测雷达公开数据。" };
    std::chrono::steady_clock::time_point m_last_read{};
    std::chrono::steady_clock::time_point m_last_launch_attempt{};

    void ReadSnapshot();
    void EnsureBackgroundHost();
    bool OpenDetails();
};

class RadarScorePlugin final : public ITMPlugin
{
public:
    static RadarScorePlugin& Instance();
    IPluginItem* GetItem(int index) override;
    void DataRequired() override;
    const wchar_t* GetInfo(PluginInfoIndex index) override;
    const wchar_t* GetTooltipInfo() override;

private:
    RadarScoreItem m_item;
};

extern "C" __declspec(dllexport) ITMPlugin* TMPluginGetInstance();
