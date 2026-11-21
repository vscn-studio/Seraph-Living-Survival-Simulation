using Seraph.Core.Framework;
using Seraph.Core.State;
using Seraph.Network;
using Seraph.World;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Seraph.Server;

internal sealed partial class ServerCoordinator
{
    internal Seraph.Application.SeraphServerConfig InteractionConfig => settings.Config;
    private sealed record PendingDefecation(double Elapsed, double Duration, double Amount, int Items, bool Full,
        double X, double Y, double Z, long Generation, int HurtCounter, BlockPos Target, int OriginalBlock, int OriginalQuantity);
    private sealed record PendingLatrineDefecation(double Elapsed, double Duration, double Amount, int Items, bool Full,
        double X, double Y, double Z, long Generation, int HurtCounter, BlockPos Target, BlockEntityLatrine Latrine, LatrineWasteStorage Storage);
    private sealed record PendingGroundDrink(double Elapsed, double X, double Y, double Z, long Generation,
        int HurtCounter, BlockPos Target, int FluidId, ItemSlot Slot, ItemStack? Held);
    private sealed record PendingWellBucketRaise(double Elapsed, double Duration, double X, double Y, double Z,
        long Generation, int HurtCounter, BlockPos Head);
    private sealed record WaterSource(WaterBatch Batch, string LiquidCode, System.Func<double, bool> Take, Action Restore);
    private static readonly string[] BodyRegionIds = ["head-neck", "torso", "left-arm", "right-arm", "left-leg", "right-leg"];
    private bool? naturalFertilizerLoaded;
    private ManureGroundStorage? manureStorage;
    private bool manureStorageChecked;

    private ManureGroundStorage? PreferredManureStorage()
    {
        if (!manureStorageChecked)
        {
            manureStorageChecked = true;
            manureStorage = HasNaturalFertilizer()
                ? ManureGroundStorage.Resolve(api.World) : ManureGroundStorage.ResolveFallback(api.World);
            if (manureStorage is null)
                api.Logger.Warning("[vsslssmod] Preferred manure item/ground storage API is unavailable; defecation is disabled.");
        }
        return manureStorage;
    }

    private bool HasNaturalFertilizer()
    {
        if (naturalFertilizerLoaded is { } cached) return cached;
        naturalFertilizerLoaded = api.ModLoader.Mods.Any(m =>
            string.Equals(m.Info.ModID, "naturalfertilizer", StringComparison.OrdinalIgnoreCase));
        return naturalFertilizerLoaded.Value;
    }

    private void OnWorldAction(IServerPlayer player, WorldActionRequest packet)
    {
        if (disposed || !Current(player, out var session) || !session.HelloAccepted
            || packet.ProtocolVersion != Protocol.Version || packet.SessionEpoch != session.Epoch
            || packet.Sequence <= session.LastWorldActionSequence || packet.Sequence <= 0) return;
        session.LastWorldActionSequence = packet.Sequence;
        // Release and lease renewal are never throttled behind a start request.
        if (packet.Action == "pressure-stop")
        { if (packet.ActionId == session.PressureActionId) { EndPressure(session, "released"); SendSnapshot(session); } return; }
        if (packet.Action == "pressure-hold")
        { if (packet.ActionId == session.PressureActionId && session.Pressure is { } p) session.Pressure = p with { LastHold = ServerSeconds }; return; }
        if (packet.Action == "pressure-start")
        {
            if (session.Pressure is not null) return; // Retransmission must not reset the held action.
            session.PressureActionId = packet.Sequence;
            session.PressureReason = "";
            if (ServerSeconds - session.LastPressureStart < .4)
            { session.PressureReason = "throttled"; player.SendIngameError("vsslssmod-pressure", L(player, "pressure-throttled")); }
            else
            {
                session.LastPressureStart = ServerSeconds;
                var result = BeginPressure(session);
                if (result.Status != EnumCommandStatus.Success)
                {
                    session.PressureReason = "rejected";
                    player.SendIngameError("vsslssmod-pressure", result.StatusMessage);
                }
            }
            SendSnapshot(session); return;
        }
        if (packet.Action == "drink-stop")
        { session.GroundDrink = null; session.ProduceWash = null; session.LastDrinkHoldSeconds = double.NegativeInfinity; SendSnapshot(session); return; }
        if (packet.Action == "drink-hold")
        { if (session.GroundDrink is not null || session.ProduceWash is not null) session.LastDrinkHoldSeconds = ServerSeconds; return; }
        if (packet.Action == "clean-stop")
        { CancelWash(session, notify: true); SendSnapshot(session); return; }
        if (packet.Action == "clean-hold")
        { if (session.Wash is { Bucket: true } wash) session.Wash = wash with { LastHold = ServerSeconds }; return; }
        if (packet.Action == "closed-eye-toggle")
        {
            try { player.SendMessage(GlobalConstants.GeneralChatGroup, ToggleClosedEyeRest(session).StatusMessage, EnumChatType.Notification); }
            catch (Exception ex) { SetFault(session, SessionFaultKind.UnexpectedSessionError, ex); }
            SendSnapshot(session);
            return;
        }
        if (ServerSeconds - session.LastWorldActionSeconds < 0.4) return;
        session.LastWorldActionSeconds = ServerSeconds;
        try
        {
            var result = packet.Action switch
            {
                "clean-start" => BeginWash(session, bucket: true),
                "drink-start" => BeginHeldDrink(player, session), "defecate" => BeginDefecation(player),
                _ => TextCommandResult.Error(L(player, "action-unavailable"))
            };
            player.SendMessage(GlobalConstants.GeneralChatGroup, result.StatusMessage, EnumChatType.Notification);
            SendSnapshot(session);
        }
        catch (Exception ex)
        {
            SetFault(session, SessionFaultKind.UnexpectedSessionError, ex);
            player.SendMessage(GlobalConstants.GeneralChatGroup, L(player, "action-save-failed"), EnumChatType.Notification);
        }
    }

