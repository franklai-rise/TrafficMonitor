# 2026-09-24 VPN plugin independence repair

The prior compatibility layout placed Radar rendering and background-host handling inside the VPN plugin. After Radar data/format work, the live VPN snapshot was still fresh but the taskbar said "VPN 状态过期". This was an avoidable dependency between unrelated plugins.

The VPN plugin now reads only `VpnManager/vpn-status.json` and exposes two display items for its country and access-method rows. TrafficMonitor pairs those rows in one column; CPU/RAM are paired in another column. The original Radar plugin item is enabled separately to the right. VPN display settings continue to affect only the VPN rows. The plugin does not reference the Radar state directory or launch its host.

Native offline tests cover missing/stale/schema-invalid VPN snapshots, two-line pairing at 16/24/32 pixels, a changed/unparseable Radar fixture that does not affect VPN output, and concurrent reads. A live taskbar capture confirmed the VPN country above `HTTP/SOCKS5 :7890 · Clash`, with Radar rendered by its own plugin. The manager snapshot was fresh (~0.4 s), and the observed mode remained Clash. No VPN switch or proxy change was performed.

Local rollback files are `VpnStatusPlugin-before-20260924-075245.dll` and `TrafficMonitor-config-before-20260924-075245.ini`. Stop TrafficMonitor before restoring them, then start it again. `VpnStatusPlugin.before.cpp`, `VpnStatusPlugin.before.h`, and `PluginTests.before.cpp` preserve the user's prior local edits for reference. Binaries, backups, and screenshots in this task folder remain local and are not committed.
