using System.Reflection;
using Seraph.Core.Simulation;
using Seraph.Core.State;

internal static class FlowSamplingChecks
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    internal static void Run(Type serverType, object server, Type sessionType, object previousSession)
    {
        var player = sessionType.GetProperty("Player")!.GetValue(previousSession)!;
        var session = Activator.CreateInstance(sessionType, All, null, [player], null)!;
        void Set(string name, object value) => sessionType.GetProperty(name)!.SetValue(session, value);
        T Get<T>(string name) => (T)sessionType.GetProperty(name)!.GetValue(session)!;
        var begin = serverType.GetMethod("BeginFlowWindow", All)!;
        var update = serverType.GetMethod("UpdateEnergySnapshot", All)!;
        var settle = serverType.GetMethod("SettleCalendarPeriod", All)!;
        var publishActivity = serverType.GetMethod("PublishActivity", All)!;
        Set("Environment", EnvironmentSample.Neutral);
        Set("Kernel", new SimulationKernel(BodyProfile.Default));
        Set("LastLedgerStamina", 1d);
        Set("LastLedgerOxygen", 1d);
        Set("LastLedgerFatigue", 0d);
        Set("HelloAccepted", true);
        Set("PaidEnergyJ", 4184d);
        Set("SettledSeconds", 1d);
        publishActivity.Invoke(server, [session, .1d]);
        Set("PaidEnergyJ", 4184d);
        Set("SettledSeconds", 1d);
        publishActivity.Invoke(server, [session, .1d]);
        Require(Math.Abs(Get<double>("ActivityWindowWallSeconds") - .2) < 1e-8
            && Math.Abs(Get<double>("ActivityWindowPaidEnergyJ") - 8368) < 1e-8,
            "Activity HUD discarded short callback samples instead of accumulating a publication window.");
        for (var i = 0; i < 8; i++)
        {
            Set("PaidEnergyJ", 4184d);
            Set("SettledSeconds", 1d);
            publishActivity.Invoke(server, [session, .1d]);
        }
        // Floating-point rounding can require one more callback to complete the window.
        if (Get<double>("ActivityWindowWallSeconds") > 0)
        {
            Set("PaidEnergyJ", 4184d);
            Set("SettledSeconds", 1d);
            publishActivity.Invoke(server, [session, .1d]);
        }
        Require(Math.Abs(Get<double>("ActivityRateEnergy") - 3600) < 1e-8
            && Get<double>("ActivityWindowWallSeconds") == 0,
            "Activity HUD did not publish the complete averaged energy window.");
        void Callback(bool working)
        {
            begin.Invoke(null, [session, true]);
            Set("DemandPowerW", working ? 450 * 4184d / 3600 : 0d);
            Set("RestFraction", working ? 0d : 1d);
            Require((bool)settle.Invoke(server, [session, new CalendarSleepPeriod(8d / 3600, false, 1)])!, "Flow settlement failed.");
            update.Invoke(null, [session]);
        }
        // Ten callbacks in one publication window: the displayed rate must use
        // all accepted steps, including a rest segment at the packet boundary.
        for (var i = 0; i < 10; i++) Callback(i < 5);
        var flows = Get<PhysiologyFlowLedger>("Flows");
        Require(Math.Abs(flows.Seconds - 80) < 1e-8 && Math.Abs(Get<double>("EnergyActivityKcalPerGameHour") - 225) < 1e-7,
            "Published rate used the last callback instead of all labor/rest segments.");
        Require(Math.Abs(Get<ThermalLedger>("Thermal").Seconds - flows.Seconds) < 1e-8, "Heat and energy use different publication windows.");
        var body = Get<BodyState>("State");
        var paid = Get<double>("EnergyTotalKcalPerGameHour");
        for (var i = 0; i < 100; i++) update.Invoke(null, [session]);
        Require(ReferenceEquals(body, Get<BodyState>("State")) && Get<double>("EnergyTotalKcalPerGameHour") == paid
            && ReferenceEquals(flows, Get<PhysiologyFlowLedger>("Flows")), "Read-only/on-demand rate projection reset flows or charged physiology.");
        Set("ResetFlowWindow", true);
        for (var i = 0; i < 10; i++) Callback(true);
        Require(Math.Abs(Get<PhysiologyFlowLedger>("Flows").Seconds - 80) < 1e-8
            && Math.Abs(Get<double>("EnergyActivityKcalPerGameHour") - 450) < 1e-7,
            "Next publication window retained old rest segments or changed work price.");
        Set("ResetFlowWindow", true);
        for (var i = 0; i < 10; i++) Callback(false);
        Require(Get<double>("EnergyActivityKcalPerGameHour") == 0 && Get<bool>("HasEnergySnapshot"), "Rest retained labor expenditure.");
        begin.Invoke(null, [session, false]); update.Invoke(null, [session]);
        Require(!Get<bool>("HasEnergySnapshot") && Get<PhysiologyFlowLedger>("Flows").Seconds == 0
            && Get<ThermalLedger>("Thermal").Seconds == 0 && Get<double>("EnergyTotalKcalPerGameHour") == 0,
            "Frozen/zero-calendar callback retained old energy or thermal rates.");
        Callback(true);
        Require(Get<bool>("HasEnergySnapshot") && Math.Abs(Get<double>("EnergyActivityKcalPerGameHour") - 450) < 1e-7,
            "Resumed work inherited stale samples.");
        serverType.GetMethod("ClearFlowWindow", All)!.Invoke(null, [session]);
        Require(!Get<bool>("HasEnergySnapshot") && Get<PhysiologyFlowLedger>("Flows").Seconds == 0
            && Get<ThermalLedger>("Thermal").Seconds == 0 && Get<double>("EnergyTotalKcalPerGameHour") == 0
            && Get<bool>("ResetFlowWindow"), "Life/calendar reset retained a previous window.");
        Callback(true);
        Require(Get<bool>("HasEnergySnapshot") && Get<PhysiologyFlowLedger>("Flows").Seconds == 8
            && Math.Abs(Get<double>("EnergyActivityKcalPerGameHour") - 450) < 1e-7,
            "First sample after life/calendar reset inherited old expenditure.");
        Console.WriteLine("PASS complete energy/thermal publication windows: actual 450 kcal labor, half labor/rest 225, subsequent labor/rest, read-only projection, frozen/zero time and resume");
    }

    private static void Require(bool condition, string reason) { if (!condition) throw new Exception(reason); }
}