    private TextCommandResult BeginDefecation(IServerPlayer? player)
    {
        if (!ReadyForAction(player, out var s) || !settings.Config.EnableDefecation)
            return TextCommandResult.Error(L(player, "action-unavailable"));
        if (s.Defecation is not null) { s.Defecation = null; ClearGroundSquat(s); return TextCommandResult.Success(L(player, "world-cancelled")); }
        if (s.Interactions.BowelUnits < BowelSettlement.ManualMinimumG) return TextCommandResult.Error(L(player, "world-bowel-empty"));
        var e = s.Player.Entity;
        if (e.MountedOn is not null || e.Swimming || e.ServerControls.TriesToMove)
            return TextCommandResult.Error(L(player, "must-rest"));
        if (!DungTarget(s, out var target, out var stage)) return TextCommandResult.Error(L(player, "world-dung-ground"));
        var block = api.World.BlockAccessor.GetBlock(target, BlockLayersAccess.Solid);
        var capacity = PreferredManureStorage()!.Capacity;
        var (portions, amountG) = BowelSettlement.PlanDefecation(s.Interactions.BowelUnits, capacity - stage);
        if (amountG <= 0) return TextCommandResult.Error(L(player, "world-dung-ground"));
        CancelCompetingActions(s);
        s.Defecation = new(0, 2 + portions, amountG, portions, amountG == s.Interactions.BowelUnits, e.Pos.X, e.Pos.Y, e.Pos.Z, s.LifeGeneration,
            e.WatchedAttributes.GetInt("onHurtCounter"), target.Copy(), block.Id, stage);
        UpdateGroundSquat(s);
        return TextCommandResult.Success(L(player, "world-defecation-start", 2 + portions));
    }

    private bool DungTarget(Session s, out BlockPos target, out int quantity)
    {
        target = s.Player.Entity.Pos.AsBlockPos.Copy(); quantity = 0;
        var b = api.World.BlockAccessor;
        if (b.GetChunkAtBlockPos(target) is null || !api.World.Claims.TryAccess(s.Player, target, EnumBlockAccessFlags.BuildOrBreak)
            || b.GetBlock(target, BlockLayersAccess.Fluid).Id != 0) return false;
        var support = b.GetBlock(target.DownCopy());
        if (!support.SideSolid[BlockFacing.UP.Index]) return false;
        return PreferredManureStorage() is { } bridge && bridge.TryRead(api.World, target, out quantity) && quantity <= bridge.Capacity;
    }

    private bool ActionInterrupted(Session s, bool advancing, double x, double y, double z, long generation, int hurtCounter)
    {
        var e = s.Player.Entity;
        return !advancing || !IsSurvivalTestPlayer(s.Player) || generation != s.LifeGeneration
            || Math.Pow(e.Pos.X - x, 2) + Math.Pow(e.Pos.Y - y, 2) + Math.Pow(e.Pos.Z - z, 2) > 0.09
            || e.MountedOn is not null || e.Swimming || e.ServerControls.TriesToMove || !e.Alive
            || e.WatchedAttributes.GetInt("onHurtCounter") != hurtCounter
            || e.WatchedAttributes.GetTreeAttribute("tiredness")?.GetInt("isSleeping") > 0;
    }

    private void TickWorldActions(Session s, bool advancing, double dt)
    {
        // Loaded real seconds only: no time acceleration and no completion after a long stall.
        var step = Math.Clamp(dt, 0, 0.25);
        TickGroundDrink(s, advancing, step);
        TickProduceWash(s, advancing, step);
        try { TickDefecation(s, advancing, step); }
        finally { UpdateGroundSquat(s); }
        TickLatrineDefecation(s, advancing, step);
        TickWellBucket(s, advancing, step);
    }

    private static void UpdateGroundSquat(Session s)
    {
        var entity = s.Player.Entity;
        if (s.Defecation is null || entity is null || !entity.Alive)
        { ClearGroundSquat(s); return; }
        if (ReferenceEquals(s.GroundSquattingEntity, entity)) return;
        ClearGroundSquat(s);
        entity.AnimManager?.StartAnimation(DefecationPose.Create());
        s.GroundSquattingEntity = entity;
    }

    private static void ClearGroundSquat(Session s)
    {
        s.GroundSquattingEntity?.AnimManager?.StopAnimation(DefecationPose.Code);
        s.GroundSquattingEntity = null;
    }

    private static bool IsEmptyWoodBucket(ItemStack? stack, out BlockLiquidContainerBase container)
    {
        container = null!;
        if (stack?.Collectible is not BlockLiquidContainerBase candidate
            || candidate.Code?.Domain != "game"
            || candidate.Code.Path.StartsWith("woodbucket", StringComparison.OrdinalIgnoreCase) == false)
            return false;
        var content = candidate.GetContent(stack);
        if (content is not null && content.StackSize > 0) return false;
        container = candidate;
        return true;
    }

    internal bool TryLowerWellBucket(IServerPlayer player, BlockEntityWellHead well)
    {
        if (!ReadyForAction(player, out var session)) return true;
        if (!well.WellComplete)
        { return true; }
        if (well.BucketInWell)
        { return true; }
        if (sessions.Values.Any(s => s.WellBucketRaise is { Head: var head } && head.Equals(well.Pos)))
        { return true; }
        var slot = player.InventoryManager.ActiveHotbarSlot;
        if (!IsEmptyWoodBucket(slot.Itemstack, out var container))
        { return true; }
        if (!TryGetWellSupply(well.Pos, container.CapacityLitres, out var batch, out _))
        { return true; }
        // Prepare a detached copy first. A rejected extraction must leave the
        // original empty bucket in the player's slot.
        var bucket = slot.Itemstack!.Clone();
        bucket.StackSize = 1;
        var committed = false;
        try
        {
            var waterItem = api.World.GetItem(new AssetLocation("game:waterportion"));
            var props = waterItem is null ? null : BlockLiquidContainerBase.GetContainableProps(new ItemStack(waterItem));
            if (waterItem is null || props is not { Containable: true, ItemsPerLitre: > 0 }) throw new InvalidOperationException("water portion unavailable");
            var count = Math.Max(1, (int)Math.Ceiling(batch.VolumeL * props.ItemsPerLitre));
            var source = new ItemStack(waterItem, count);
            var moved = container.TryPutLiquid(bucket, source, (float)batch.VolumeL);
            if (moved <= 0) throw new InvalidOperationException("bucket fill rejected");
            var actualL = moved / (double)props.ItemsPerLitre;
            if (container.GetContent(bucket) is { } content)
                WaterMetadata.WriteAt(api.World, content, batch with { VolumeL = actualL });
            if (!TryExtractWell(well.Pos, actualL)) throw new InvalidOperationException("well extraction rejected");
            slot.TakeOut(1);
            // SetBucket transfers ownership before any network/UI notifications.
            committed = true;
            well.SetBucket(bucket, inWell: true, raised: false);
            CancelCompetingActions(session);
            slot.MarkDirty();
            SendSnapshot(session);
        }
        catch (Exception ex)
        {
            api.Logger.Error("[vsslssmod] Well bucket transaction at {0}, committed={1}: {2}", well.Pos, committed, ex);
        }
        return true;
    }

