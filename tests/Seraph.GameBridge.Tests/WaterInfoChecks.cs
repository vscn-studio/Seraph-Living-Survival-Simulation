using System.Reflection;
using System.Runtime.CompilerServices;
using Seraph.Core.Framework;

internal static class WaterInfoChecks
{
    internal static void Run(Assembly mod)
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var api = Assembly.Load("VintagestoryAPI");
        Type Api(string name) => api.GetType("Vintagestory.API." + name, true)!;
        var bridgeType = mod.GetType("Seraph.Client.SeraphClientBridge", true)!;
        var bridge = RuntimeHelpers.GetUninitializedObject(bridgeType);
        void Field(string name, object value) => bridgeType.GetField(name, all)!.SetValue(bridge, value);
        foreach (var name in new[] { "waterCache", "waterQueryAt", "waterRequestIds", "waterLogAt" })
            Field(name, Activator.CreateInstance(bridgeType.GetField(name, all)!.FieldType)!);
        long now = 100;
        var connected = true;
        var sent = new List<object>();
        var messages = new List<string>();
        var logger = RoutingProxy.Make(Api("Common.ILogger"), (method, args) =>
        {
            messages.Add(method.Name + ":" + args?[0]);
            return null;
        });
        var lang = Api("Config.Lang");
        var languages = (System.Collections.IDictionary)lang.GetProperty("AvailableLanguages")!.GetValue(null)!;
        languages["en"] = RoutingProxy.Make(Api("Config.ITranslationService"), (method, args) => method.Name switch
        {
            "HasTranslation" => true,
            "Get" or "GetUnformatted" or "GetMatching" => args![0],
            _ => throw new Exception("Unexpected translation call: " + method.Name)
        });
        lang.GetMethod("ChangeLanguage")!.Invoke(null, ["en"]);
        var air = Activator.CreateInstance(Api("Common.Block"))!;
        Api("Common.Block").GetField("Code")!.SetValue(air,
            Activator.CreateInstance(Api("Common.AssetLocation"), ["game:air"]));
        var accessor = RoutingProxy.Make(Api("Common.IBlockAccessor"), (method, _) =>
            method.Name == "GetBlock" ? air : throw new Exception("Unexpected accessor: " + method.Name));
        var world = RoutingProxy.Make(Api("Client.IClientWorldAccessor"), (method, _) => method.Name switch
        {
            "get_ElapsedMilliseconds" => now,
            "get_BlockAccessor" => accessor,
            _ => throw new Exception("Unexpected world: " + method.Name)
        });
        var core = RoutingProxy.Make(Api("Client.ICoreClientAPI"), (method, _) => method.Name switch
        {
            "get_World" => world,
            "get_Logger" => logger,
            _ => throw new Exception("Unexpected API: " + method.Name)
        });
        var channel = RoutingProxy.Make(Api("Client.IClientNetworkChannel"), (method, args) =>
        {
            if (method.Name == "get_Connected") return connected;
            if (method.Name == "SendPacket") { sent.Add(args![0]!); return null; }
            throw new Exception("Unexpected channel: " + method.Name);
        });
        Field("api", core); Field("channel", channel);
        var snapshotType = mod.GetType("Seraph.Network.ServerSnapshot", true)!;
        var snapshot = Activator.CreateInstance(snapshotType)!;
        snapshotType.GetProperty("SessionEpoch")!.SetValue(snapshot, "water-test");
        bridgeType.GetProperty("Snapshot")!.SetValue(bridge, snapshot);
        bridgeType.GetProperty("LastReceivedUtc")!.SetValue(bridge, DateTime.UtcNow);
        var pos = Activator.CreateInstance(Api("MathTools.BlockPos"), [10, 20, 30, 0])!;
        var query = bridgeType.GetMethod("WaterInfo", all)!;
        void Query() => query.Invoke(bridge, [pos]);
        void Require(bool condition, string reason) { if (!condition) throw new Exception(reason); }
        Query();
        Require(sent.Count == 1, "First water query never reached the network (timestamp overflow).");
        var request = sent[0];
        Require((int)request.GetType().GetProperty("X")!.GetValue(request)! == 10
            && (string)request.GetType().GetProperty("SessionEpoch")!.GetValue(request)! == "water-test",
            "Water request lost coordinates or session.");
        now = 200; Query(); Require(sent.Count == 1, "Water query throttle failed.");
        now = 600; Query(); Require(sent.Count == 2, "Unanswered water query was not retried within 500ms.");
        Require((long)sent[0].GetType().GetProperty("RequestId")!.GetValue(sent[0])!
            == (long)sent[1].GetType().GetProperty("RequestId")!.GetValue(sent[1])!,
            "Retry invalidated the response still in flight.");
        object Response(string epoch, string failure, string line)
        {
            var response = Activator.CreateInstance(mod.GetType("Seraph.Network.WaterInfoResponse", true)!)!;
            var water = Activator.CreateInstance(mod.GetType("Seraph.Network.WaterHoverSnapshot", true)!)!;
            foreach (var (name, value) in new[] { ("X", 10), ("Y", 20), ("Z", 30) })
                water.GetType().GetProperty(name)!.SetValue(water, value);
            water.GetType().GetProperty("Lines")!.SetValue(water, new[] { line });
            response.GetType().GetProperty("SessionEpoch")!.SetValue(response, epoch);
            response.GetType().GetProperty("FailureReason")!.SetValue(response, failure);
            response.GetType().GetProperty("Water")!.SetValue(response, water);
            return response;
        }
        var receive = bridgeType.GetMethod("ReceiveWaterInfo", all)!;
        receive.Invoke(bridge, [Response("old-world", "", "stale")]);
        receive.Invoke(bridge, [Response("water-test", "rate-limit", "unavailable")]);
        now = 1100; Query(); Require(sent.Count == 3, "Rejected response was cached and blocked recovery.");
        var delayed = Response("water-test", "", "actual water quality");
        delayed.GetType().GetProperty("RequestId")!.SetValue(delayed, request.GetType().GetProperty("RequestId")!.GetValue(request));
        receive.Invoke(bridge, [delayed]);
        Require((string)query.Invoke(bridge, [pos])! == "actual water quality", "Valid water response not displayed.");
        Require(sent.Count == 3, "Cached water answer still sends requests.");
        var neighbor = Activator.CreateInstance(Api("MathTools.BlockPos"), [11, 20, 30, 0])!;
        now = 1200;
        var neighboringInfo = (string)query.Invoke(bridge, [neighbor])!;
        Require(neighboringInfo != "actual water quality", "Neighboring water silently inherited another cell's quality.");
        Require(sent.Count == 3, "Rapid target sweep bypassed the global server-safe throttle.");
        now = 1300; query.Invoke(bridge, [neighbor]);
        Require(sent.Count == 4, "New water target did not query after the global throttle elapsed.");
        receive.Invoke(bridge, [Response("water-test", "", "updated exact cell")]);
        Require((string)query.Invoke(bridge, [neighbor])! != "updated exact cell", "Response was aliased to a nearby pending request.");
        now = 13200; Query(); Require(sent.Count == 5, "Expired cache never refreshes.");
        Require((string)query.Invoke(bridge, [pos])! == "updated exact cell", "Refreshing quality flickers to loading text.");
        connected = false; now = 15000; Query(); Require(sent.Count == 5, "Disconnected water query sent.");
        // A response for an invalidated or superseded request must not repopulate the cell.
        var late = Response("water-test", "", "late packet");
        late.GetType().GetProperty("RequestId")!.SetValue(late, 999999L);
        receive.Invoke(bridge, [late]);
        Require((string)query.Invoke(bridge, [pos])! != "late packet", "Superseded request repopulated water cache.");
        var prefetched = Response("water-test", "", "primary fresh water");
        prefetched.GetType().GetProperty("Nearby")!.SetValue(prefetched, new Seraph.Network.WaterHoverSnapshot[] {
            new() { X = 9, Y = 20, Z = 30, Lines = ["neighbor salt 35 g/L"] },
            new() { X = 50, Y = 20, Z = 30, Lines = ["invalid far sample"] }
        });
        receive.Invoke(bridge, [prefetched]);
        var exactNeighbor = Activator.CreateInstance(Api("MathTools.BlockPos"), [9, 20, 30, 0])!;
        connected = true; now = 15100;
        Require((string)query.Invoke(bridge, [exactNeighbor])! == "neighbor salt 35 g/L" && sent.Count == 5,
            "Prefetch did not retain independent neighboring quality without another request.");
        var farNeighbor = Activator.CreateInstance(Api("MathTools.BlockPos"), [50, 20, 30, 0])!;
        Require((string)query.Invoke(bridge, [farNeighbor])! != "invalid far sample", "Unbounded prefetch was cached.");
        Require(messages.Count > 0, "Water diagnostics missing.");
        Console.WriteLine("PASS water query throttles, delayed replies across retries, independent neighbor prefetch, epoch and cache rejection");
        // Pending classification is still a useful reading, not a reason for 500ms polling.
        var pending = Response("water-test", "", "classification pending");
        var pendingWater = (Seraph.Network.WaterHoverSnapshot)pending.GetType().GetProperty("Water")!.GetValue(pending)!;
        pendingWater.ClassificationPending = true;
        receive.Invoke(bridge, [pending]);
        var beforePending = sent.Count;
        now = 16000; Query();
        Require(sent.Count == beforePending, "Pending classification resumed rapid polling.");
        now = 25200; Query();
        Require(sent.Count == beforePending + 1, "Pending classification never refreshes after its cache expires.");
        var changed = bridgeType.GetMethod("WaterSnapshotChanged", all | BindingFlags.Static)!;
        var repeated = new Seraph.Network.WaterHoverSnapshot { X = 10, Y = 20, Z = 30, ClassificationPending = true, Lines = ["classification pending"] };
        Require(!(bool)changed.Invoke(null, [pendingWater, repeated])!, "Identical packets force a HUD redraw.");
        repeated.ClassificationPending = false;
        Require((bool)changed.Invoke(null, [pendingWater, repeated])!, "Classification completion cannot refresh the HUD.");
        repeated.ClassificationPending = true; repeated.Lines = ["water temperature changed"];
        Require((bool)changed.Invoke(null, [pendingWater, repeated])!, "Changed water information cannot refresh the HUD.");
        Console.WriteLine("PASS pending water cache lifetime and unchanged/changed HUD snapshots");
        var presentation = mod.GetType("Seraph.World.WaterInfoText", true)!.GetMethod("Lines", BindingFlags.Static | BindingFlags.NonPublic)!;
        System.Func<string, object[], string> text = (key, values) => key + ":" + string.Join("|", values);
        string[] Lines(WaterBatch batch, bool volume = true, bool includeWaste = true)
            => (string[])presentation.Invoke(null, [batch, text, .25d, volume, includeWaste])!;
        foreach (var hazard in new[] { 0d, .000015, .012345, .02, .5, 2, 100 })
        {
            var batch = new WaterBatch { VolumeL = 1, Source = "surface", EntericHazardPerL = hazard,
                ChemicalHazardPerL = .0001 };
            var expected = "water-microbial-load:" + hazard.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            Require(Lines(batch).Contains(expected), "Numeric water exposure was rounded to a category or lost precision.");
            Require(!Lines(batch).Any(x => x.StartsWith("water-chemical-risk:") || x.StartsWith("water-pollution:")),
                "Removed chemical/source-record fields remain in water info.");
            Require(Lines(batch).Single(x => x.StartsWith("water-microbial-load:")) ==
                Lines(batch with { Source = "filtered", VolumeL = 5 }).Single(x => x.StartsWith("water-microbial-load:")),
                "Same water quality received different numeric presentations across sources.");
        }
        var natural = new WaterBatch { VolumeL = 1000, Source = "surface", TemperatureC = 3.1 };
        var pond = SurfaceWater.Apply(natural, "pond", new());
        var pondLines = Lines(pond);
        Require(pondLines.Contains("water-turbidity:30 %")
            && !pondLines.Any(x => x.StartsWith("water-turbidity-base:") || x.StartsWith("water-turbidity-added:") || x.StartsWith("water-clarity:")),
            "Compact water info did not show only the total turbidity.");
        var pollutedPond = pond with { Turbidity = .36 };
        var pollutedLines = Lines(pollutedPond);
        Require(pollutedLines.Contains("water-turbidity:36 %")
            && !pollutedLines.Any(x => x.StartsWith("water-turbidity-base:") || x.StartsWith("water-turbidity-added:") || x.StartsWith("water-clarity:")),
            "Polluted water did not show its total turbidity without redundant breakdowns.");
        Require(!Lines(pond).Any(l => l.StartsWith("water-waste-help:")), "Quick water info still contains the long physiology explanation.");
        Require(!Lines(pond).Any(l => l.StartsWith("water-waste-burden:"))
            && !Lines(pond).Any(l => l.StartsWith("water-pathogen-load:"))
            && Lines(pond).Contains("water-microbial-load:0.15"),
            "Compact water info still repeats internal pathogen reservoirs or waste burden.");
        var rounded = new WaterBatch { VolumeL = 9.75, TemperatureC = 13.7, SaltGramsPerL = 0,
            Turbidity = .5298258, Source = "fecal", PathogensPerL = new Dictionary<string, double> { ["shigella"] = .5 },
            EntericHazardPerL = .4154 };
        var roundedLines = Lines(rounded);
        Require(roundedLines.Contains("water-volume:9.75") && roundedLines.Contains("water-temperature:13.7")
            && roundedLines.Contains("water-salt:0")
            && roundedLines.Contains("water-microbial-load:0.92") && roundedLines.Contains("water-quality-risk:water-risk-very-high:")
            && !roundedLines.Any(l => l.StartsWith("water-dose-risk:"))
            && !roundedLines.Any(l => l.StartsWith("water-pathogen-load:") || l.StartsWith("water-waste-burden:")),
            "Water tooltip values were not reduced to readable precision.");
        Require(!Lines(pond, includeWaste: false).Any(x => x.StartsWith("water-waste-burden:")),
            "Compact item water info still exposes the drinking waste burden.");
        Require(!Lines(pond with { PathogensPerL = new Dictionary<string, double> { ["unknown-agent"] = .4 } })
            .Any(l => l.StartsWith("water-pathogen-load:")), "Ordinary water info exposed an internal pathogen reservoir.");
        var region = mod.GetType("Seraph.World.PollutionText", true)!.GetMethod("Region", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var locale in new[] { "en", "zh-cn" })
        {
            var file = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../assets/vsslssmod/lang/" + locale + ".json"));
            var translations = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file))!;
            System.Func<string, object[], string> localized = (key, values) => string.Format(translations[key], values);
            var name = locale == "en" ? "Shigella" : "志贺菌";
            var localizedLines = (string[])presentation.Invoke(null, [pond, localized, .25d, false, true])!;
            Require(!localizedLines.Any(l => l.StartsWith("water-waste-help:"))
                && localizedLines.Any(l => l.Contains("0.15")),
                "Compact water info lost its microbial load in " + locale);
            var compactLines = (string[])presentation.Invoke(null, [pond, localized, .25d, false, false])!;
            Require(!compactLines.Any(l => l.Contains("400") && l.Contains("250") && l.Contains("100"))
                && !localizedLines.Any(l => l.Contains(name)),
                "Item water info still contains internal pathogen detail in " + locale);
            Require(localizedLines.Any(l => l.Contains("0.15"))
                && Pathogens.Definitions.Keys.All(id => translations.ContainsKey("pathogen-name-" + id)),
                "Water translations or microbial load are incomplete in " + locale);
            var details = (IEnumerable<string>)region.Invoke(null, [new RegionalEnvironment {
                SurfaceAgents = new Dictionary<string, double> { ["shigella"] = .5, ["unknown-agent"] = .3 } }, new RegionalSources(), localized])!;
            Require(details.Any(l => l.Contains(name)) && details.Any(l => l.Contains("unknown-agent")),
                "Developer pollution details lost localized names or unknown code fallback.");
        }

        Require(!Lines(natural).Any(x => x.StartsWith("water-volume"))
            && Lines(natural).Contains("water-temperature:3.1"), "Natural water shows sentinel volume or lost temperature.");
        Require(Lines(natural with { VolumeL = 2 }).Contains("water-volume:2")
            && !Lines(natural with { VolumeL = 2 }, false).Any(x => x.StartsWith("water-volume")),
            "Actual container volume missing or explicit volume exclusion ignored.");
        foreach (var source in new[] { "fecal", "polluted", "mixed", "unverified" })
            Require(Lines(natural with { Source = source }).Contains("water-info:water-source-" + source + ":"),
                "Source type lost the contamination/mixing record.");
        Require(Lines(new WaterBatch { VolumeL = 1, Source = "filtered", Unverified = false, ChemicalHazardPerL = 1 })
            .Contains("water-drinking-advice:water-advice-ready:"), "Legacy generic chemical metadata still controls drinking advice.");
        Console.WriteLine("PASS shared water quality: readable precision, source record merged, actual volume only, no generic chemical field");
    }
}
