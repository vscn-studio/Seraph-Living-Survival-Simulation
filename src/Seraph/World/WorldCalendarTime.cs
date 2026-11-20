using System.Runtime.CompilerServices;
using Vintagestory.API.Common;

namespace Seraph.World;

/// <summary>Loaded-world calendar clock. Commands reset its observation without earning skipped time.</summary>
internal static class WorldCalendarTime
{
    private sealed class Clock { internal double Last = double.NaN, Seconds; }
    private static readonly ConditionalWeakTable<IWorldAccessor, Clock> Clocks = new();
    internal static double Seconds(IWorldAccessor world)
    {
        var clock = Clocks.GetValue(world, _ => new());
        // StartServerSide can restore aquifers before the native calendar is created.
        // Keep their clock at its current value until the first real calendar observation.
        if (world.Calendar is not { } calendar) return clock.Seconds;
        var now = calendar.TotalHours;
        var day = calendar.HoursPerDay;
        if (double.IsFinite(now) && double.IsFinite(day) && day > 0)
        {
            if (double.IsFinite(clock.Last) && now >= clock.Last)
                clock.Seconds += (now - clock.Last) / day * 86400;
            clock.Last = now;
        }
        return clock.Seconds;
    }
    internal static double Hours(IWorldAccessor world) => Seconds(world) / 3600;
    internal static void Reset(IWorldAccessor world)
        => Clocks.GetValue(world, _ => new()).Last = world.Calendar?.TotalHours ?? double.NaN;
}