    internal bool TryBeginWellBucketRaise(IServerPlayer player, BlockEntityWellHead well)
    {
        if (!ReadyForAction(player, out var session)) return true;
        if (!well.WellComplete)
        { return true; }
        if (!well.BucketInWell || well.BucketRaised)
        { return true; }
        if (session.WellBucketRaise is not null) return true;
        if (sessions.Values.Any(s => !ReferenceEquals(s, session) && s.WellBucketRaise is { Head: var head } && head.Equals(well.Pos)))
        { return true; }
        var fill = well.FillFraction;
        var duration = Math.Clamp(2.0 + well.WellDepth * 0.18 + (1 - fill) * 4.0, 3.0, 24.0);
        var e = player.Entity;
        if (e.MountedOn is not null || e.Swimming || e.ServerControls.TriesToMove)
        { return true; }
        CancelCompetingActions(session);
        session.WellBucketRaise = new(0, duration, e.Pos.X, e.Pos.Y, e.Pos.Z, session.LifeGeneration,
            e.WatchedAttributes.GetInt("onHurtCounter"), well.Pos.Copy());
        well.SetRaiseProgress(0);
        SendSnapshot(session);
        return true;
    }

    internal bool IsWellBucketRaiseActive(IServerPlayer player, BlockEntityWellHead well)
        => Current(player, out var session) && session.WellBucketRaise is { Head: var head } && head.Equals(well.Pos);

    internal void StopWellBucketRaise(IServerPlayer player, BlockEntityWellHead well)
    {
        if (!Current(player, out var session) || session.WellBucketRaise is not { Head: var head } pending || !head.Equals(well.Pos)) return;
        session.WellBucketRaise = null;
        well.ClearRaiseProgress();
        SendSnapshot(session);
    }

    internal bool TryTakeWellBucket(IServerPlayer player, BlockEntityWellHead well)
    {
        if (!ReadyForAction(player, out var session)) return true;
        if (!well.WellComplete) { return true; }
        if (!well.BucketRaised || well.BucketStack is null) { return true; }
        var stack = well.RemoveBucket();
        if (stack is null) return true;
        if (!player.InventoryManager.TryGiveItemstack(stack))
        {
            well.SetBucket(stack, inWell: true, raised: true);
            return true;
        }
        SendSnapshot(session);
        return true;
    }

    private void TickWellBucket(Session session, bool advancing, double step)
    {
        if (session.WellBucketRaise is not { } pending) return;
        var well = api.World.BlockAccessor.GetBlockEntity(pending.Head) as BlockEntityWellHead;
        if (well is null || !well.WellComplete || !well.BucketInWell
            || ActionInterrupted(session, advancing, pending.X, pending.Y, pending.Z, pending.Generation, pending.HurtCounter))
        { session.WellBucketRaise = null; well?.ClearRaiseProgress(); SendSnapshot(session); return; }
        pending = pending with { Elapsed = pending.Elapsed + step };
        session.WellBucketRaise = pending;
        well.SetRaiseProgress(pending.Elapsed / pending.Duration);
        if (pending.Elapsed < pending.Duration) return;
        session.WellBucketRaise = null;
        well.SetRaised(true);
        SendSnapshot(session);
    }

    private void TickDefecation(Session s, bool advancing, double step)
    {
        if (s.Defecation is not { } pending) return;
        if (ActionInterrupted(s, advancing, pending.X, pending.Y, pending.Z, pending.Generation, pending.HurtCounter)
            || !DungTarget(s, out var target, out var quantity) || !target.Equals(pending.Target)
            || quantity != pending.OriginalQuantity
            || api.World.BlockAccessor.GetBlock(target, BlockLayersAccess.Solid).Id != pending.OriginalBlock)
        { s.Defecation = null; Notify(s, "world-cancelled"); SendSnapshot(s); return; }
        pending = pending with { Elapsed = pending.Elapsed + step }; s.Defecation = pending;
        if (pending.Elapsed < pending.Duration) return;
        s.Defecation = null;
        if (s.Interactions.BowelUnits < pending.Amount) { Notify(s, "world-bowel-empty"); SendSnapshot(s); return; }
        var pos = pending.Target;
        var old = s.Interactions;
        var oldFrame = s.Framework;
        var plan = BowelSettlement.PlanDefecation(old.BowelUnits, PreferredManureStorage()!.Capacity - pending.OriginalQuantity);
        if (plan.MassG <= 0) { Notify(s, "world-bowel-empty"); SendSnapshot(s); return; }
        var massG = plan.MassG;
        s.Framework = HandContamination.AfterDefecation(oldFrame with { Dirtiness = 1 }, PathogenMetadata.Shed(oldFrame));
        s.Interactions = old with { BowelUnits = old.AfterDefecation(massG), DefecationCount = checked(old.DefecationCount + 1) };
        if (!Save(s)) { s.Framework = oldFrame; s.Interactions = old; Notify(s, "action-save-failed"); return; }
        // Stage the charge first. Player moddata and chunk saves are NOT a cross-store disk transaction.
        try
        {
            if (plan.Items > 0) PreferredManureStorage()!.Add(api.World, pos, pending.OriginalBlock, pending.OriginalQuantity, plan.Items, PathogenMetadata.Shed(s.Framework), plan.ProductMassG);
            Trace(s, "defecation-removed-g:" + Number(plan.MassG, "0.##") + ";product-g:" + Number(plan.ProductMassG, "0.##"));
            Trace(s, "defecation-tail-discard-g:" + Number(plan.DiscardedG, "0.##"));
            WaterQuality.Invalidate(api.World, pos);
        }
        catch
        {
            s.Framework = oldFrame; s.Interactions = old; Save(s); throw;
        }
        Notify(s, "world-defecation-complete"); Trace(s, "defecate-placed"); SendSnapshot(s);
    }

