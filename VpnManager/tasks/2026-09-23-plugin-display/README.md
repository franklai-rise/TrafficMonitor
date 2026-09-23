# 2026-09-23 TrafficMonitor VPN/Radar display repair

The running TrafficMonitor.exe 1.8.6.0 loads both plugin DLLs, but it does not have the newer plugin-exclusive two-line layout found in the fork source. Two separately enabled plugin items shared a column and overlapped. The installed Radar host and status file were also visible only through the Codex MSIX filesystem view, so the independent TrafficMonitor process could not read them.

The VPN plugin now draws its two-line status on the left and a compact Radar score on the right. TrafficMonitor is configured for non-horizontal arrangement and only `vpn-manager-status-v1` is enabled as a display item; CPU and RAM form the first two-row column. RadarScorePlugin.dll remains loaded and continues to start the Radar background host. The host was copied from the project artifacts into the real user LocalAppData directory using a separate scheduled maintenance process.

Offline native plugin tests passed, including text rendering at 32/48/64 px. An actual taskbar PrintWindow capture verified the requested layout and live Radar scores. The manager's snapshot remained fresh and the observed VPN mode remained Clash. No VPN switch or system proxy change was performed.

Local recovery files: `config-before-single-20260923-234119.ini` preserves the prior TrafficMonitor config; `VpnStatusPlugin-before-20260923-234728.dll` preserves the prior VPN plugin DLL. Stop TrafficMonitor before restoring either file, then start it again. Diagnostic screenshots and downloaded build dependencies in this folder are local-only and should not be committed.
