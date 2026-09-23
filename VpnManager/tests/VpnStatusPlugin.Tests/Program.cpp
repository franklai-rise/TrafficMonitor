#include "../../TrafficMonitorPlugin/VpnStatusPlugin.cpp"
#include <thread>
#include <iostream>
#include <stdexcept>
void Check(bool test) { if (!test) throw std::runtime_error("plugin assertion failed"); }
int main(int argc, char** argv)
{
    if (argc == 3 && std::string(argv[1]) == "--probe-dll") {
        const auto library = LoadLibraryA(argv[2]);
        if (!library) { std::cout << "loadError=" << GetLastError() << "\n"; return 2; }
        const auto entry = reinterpret_cast<ITMPlugin*(*)()>(GetProcAddress(library, "TMPluginGetInstance"));
        if (!entry) { std::cout << "entryError=" << GetLastError() << "\n"; return 3; }
        auto* plugin = entry();
        auto* item = plugin ? plugin->GetItem(0) : nullptr;
        std::cout << "api=" << (plugin ? plugin->GetAPIVersion() : -1) << "\n"
                  << "item=" << (item ? 1 : 0) << "\n"
                  << "custom=" << (item ? item->IsCustomDraw() : false) << "\n"
                  << "exclusive=" << (item ? item->IsDoubleLineExclusive() : -1) << "\n";
        if (item) std::wcout << L"id=" << item->GetItemId() << L"\n";
        return item ? 0 : 4;
    }
    if (argc == 3 && std::string(argv[1]) == "--read-installed") {
        VpnStatusItem live; live.Refresh(true);
        const auto raw=ReadFile(StatePath()); const auto wide=live.Tooltip();
        int len=WideCharToMultiByte(CP_UTF8,0,wide.c_str(),-1,nullptr,0,nullptr,nullptr);
        std::string tip(len,0);WideCharToMultiByte(CP_UTF8,0,wide.c_str(),-1,tip.data(),len,nullptr,nullptr);
        std::ofstream out(argv[2]); out << "recent=" << RecentObservation(JsonString(raw,"observedAt")) << "\n";
        out << "schema=" << std::regex_search(raw,std::regex("\"schemaVersion\"\\s*:\\s*1\\s*[,}]")) << "\n";
        try { out << "display=" << JsonString(raw,"displayText") << "\n"; out << "tipLength=" << JsonString(raw,"tooltip").size() << "\n"; } catch (const std::exception& ex) {out << "parseError=" << ex.what() << "\n"; }
        out << "fresh=" << JsonBool(raw,"isFresh") << "\n" << "tooltip=" << tip << "\n" << raw;
        out << "\nradar="; auto radar = RadarDisplayValue();
        int radarLen=WideCharToMultiByte(CP_UTF8,0,radar.c_str(),-1,nullptr,0,nullptr,nullptr);
        std::string radarUtf8(radarLen,0);WideCharToMultiByte(CP_UTF8,0,radar.c_str(),-1,radarUtf8.data(),radarLen,nullptr,nullptr);
        out << radarUtf8 << "\n";
        auto radarPath=RadarStatePath();
        out << "radarExists=" << std::filesystem::exists(radarPath) << "\n";
        std::wstring profile=UserLocalAppData();
        auto narrow=[](const std::wstring& s){int n=WideCharToMultiByte(CP_UTF8,0,s.c_str(),-1,nullptr,0,nullptr,nullptr);std::string v(n,0);WideCharToMultiByte(CP_UTF8,0,s.c_str(),-1,v.data(),n,nullptr,nullptr);return v;};
        out << "localAppData=" << narrow(profile) << "\nradarPath=" << narrow(radarPath) << "\n";
        return 0;
    }
    wchar_t temporary[MAX_PATH]{}; GetTempPathW(MAX_PATH, temporary);
    const auto root = std::filesystem::path(temporary) / (L"VpnPluginTests-" + std::to_wstring(GetCurrentProcessId()));
    std::filesystem::create_directories(root / L"AppData/Local/VpnManager");
    std::filesystem::create_directories(root / L"AppData/Local/CodexRadarTrafficMonitor");
    SetEnvironmentVariableW(L"USERPROFILE", root.c_str()); // This test process only.
    auto cleanup = [&] { std::filesystem::remove_all(root); };
    try {
        Check(JsonString(R"({"v":"node \"quoted\" \\ path\n\u65e5\u672c \ud83c\uddef\ud83c\uddf5"})", "v") == "node \"quoted\" \\ path\n日本 🇯🇵");
        Check(!RecentObservation("2000-01-01T00:00:00Z"));
        {
            std::ofstream radar(root / L"AppData/Local/CodexRadarTrafficMonitor/status.ini");
            radar << "[status]\nvalue=Astra 107.2 | Sol 90.0 | Luna 57.3\nstale=0\nupdatedUnixSeconds=" << std::time(nullptr) << "\n";
        }
        Check(RadarDisplayValue() == L"A107.2 · S90.0 · L57.3");
        VpnStatusItem item; item.Refresh(true); Check(std::wstring(item.GetItemValueText()) == L"VPN 状态过期");
        SYSTEMTIME time{}; GetSystemTime(&time); char stamp[64]{};
        sprintf_s(stamp,"%04d-%02d-%02dT%02d:%02d:%02dZ",time.wYear,time.wMonth,time.wDay,time.wHour,time.wMinute,time.wSecond);
        auto fixture = [&](std::string text, int version = 1, std::string date = "") {
            const auto json = std::string("{\"schemaVersion\":") + std::to_string(version) + ",\"displayText\":\"" + text +
                "\",\"tooltip\":\"提示含有\\\"引号\\\"\",\"isFresh\":true,\"observedAt\":\"" + (date.empty() ? stamp : date) + "\"}";
            std::ofstream out(std::filesystem::path(StatePath()), std::ios::binary); out << json;
        };
        for (const auto& text : { "美国 加利福尼亚州 洛杉矶\\nHTTP/SOCKS5 :7890 · Clash", "日本\\nTUN · TiziGo", "VPN 状态冲突", "中国 广东 深圳\\n203.0.113.8 · 普通直连", "未知\\nTUN · TiziGo" }) {
            fixture(text); item.Refresh(true); Check(std::wstring(item.GetItemValueText()) != L"VPN 状态过期");
            Check(item.Tooltip().find(L"\"引号\"") != std::wstring::npos);
        }
        fixture("test", 2); item.Refresh(true); Check(std::wstring(item.GetItemValueText()) == L"VPN 状态过期");
        fixture("test", 1, "2000-01-01T00:00:00Z"); item.Refresh(true); Check(std::wstring(item.GetItemValueText()) == L"VPN 状态过期");
        fixture("test"); std::filesystem::last_write_time(StatePath(), std::filesystem::file_time_type::clock::now()-std::chrono::seconds(20));
        item.Refresh(true); Check(std::wstring(item.GetItemValueText()) == L"VPN 状态过期");
        fixture("美国 加利福尼亚州 洛杉矶\\nHTTP/SOCKS5 :7890 · Clash"); item.Refresh(true);
        {
            std::string longTip;
            for (int n=0;n<600;n++) longTip += "中文详情\\n";
            std::ofstream out(std::filesystem::path(StatePath()),std::ios::binary);
            out << "{\"schemaVersion\":1,\"displayText\":\"美国 加州 洛杉矶\\nHTTP/SOCKS5 :7890 · Clash\",\"tooltip\":\""
                << longTip << "\",\"isFresh\":true,\"observedAt\":\"" << stamp << "\"}"; out.close();
            item.Refresh(true); Check(item.Tooltip().size()==3000);
            Check(std::wstring(item.GetItemValueText())!=L"VPN 状态过期");
        }
        const auto dc = CreateCompatibleDC(nullptr);
        BITMAPINFO info{}; info.bmiHeader.biSize=sizeof(BITMAPINFOHEADER); info.bmiHeader.biWidth=800; info.bmiHeader.biHeight=-160;
        info.bmiHeader.biPlanes=1; info.bmiHeader.biBitCount=32; info.bmiHeader.biCompression=BI_RGB;
        void* pixels{}; const auto bitmap=CreateDIBSection(dc,&info,DIB_RGB_COLORS,&pixels,nullptr,0);
        const auto previous=SelectObject(dc,bitmap);
        Check(item.GetItemWidthEx(dc)>=600);
        for (int height : {32,48,64}) {
            memset(pixels,255,800*160*4); item.DrawItem(dc,10,10,600,height,false); GdiFlush();
            const auto data=static_cast<unsigned int*>(pixels); bool top=false,bottom=false,radar=false;
            for (int y=0;y<160;y++) for(int x=0;x<800;x++) {
                if ((data[y*800+x]&0xFFFFFF)!=0xFFFFFF) {
                    Check(x>=10&&x<610&&y>=10&&y<10+height);
                    if(y<10+height/2) top=true; else bottom=true;
                    if(x>350) radar=true;
                }
            }
            Check(top&&bottom&&radar);
        }
        std::thread reader([&] { for(int n=0;n<50;n++) { (void)item.GetItemValueText(); (void)item.Tooltip(); } });
        for(int n=0;n<50;n++) item.Refresh(true); reader.join();
        SelectObject(dc,previous); DeleteObject(bitmap); DeleteDC(dc);
        std::cout<<"PASS JSON escapes and emoji; missing/stale/schema/modes; two-line clipping at 32/48/64 pixels; concurrent reads\n";
        cleanup(); return 0;
    } catch (...) { cleanup(); throw; }
}