    internal bool TryBeginLatrineDefecation(IServerPlayer player, BlockEntityLatrine latrine)
    {
        if (!ReadyForAction(player, out var s) || !settings.Config.EnableDefecation)
        { player.SendMessage(GlobalConstants.GeneralChatGroup, L(player, "action-unavailable"), EnumChatType.Notification); return true; }
        if (s.LatrineDefecation is not null) { s.LatrineDefecation = null; Notify(s, "world-cancelled"); return true; }
        if (s.Interactions.BowelUnits < BowelSettlement.ManualMinimumG) { Notify(s, "world-bowel-empty"); return true; }
        var e = player.Entity;
        if (e.MountedOn is not null || e.Swimming || e.ServerControls.TriesToMove)
        { Notify(s, "must-rest"); return true; }
        var storage = LatrineWasteStorage.Read(api.World, latrine.Pos);
        if (storage is null) { Notify(s, "latrine-needs-barrel"); return true; }
        if (!api.World.Claims.TryAccess(player, latrine.Pos, EnumBlockAccessFlags.Use)
            || !api.World.Claims.TryAccess(player, storage.Position, EnumBlockAccessFlags.Use)) return true;
        var (portions, amountG) = BowelSettlement.PlanDefecation(s.Interactions.BowelUnits, storage.AvailablePortions);
        if (amountG <= 0) { Notify(s, "latrine-full"); return true; }
        // Release the ground pose before the seat takes ownership of the same animation.
        if (!e.TryMount(latrine)) { Notify(s, "must-rest"); return true; }
        CancelCompetingActions(s);
        s.LatrineDefecation = new(0, 2 + portions, amountG, portions, amountG == s.Interactions.BowelUnits, e.Pos.X, e.Pos.Y, e.Pos.Z,
            s.LifeGeneration, e.WatchedAttributes.GetInt("onHurtCounter"), latrine.Pos.Copy(), latrine, storage);
        Notify(s, "latrine-use-start");
        SendSnapshot(s);
        return true;
    }

    private void TickLatrineDefecation(Session s, bool advancing, double step)
    {
        if (s.LatrineDefecation is not { } pending) return;
        var latrine = api.World.BlockAccessor.GetBlockEntity(pending.Target) as BlockEntityLatrine;
        if (!ReferenceEquals(latrine, pending.Latrine) || !pending.Storage.Matches(api.World)
            || !api.World.Claims.TryAccess(s.Player, pending.Target, EnumBlockAccessFlags.Use)
            || !api.World.Claims.TryAccess(s.Player, pending.Storage.Position, EnumBlockAccessFlags.Use)
            || !advancing || !s.Player.Entity.Alive || !IsSurvivalTestPlayer(s.Player)
            || s.Player.Entity.MountedOn != latrine || pending.Generation != s.LifeGeneration
            || s.Player.Entity.WatchedAttributes.GetInt("onHurtCounter") != pending.HurtCounter)
        {
            s.LatrineDefecation = null;
            if (s.Player.Entity.MountedOn == pending.Latrine) s.Player.Entity.TryUnmount();
            Notify(s, "world-cancelled"); SendSnapshot(s); return;
        }
        pending = pending with { Elapsed = pending.Elapsed + step };
        s.LatrineDefecation = pending;
        if (pending.Elapsed < pending.Duration) return;
        if (s.Interactions.BowelUnits < pending.Amount)
        { s.LatrineDefecation = null; Notify(s, "world-bowel-empty"); SendSnapshot(s); return; }
        var old = s.Interactions;
        var oldFrame = s.Framework;
        var plan = BowelSettlement.PlanDefecation(old.BowelUnits, pending.Storage.AvailablePortions);
        if (plan.MassG <= 0) { s.LatrineDefecation = null; Notify(s, "world-bowel-empty"); SendSnapshot(s); return; }
        var massG = plan.MassG;
        s.Framework = HandContamination.AfterDefecation(oldFrame with {
            Dirtiness = Math.Min(1, oldFrame.Dirtiness + .01 * Sanitation.GainMultiplier(oldFrame))
        }, PathogenMetadata.Shed(oldFrame));
        s.Interactions = old with { BowelUnits = old.AfterDefecation(massG), DefecationCount = checked(old.DefecationCount + 1) };
        if (!Save(s)) { s.Framework = oldFrame; s.Interactions = old; s.LatrineDefecation = null; Notify(s, "action-save-failed"); return; }
        try
        {
            if (plan.Items > 0) pending.Storage.Add(api.World, plan.Items, PathogenMetadata.Shed(s.Framework), plan.ProductMassG);
            Trace(s, "defecation-removed-g:" + Number(plan.MassG, "0.##") + ";product-g:" + Number(plan.ProductMassG, "0.##"));
            Trace(s, "defecation-tail-discard-g:" + Number(plan.DiscardedG, "0.##"));
        }
        catch { s.Framework = oldFrame; s.Interactions = old; Save(s); s.LatrineDefecation = null; throw; }
        s.LatrineDefecation = null;
        Notify(s, "latrine-use-complete"); Trace(s, "defecate-latrine"); SendSnapshot(s);
    }

    private void Notify(Session s, string key) => s.Player.SendMessage(GlobalConstants.GeneralChatGroup, L(s.Player, key), EnumChatType.Notification);

