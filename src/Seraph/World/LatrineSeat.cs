using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace Seraph.World;

public sealed partial class BlockEntityLatrine : IMountableSeat, IMountable
{
    private EntityAgent? occupant;
    private readonly EntityControls seatControls = new() { Sneak = true };
    private readonly AnimationMetaData squat = DefecationPose.Create();
    private Vec3d? departure;
    private int departureDimension;
    public SeatConfig Config { get; set; } = null!;
    public string SeatId { get; set; } = "vsslssmod-latrine";
    public long PassengerEntityIdForInit { get; set; }
    public bool DoTeleportOnUnmount { get; set; } = true;
    public Entity Entity => null!;
    public Entity Passenger => occupant!;
    public IMountable MountSupplier => this;
    public bool CanControl => false;
    public EnumMountAngleMode AngleMode => EnumMountAngleMode.Unaffected;
    public AnimationMetaData SuggestedAnimation => squat;
    public bool SkipIdleAnimation => true;
    public float FpHandPitchFollow => 1;
    public Vec3f LocalEyePos => new(0, .9f, 0);
    public EntityPos SeatPosition => Position;
    public Matrixf RenderTransform => null!;
    public EntityControls Controls => seatControls;
    public IMountableSeat[] Seats => [this];
    public EntityPos Position => new EntityPos().SetPos(Pos).Add(.5, .125, .5);
    public double StepPitch => 0;
    public Entity Controller => occupant!;
    public Entity OnEntity => null!;
    public EntityControls ControllingControls => seatControls;
    public bool AnyMounted() => occupant is not null;
    public bool CanMount(EntityAgent entityAgent) => occupant is null || occupant == entityAgent;
    public bool CanUnmount(EntityAgent entityAgent) => true;

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);
        seatControls.OnAction = OnSeatAction;
    }

    private void OnSeatAction(EnumEntityAction action, bool on, ref EnumHandling handled)
    {
        if (action != EnumEntityAction.Sneak || !on) return;
        occupant?.TryUnmount();
        seatControls.StopAllMovement();
    }

    public void DidMount(EntityAgent entityAgent)
    {
        departure = entityAgent.Pos.XYZ.Clone();
        departureDimension = entityAgent.Pos.Dimension;
        occupant = entityAgent;
        seatControls.Sneak = true;
        entityAgent.AnimManager?.StartAnimation(squat);
    }

    public void DidUnmount(EntityAgent entityAgent)
    {
        occupant = null;
        entityAgent.AnimManager?.StopAnimation(squat.Code);
        seatControls.Sneak = true;
        if (Api.Side == EnumAppSide.Server && departure is { } target
            && !Api.World.CollisionTester.IsColliding(Api.World.BlockAccessor, entityAgent.CollisionBox, target, false))
            entityAgent.TeleportTo(new EntityPos { X = target.X, Y = target.Y, Z = target.Z, Dimension = departureDimension });
        departure = null;
    }

    public void MountableToTreeAttributes(TreeAttribute tree)
    {
        tree.SetString("className", "vsslssmod-latrine");
        tree.SetInt("x", Pos.X); tree.SetInt("y", Pos.InternalY); tree.SetInt("z", Pos.Z);
    }

    public override void OnBlockRemoved()
    {
        occupant?.TryUnmount();
        base.OnBlockRemoved();
    }

    public override void OnBlockUnloaded()
    {
        occupant?.TryUnmount();
        base.OnBlockUnloaded();
    }
}
