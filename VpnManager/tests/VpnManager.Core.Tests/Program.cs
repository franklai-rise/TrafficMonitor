using VpnManager.Core;

var tests = new OfflineTests();
await tests.Run();

sealed class OfflineTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "VpnManagerTests-" + Guid.NewGuid());
    public async Task Run()
    {
        Directory.CreateDirectory(_root);
        await TargetStartFailureLeavesEnvironmentUntouched();
        await RefusesWhenCodexRunning();
        await TiziGoRequiresAllRoutes();
        await StopFailureRestoresEnvironment();
        await DirectModeStopsVerifiedVpnAndClearsProxy();
        SnapshotFormattingHandlesModes();
        Ping0GeoResponseSuppliesCountry();
        SnapshotStoreWritesCamelCaseAtomically();
        Console.WriteLine("Offline switch and snapshot tests passed (8/8).");
    }
    private VpnPaths Paths(string name)
    {
        var directory = Path.Combine(_root, name); Directory.CreateDirectory(directory);
        var clash = Path.Combine(directory, "Clash.exe"); var tizi = Path.Combine(directory, "TiziGo.exe"); File.WriteAllText(clash, "test"); File.WriteAllText(tizi, "test");
        return new(clash, tizi, "", Path.Combine(directory, "region.txt"), directory);
    }
    private async Task TargetStartFailureLeavesEnvironmentUntouched()
    {
        var path = Paths(nameof(TargetStartFailureLeavesEnvironmentUntouched)); var fake = new FakeSystem { StartFails = true }; fake.User["HTTP_PROXY"] = "old";
        var service = Make(fake, path); var result = await service.SwitchAsync(VpnMode.Clash, default);
        Require(!result.Success && fake.User["HTTP_PROXY"] == "old", "start failure must not alter environment");
    }
    private async Task RefusesWhenCodexRunning()
    {
        var path = Paths(nameof(RefusesWhenCodexRunning)); var fake = new FakeSystem { Codex = true }; var result = await Make(fake, path).SwitchAsync(VpnMode.Clash, default);
        Require(!result.Success && fake.StartCount == 0, "running Codex must block before network changes");
    }
    private async Task TiziGoRequiresAllRoutes()
    {
        var path = Paths(nameof(TiziGoRequiresAllRoutes)); var fake = new FakeSystem { TiziAdapter = true }; fake.Routes.Add("0.0.0.0/1");
        var result = await Make(fake, path).SwitchAsync(VpnMode.TiziGo, default);
        Require(!result.Success, "partial TUN routes must not be accepted");
    }
    private async Task StopFailureRestoresEnvironment()
    {
        var path = Paths(nameof(StopFailureRestoresEnvironment)); var fake = new FakeSystem { ClashPort = true, TiziAdapter = true, CloseResult = false }; fake.Routes.UnionWith(["0.0.0.0/1", "128.0.0.0/1", "::/1", "8000::/1"]); fake.User["HTTP_PROXY"] = "old";
        var result = await Make(fake, path).SwitchAsync(VpnMode.Clash, default);
        Require(!result.Success && result.RestoreAttempted && fake.User["HTTP_PROXY"] == "old", "failed graceful stop must restore snapshot");
    }
    private async Task DirectModeStopsVerifiedVpnAndClearsProxy()
    {
        var path = Paths(nameof(DirectModeStopsVerifiedVpnAndClearsProxy)); var fake = new FakeSystem { ClashPort = true }; fake.User["HTTP_PROXY"] = fake.User["HTTPS_PROXY"] = fake.User["ALL_PROXY"] = "http://127.0.0.1:7890";
        var result = await Make(fake, path).SwitchAsync(VpnMode.Direct, default);
        Require(result.Success && !fake.ClashPort && fake.User["HTTP_PROXY"] is null && fake.User["HTTPS_PROXY"] is null && fake.User["ALL_PROXY"] is null, "direct mode must gracefully stop the verified VPN and clear proxy variables");
    }
    private void SnapshotFormattingHandlesModes()
    {
        var path = Paths(nameof(SnapshotFormattingHandlesModes)); File.WriteAllText(path.TiziGoRegionFile, "jp"); var fake = new FakeSystem { TiziAdapter = true }; fake.Routes.UnionWith(["0.0.0.0/1", "128.0.0.0/1", "::/1", "8000::/1"]);
        var snapshot = new StatusCollector(fake, path).ToSnapshot(new StatusCollector(fake, path).Collect());
        Require(snapshot.DisplayText == "日本\nTUN · TiziGo", "TiziGo taskbar text must use two lines");
    }
    private void SnapshotStoreWritesCamelCaseAtomically()
    {
        var path = Paths(nameof(SnapshotStoreWritesCamelCaseAtomically)); var store = new SnapshotStore(path.StateDirectory);
        store.Write(new StatusSnapshot(1, "日本 · TUN · TiziGo", "tip", "TiziGo", "TUN", "TiziGo", "日本", "test", null, DateTimeOffset.Now, true, null));
        var raw = File.ReadAllText(store.Path); Require(raw.Contains("\"displayText\"") && raw.Contains("日本"), "plugin snapshot must preserve UTF-8 Chinese text"); Require(store.Read()?.DisplayText.Contains("TiziGo") == true, "snapshot must remain readable");
    }
    private void Ping0GeoResponseSuppliesCountry()
    {
        var geo = Ping0GeoParser.Parse("45.150.165.158\n美国 华盛顿州 西雅圖 — 斯巴达\nAS201106\nSpartan Host Ltd\n");
        Require(geo?.Ip == "45.150.165.158" && geo.Country == "美国" && geo.Location == "美国 华盛顿州 西雅圖", "ping0 geo response must provide country, state and city without the provider name");
    }
    private static SwitchService Make(FakeSystem fake, VpnPaths path) { var collector = new StatusCollector(fake, path); return new SwitchService(fake, path, collector, new SnapshotStore(path.StateDirectory)); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

sealed class FakeSystem : ISystemGateway
{
    public bool ClashPort; public bool TiziAdapter; public bool Codex; public bool StartFails; public bool CloseResult = true; public int StartCount; public readonly HashSet<string> Routes = new(); public readonly Dictionary<string, string?> User = new(StringComparer.OrdinalIgnoreCase);
    public bool IsProcessRunningAtPath(string path) => path.EndsWith("Clash.exe") ? ClashPort : TiziAdapter;
    public bool IsPortListening(int port) => port == VpnPaths.ClashPort && ClashPort;
    public bool IsAdapterUp(string adapterName) => TiziAdapter;
    public IReadOnlyCollection<string> GetRoutesForAdapter(string adapterName, bool forceRefresh = false) => Routes;
    public bool IsCodexRunning() => Codex;
    public void Start(string executablePath) { StartCount++; if (StartFails) return; if (executablePath.EndsWith("Clash.exe")) ClashPort = true; else { TiziAdapter = true; Routes.UnionWith(["0.0.0.0/1", "128.0.0.0/1", "::/1", "8000::/1"]); } }
    public bool RequestCloseAtPath(string executablePath) { if (!CloseResult) return false; if (executablePath.EndsWith("Clash.exe")) ClashPort = false; else { TiziAdapter = false; Routes.Clear(); } return true; }
    public Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken token) => Task.FromResult(condition());
    public string? GetUserEnvironment(string name) => User.TryGetValue(name, out var value) ? value : null;
    public string? GetMachineEnvironment(string name) => null;
    public void SetUserEnvironment(string name, string? value) => User[name] = value;
    public void BroadcastEnvironmentChanged() { }
}