    private BlockPos? WaterTarget(Session s, double range = WaterInteractionTarget.InteractionRange, bool allowMounted = false, bool readOnly = false)
    {
        var e = s.Player.Entity;
        if (!allowMounted && e.MountedOn is not null) return null;
        var pos = WaterInteractionTarget.Find(api.World, e, range);
        return pos is not null && (readOnly || api.World.Claims.TryAccess(s.Player, pos, EnumBlockAccessFlags.Use)) ? pos : null;
    }

    private WaterBatch? ReadLiquid(ItemStack? liquid, double temperature) =>
        WaterQuality.ReadLiquidAt(api.World, liquid, temperature, settings.Config);

    private WaterSource? SourceAt(Session s, bool heldFirst, BlockPos? selected = null)
    {
        var slot = s.Player.InventoryManager.ActiveHotbarSlot;
        var ambient = s.Environment?.AirTemperatureC ?? 15;
        if (heldFirst && slot.Itemstack?.Collectible is BlockLiquidContainerBase held)
        {
            if (slot.StackSize != 1) return null;
            var original = slot.Itemstack.Clone();
            var liquid = held.GetContent(slot.Itemstack);
            if (ReadLiquid(liquid, ambient) is not { VolumeL: > 0 } batch) return null;
            return new(batch, liquid!.Collectible.Code.ToString(), amount =>
            {
                var units = BlockLiquidContainerBase.GetContainableProps(liquid)!.ItemsPerLitre;
                var count = (int)Math.Round(amount * units);
                if (count <= 0 || Math.Abs(count / (double)units - amount) > 0.00001) return false;
                var taken = held.TryTakeContent(slot.Itemstack, count);
                slot.MarkDirty();
                return taken is not null && taken.StackSize == count;
            }, () => { slot.Itemstack = original; slot.MarkDirty(); });
        }
        var pos = selected ?? WaterTarget(s);
        if (pos is null) return null;
        var accessor = api.World.BlockAccessor;
        if (accessor.GetBlock(pos, BlockLayersAccess.Solid) is BlockLiquidContainerBase container)
        {
            var liquid = container.GetContent(pos);
            if (ReadLiquid(liquid, ambient) is not { VolumeL: > 0 } batch) return null;
            var old = liquid!.Clone();
            return new(batch, liquid.Collectible.Code.ToString(), amount =>
            {
                var units = BlockLiquidContainerBase.GetContainableProps(liquid)!.ItemsPerLitre;
                var count = (int)Math.Round(amount * units);
                if (count <= 0 || count > liquid.StackSize) return false;
                var left = liquid.Clone(); left.StackSize -= count;
                NativeWaterTransfer.SetPlacedContent(api.World, container, pos, left.StackSize > 0 ? left : null);
                accessor.GetBlockEntity(pos)?.MarkDirty(true);
                return true;
            }, () => { NativeWaterTransfer.SetPlacedContent(api.World, container, pos, old); });
        }
        var batchSource = WaterQuality.WorldSource(api.World, pos, settings.Config);
        // Source is a quality label (pollution may replace "groundwater" with "fecal").
        var aquiferSource = WaterInteractionTarget.TryGetWaterAt(accessor, pos, out var sourceFluid, out _)
            && sourceFluid.Code?.Path?.Contains("saltwater", StringComparison.OrdinalIgnoreCase) != true
            && sourceFluid.LiquidCode != "saltwater" && pos.Y < api.World.SeaLevel - 4;
        Action rollback = () => { };
        return batchSource is null ? null : new(batchSource,
            batchSource.SaltGramsPerL > 5 ? "game:saltwaterportion" : "game:waterportion",
            amount => !aquiferSource || groundwater?.TryExtract(pos, amount, out rollback) == true, () => rollback());
    }

    private TextCommandResult BeginHeldDrink(IServerPlayer player, Session session)
    {
        if (session.GroundDrink is not null || session.ProduceWash is not null) return TextCommandResult.Success("");
        session.LastDrinkHoldSeconds = ServerSeconds;
        return NativeCropPollution.Washable(player.InventoryManager.ActiveHotbarSlot.Itemstack, api.World)
            ? BeginProduceWash(session) : Drink(player);
    }

    private TextCommandResult Drink(IServerPlayer? player)
    {
        if (!ReadyForAction(player, out var s) || !settings.Config.EnableGroundWaterDrinking || ServerSeconds - s.LastDrinkHoldSeconds > .75)
            return TextCommandResult.Error(L(player, "action-unavailable"));
        if (!s.Player.Entity.ServerControls.ShiftKey
            || s.Player.InventoryManager.ActiveHotbarSlot.Itemstack?.Collectible is BlockLiquidContainerBase)
            return TextCommandResult.Error(L(player, "medical-water-gesture"));
        if (ServerSeconds - s.LastDrinkSeconds < settings.Config.DrinkCooldownRealSeconds)
            return TextCommandResult.Error(L(player, "drink-cooldown"));
        if (s.GroundDrink is not null) return TextCommandResult.Success("");
        var e = s.Player.Entity;
        if (e.MountedOn is not null || e.Swimming || e.ServerControls.TriesToMove)
            return TextCommandResult.Error(L(player, "must-rest"));
        var pos = WaterTarget(s);
        var batch = pos is null ? null : WaterInteractionTarget.IsSurfaceWater(api.World, pos)
            ? WaterQuality.WorldSource(api.World, pos, settings.Config)
            : WaterInteractionTarget.IsWaterContainer(api.World, pos) ? SourceAt(s, false, pos)?.Batch : null;
        if (batch is null) return TextCommandResult.Error(L(player, "world-ground-water-required"));
        if (batch.SaltGramsPerL > 5 || !DrinkingTemperature.IsDrinkable(batch.TemperatureC))
            return TextCommandResult.Error(L(player, "world-water-unsuitable"));
        if (s.State.GutWaterL + settings.Config.DrinkServingL > s.Profile.MaximumGutWaterL
            || s.Framework.Nutrition.ContentsMassG(s.State.GutWaterL) + settings.Config.DrinkServingL * 1000 > DietaryMetabolism.StomachCapacityG)
            return TextCommandResult.Error(L(player, "gut-full"));
        var slot = player!.InventoryManager.ActiveHotbarSlot;
        CancelCompetingActions(s);
        var isContainer = WaterInteractionTarget.IsWaterContainer(api.World, pos!);
        var targetId = api.World.BlockAccessor.GetBlock(pos!, isContainer ? BlockLayersAccess.Solid : BlockLayersAccess.Fluid).Id;
        s.GroundDrink = new(0, e.Pos.X, e.Pos.Y, e.Pos.Z, s.LifeGeneration, e.WatchedAttributes.GetInt("onHurtCounter"),
            pos!.Copy(), targetId, slot, slot.Itemstack);
        SendSnapshot(s);
        return TextCommandResult.Success(L(player, "world-drink-start"));
    }

    private void TickGroundDrink(Session s, bool advancing, double step)
    {
        if (s.GroundDrink is not { } pending) return;
        if (ServerSeconds - s.LastDrinkHoldSeconds > .75
            || !s.Player.Entity.ServerControls.ShiftKey
            || ActionInterrupted(s, advancing, pending.X, pending.Y, pending.Z, pending.Generation, pending.HurtCounter)
            || WaterTarget(s)?.Equals(pending.Target) != true
            || !ValidDrinkTarget(pending.Target, pending.FluidId)
            || !ReferenceEquals(s.Player.InventoryManager.ActiveHotbarSlot, pending.Slot)
            || !ReferenceEquals(pending.Slot.Itemstack, pending.Held))
        { s.GroundDrink = null; Notify(s, "world-cancelled"); SendSnapshot(s); return; }
        pending = pending with { Elapsed = pending.Elapsed + step }; s.GroundDrink = pending;
        if (pending.Elapsed < 3) return;
        s.GroundDrink = null;
        var source = SourceAt(s, false, pending.Target);
        var result = source is null ? TextCommandResult.Error(L(s.Player, "world-ground-water-required"))
            : CommitWater(s, source, settings.Config.DrinkServingL);
        s.Player.SendMessage(GlobalConstants.GeneralChatGroup, result.StatusMessage, EnumChatType.Notification);
        SendSnapshot(s);
    }

    private bool ValidDrinkTarget(BlockPos target, int blockId)
    {
        var isSurface = WaterInteractionTarget.IsSurfaceWater(api.World, target);
        var valid = isSurface ? api.World.BlockAccessor.GetBlock(target, BlockLayersAccess.Fluid).Id == blockId
            : WaterInteractionTarget.IsWaterContainer(api.World, target)
                && api.World.BlockAccessor.GetBlock(target, BlockLayersAccess.Solid).Id == blockId;
        return valid;
    }

    internal void DrinkFromContainer(IServerPlayer player, ItemSlot slot, BlockLiquidContainerBase container)
    {
        if (!ReadyForAction(player, out var s) || !ReferenceEquals(player.InventoryManager.ActiveHotbarSlot, slot)
            || s.GroundDrink is not null || s.Defecation is not null || slot.StackSize != 1
            || !container.CanDrinkFrom || player.Entity.MountedOn is not null) return;
        var source = SourceAt(s, true);
        if (source is null) return;
        try
        {
            var result = CommitWater(s, source, container.DrinkPortionSize);
            player.SendMessage(GlobalConstants.GeneralChatGroup, result.StatusMessage, EnumChatType.Notification);
            SendSnapshot(s);
        }
        catch (Exception ex) { SetFault(s, SessionFaultKind.UnexpectedSessionError, ex); Notify(s, "action-save-failed"); }
    }

    private TextCommandResult CommitWater(Session s, WaterSource source, double servingL)
    {
        var player = s.Player;
        if (ServerSeconds - s.LastDrinkSeconds < settings.Config.DrinkCooldownRealSeconds)
            return TextCommandResult.Error(L(player, "drink-cooldown"));
        if (source.Batch.SaltGramsPerL > 5 || !DrinkingTemperature.IsDrinkable(source.Batch.TemperatureC))
            return TextCommandResult.Error(L(player, "world-water-unsuitable"));
        var amount = Math.Floor(Math.Min(servingL, source.Batch.VolumeL) * 100 + 1e-6) / 100;
        if (amount < 0.01 || s.State.GutWaterL + amount > s.Profile.MaximumGutWaterL
            || s.Framework.Nutrition.ContentsMassG(s.State.GutWaterL) + amount * 1000 > DietaryMetabolism.StomachCapacityG)
            return TextCommandResult.Error(L(player, "gut-full"));
        var oldBody = s.State; var oldFrame = s.Framework; var oldInteractions = s.Interactions;
        try
        {
            if (!source.Take(amount)) { source.Restore(); return TextCommandResult.Error(L(player, "world-water-required")); }
            if (!ApplyIntake(s, new(s.Epoch + ":water:" + (s.Framework.EventSequence + 1), "verified-water-volume", amount, 0, 0, true, false)))
                throw new InvalidOperationException("Water intake rejected.");
            var salts = (double[])s.Framework.Nutrition.Pending.Clone();
            salts[4] += source.Batch.SaltGramsPerL / 2.54 * amount * .8;
            s.Framework = s.Framework with { Nutrition = s.Framework.Nutrition with { Pending = salts,
                PendingWaterWasteMl = s.Framework.Nutrition.PendingWaterWasteMl + source.Batch.PotentialBloodWasteMl(amount) } };
            if (settings.Config.EnableClinicalEffects)
                s.Framework = ExposeIngestion(s.Framework, source.Batch.PathogensPerL, source.Batch.EntericHazardPerL, amount);
            s.Interactions = s.Interactions with { LastWater = source.Batch with { VolumeL = amount } };
            if (!Save(s)) throw new InvalidOperationException("Water checkpoint rejected.");
        }
        catch
        { source.Restore(); s.State = oldBody; s.Framework = oldFrame; s.Interactions = oldInteractions; throw; }
        s.LastDrinkSeconds = ServerSeconds; Trace(s, "drink-verified-volume");
        return TextCommandResult.Success(L(player, "world-drink-result", Number(amount, "0.00"), L(player, "water-source-" + source.Batch.Source)));
    }

    private WorldActionProgress? ActionProgress(Session s) => medical.Get(s.Player.PlayerUID) is { } wash
        ? new() { Action = WashAction(s.Wash), ElapsedSeconds = ServerSeconds - wash.StartedRealSeconds, DurationSeconds = wash.DurationSeconds }
        : s.WellBucketRaise is { } raise
        ? new() { Action = "well-bucket-raise", ElapsedSeconds = raise.Elapsed, DurationSeconds = raise.Duration }
        : s.ProduceWash is { } produce
        ? new() { Action = "produce-wash", ElapsedSeconds = produce.Elapsed, DurationSeconds = 3 }
        : s.GroundDrink is { } drink
        ? new() { Action = "drink", ElapsedSeconds = drink.Elapsed, DurationSeconds = 3 }
        : s.LatrineDefecation is { } latrine
        ? new() { Action = "defecate", ElapsedSeconds = latrine.Elapsed, DurationSeconds = latrine.Duration }
        : s.Defecation is { } bowel
            ? new() { Action = "defecate", ElapsedSeconds = bowel.Elapsed, DurationSeconds = bowel.Duration } : null;

    private readonly SlowCallbackProbe waterInfoProbe = new("server.water-info.request", 50);
    private void OnWaterInfo(IServerPlayer player, WaterInfoRequest request)
    {
        using var timing = waterInfoProbe?.Measure(api);
        var requested = new BlockPos(request.X, request.Y, request.Z, request.Dimension);
        if (disposed || !Current(player, out var s)) return;
        if (!s.HelloAccepted || request.SessionEpoch != s.Epoch)
        {
            LogWaterInfo(s, requested, "handshake-or-epoch-mismatch");
            return;
        }
        // Never leave the client waiting because a duplicate request was dropped.
        // This guard also protects against rapidly sweeping many water cells.
        if (ServerSeconds - s.LastWaterQuerySeconds < .15)
        {
            SendUnavailableWaterInfo(player, s, requested, "rate-limit", request.RequestId);
            return;
        }
        s.LastWaterQuerySeconds = ServerSeconds;
        try
        {
            if (requested.dimension != player.Entity.Pos.Dimension || !NearPlayerWaterInfoCell(s, requested))
            {
                SendUnavailableWaterInfo(player, s, requested, "dimension-or-distance", request.RequestId);
                return;
            }

            // The client sends the exact visible fluid cell. Never substitute a
            // nearby pool, container or water behind the selected object.
            var pos = WaterInteractionTarget.TryGetWaterAt(api.World.BlockAccessor, requested, out _, out var visible)
                ? visible : null;
            var batch = pos is null ? null : WaterQuality.WorldSource(api.World, pos, settings.Config);
            if (pos is null || batch is null)
            {
                SendUnavailableWaterInfo(player, s, requested, pos is null ? "no-fluid-cell" : "no-water-batch", request.RequestId);
                return;
            }
            LogWaterInfo(s, requested, $"ok resolved={pos}, source={batch.Source}");
            var nearby = new List<WaterHoverSnapshot>(4);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (var face in BlockFacing.HORIZONTALS)
            {
                // Bound speculative work. Each entry has its own authoritative water
                // and pollution sample; never copy clean water across a boundary.
                if (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 1) break;
                var neighbor = pos.AddCopy(face);
                if (!NearPlayerWaterInfoCell(s, neighbor) || api.World.BlockAccessor.GetChunkAtBlockPos(neighbor) is null) continue;
                if (WaterQuality.WorldSource(api.World, neighbor, settings.Config) is { } water)
                    nearby.Add(WaterSnapshot(s, neighbor, water));
            }
            channel.SendPacket(new WaterInfoResponse { SessionEpoch = s.Epoch, RequestId = request.RequestId,
                Revision = waterClassifier?.Revision ?? 0, Water = WaterSnapshot(s, pos, batch), Nearby = nearby.ToArray() }, player);
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[vsslssmod] Water inspection failed for {0}: {1}", requested, ex.Message);
            SendUnavailableWaterInfo(player, s, requested, "exception", request.RequestId);
        }
    }

    private WaterHoverSnapshot WaterSnapshot(Session s, BlockPos pos, WaterBatch batch)
    {
        var sample = Pollution?.Report(pos);
        return new()
        {
            X = pos.X, Y = pos.Y, Z = pos.Z, Dimension = pos.dimension,
            // Surface water is not inherently pending. Only an absent pollution sample
            // should make the client retry classification; once classified, its snapshot
            // can use the normal cache lifetime without repeatedly rebuilding the HUD.
            ClassificationPending = sample is null,
            Lines = WaterLines(s, batch).Concat(sample is { } report
                ? [L(s.Player, "pollution-sources", PollutionText.Sources(report))]
                : new[] { L(s.Player, "pollution-pending") }).ToArray()
        };
    }

    private void LogWaterInfo(Session session, BlockPos requested, string result)
    {
        if (ServerSeconds - session.LastWaterInfoLogSeconds < 5) return;
        session.LastWaterInfoLogSeconds = ServerSeconds;
        api.Logger.Notification("[vsslssmod] [water-info] server cell={0}, epoch={1}, result={2}", requested, session.Epoch, result);
    }

    private void SendUnavailableWaterInfo(IServerPlayer player, Session session, BlockPos requested, string reason, long requestId = 0)
    {
        LogWaterInfo(session, requested, reason);
        channel.SendPacket(new WaterInfoResponse { SessionEpoch = session.Epoch, FailureReason = reason, RequestId = requestId,
            Water = new() { X = requested.X, Y = requested.Y, Z = requested.Z, Dimension = requested.dimension,
                Lines = [L(player, "water-info", ""), L(player, "water-hover-unavailable")] } }, player);
    }

    private static bool NearWaterInfoCell(BlockPos authoritative, BlockPos requested) =>
        authoritative.dimension == requested.dimension
        && Math.Abs(authoritative.X - requested.X) <= 1
        && Math.Abs(authoritative.Y - requested.Y) <= 1
        && Math.Abs(authoritative.Z - requested.Z) <= 1;

    private static bool NearPlayerWaterInfoCell(Session session, BlockPos requested)
    {
        if (requested.dimension != session.Player.Entity.Pos.Dimension) return false;
        var eye = session.Player.Entity.Pos.XYZ.AddCopy(session.Player.Entity.LocalEyePos);
        var dx = requested.X + .5 - eye.X;
        var dy = requested.Y + .5 - eye.Y;
        var dz = requested.Z + .5 - eye.Z;
        return dx * dx + dy * dy + dz * dz <= 49;
    }

    internal void AppendGroundwaterProbe(IServerPlayer player, BlockPos pos)
    {
        if (disposed || groundwater is null || !Current(player, out var session) || !session.HelloAccepted) return;
        try
        {
            var lines = GroundwaterLines(session, groundwater.Query(pos));
            player.SendMessage(GlobalConstants.InfoLogChatGroup, string.Join("\n", lines), EnumChatType.Notification, null);
            // Prospecting remains chat-only. Excavation is the only event that
            // can expose the virtual saturated zone visually.
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[vsslssmod] Groundwater prospecting result was suppressed: {0}", ex.Message);
        }
    }

    private string[] GroundwaterLines(Session s, GroundwaterState state) =>
    [
        L(s.Player, "groundwater-probe-title"),
        L(s.Player, "groundwater-material", L(s.Player, "groundwater-material-" + state.Material.ToString().ToLowerInvariant())),
        L(s.Player, "groundwater-type", L(s.Player, "groundwater-type-" + state.Type.ToString().ToLowerInvariant())),
        L(s.Player, "groundwater-storage", Number(state.StorageL, "0"), Number(state.CapacityL, "0")),
        L(s.Player, "groundwater-level", Number(state.WaterTableDepthM, "0.0")),
        L(s.Player, "groundwater-flow", Number(state.FlowLPerDay / 24, "0.0")),
        L(s.Player, "groundwater-salinity", Number(state.SalinityGPerL, "0.00")),
        L(s.Player, "groundwater-estimate")
    ];

    private void OnWaterBlockPlaced(IServerPlayer player, int oldId, BlockSelection selection, ItemStack stack)
    {
        WaterQuality.Invalidate(api.World, selection.Position);
        // Multi-block fixtures may emit one event for each occupied cell.
        // Their placement is one hand action, so charge the state-owning cell only.
        if (api.World.BlockAccessor.GetBlock(selection.Position).Code is { Domain: "vsslssmod", Path: "bathtub-part" or "copperbathtub-part" or "copperbathtub-end" }) return;
        RecordPlacedBlock(player, stack);
    }
    private void OnWaterBlockBroken(IServerPlayer player, int oldId, BlockSelection selection)
    {
        RecordNativeBlockBreak(player, oldId, selection);
        WaterQuality.Invalidate(api.World, selection.Position);
        if (player is not null) NotifyGroundwaterExposure(player, selection.Position);
    }

    private string[] WaterLines(Session s) => WaterLines(s, SourceAt(s, true)?.Batch);

    private string[] WaterLines(Session s, WaterBatch? w)
    {
        return WaterInfoText.Lines(w, (key, args) => L(s.Player, key, args), settings.Config.DrinkServingL);
    }

    private string[] InteractionLines(Session s) => [L(s.Player, "world-bowel-load", Number(s.Interactions.BowelUnits, "0")),
        L(s.Player, "world-bowel-basis")];

    private BodyRegionSnapshot[] BodyRegions(Session s) => BodyRegionIds.Select((region, index) =>
    {
        var wounds = s.Framework.Wounds.Where(w => Anatomy.Parent(w.Region) == region).ToArray();
        var injury = wounds.Length == 0 ? 0 : wounds.Max(BodyFunctionResolver.Remaining);
        return new BodyRegionSnapshot { Region = region, Integrity = s.Framework.Vitals.EffectiveFor(index), Maximum = s.Framework.Vitals.EffectiveMaximumFor(index),
            Wound = s.Framework.Vitals.Integrity[index] < VitalState.MaximumFor(index) - .001 || injury > .001, Pain = BodyFunctionResolver.Pain(wounds) > .1 };
    }).ToArray();

    private HudGauge[] HudGauges(Session s)
    {
        var health = s.Player.Entity.WatchedAttributes.GetTreeAttribute("health");
        var oxygen = s.Player.Entity.WatchedAttributes.GetTreeAttribute("oxygen");
        HudGauge Native(string key, float? v, float? max) => new() { Key = key, Value = v ?? 0, Maximum = max ?? 1,
            Available = v is { } n && max is > 0 && float.IsFinite(n) && float.IsFinite(max.Value),
            Text = v is null || max is null ? "" : Number(v, "0.0") + "/" + Number(max, "0.0") };
        return [settings.Config.Mode == "survival" ? new() { Key = "health", Value = s.Deceased ? 0 : s.Framework.Vitals.Health, Maximum = s.Framework.Vitals.EffectiveMaximum, Available = true } : Native("health", health?.TryGetFloat("currenthealth"), health?.TryGetFloat("maxhealth")),
            new() { Key = "hydration", Value = ReserveDisplay.HydrationG(s.State, s.Profile), Maximum = ReserveDisplay.HydrationCapacityG, Available = true, Text = Number(ReserveDisplay.HydrationG(s.State, s.Profile), "0") + "/8000 g" },
            new() { Key = "stamina", Value = s.Framework.Stamina, Available = true, Text = Number(s.Framework.Stamina, "P0") },
            new() { Key = "fatigue", Value = s.Framework.Fatigue, Available = true, Text = Number(s.Framework.Fatigue, "P0") },
            Native("oxygen", oxygen?.TryGetFloat("currentoxygen"), oxygen?.TryGetFloat("maxoxygen")),
            new() { Key = "bowel", Value = s.Interactions.BowelUnits, Maximum = BowelSettlement.CapacityG, Available = true, Text = Number(s.Interactions.BowelUnits, "0") + "/5000 g" }];
    }
}
