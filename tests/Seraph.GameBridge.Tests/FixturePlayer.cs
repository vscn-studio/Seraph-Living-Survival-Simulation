using System.Runtime.CompilerServices;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.Server;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

// IPlayer has an internal member that DispatchProxy cannot implement.
internal sealed class FixturePlayer : ServerPlayer, IServerPlayer
{
    private FixturePlayer() : base(null!, null!) { }
    public new EntityPlayer Entity { get; private set; } = null!;
    public new IPlayerInventoryManager InventoryManager { get; private set; } = null!;
    public new string PlayerUID => "fixture-player";
    public new string PlayerName => "Fixture";
    public new string LanguageCode => "zh-cn";
    public new IWorldPlayerData WorldData { get; set; } = null!;
    public new BlockSelection CurrentBlockSelection { get; set; } = null!;
    private Dictionary<string, byte[]> Moddata => Data.GetOrCreateValue(this);
    private static readonly ConditionalWeakTable<FixturePlayer, Dictionary<string, byte[]>> Data = new();
    public bool FailSave { get; set; }
    public bool Administrator { get; set; }
    public new bool HasPrivilege(string privilege) => Administrator;
    public new byte[] GetModdata(string key) => Moddata.GetValueOrDefault(key)!;
    public new void SetModdata(string key, byte[] data) {
        if (FailSave) throw new IOException("Injected checkpoint failure");
        Moddata[key] = data;
    }
    private static readonly ConditionalWeakTable<FixturePlayer, List<string>> Messages = new();
    public List<string> SentMessages => Messages.GetOrCreateValue(this);
    public new void SendMessage(int groupId, string message, EnumChatType chatType, string data = null!) => SentMessages.Add(message);
    private static readonly ConditionalWeakTable<FixturePlayer, List<(string Code, string Message)>> Errors = new();
    public List<(string Code, string Message)> IngameErrors => Errors.GetOrCreateValue(this);
    public new void SendIngameError(string code, string message = null!, params object[] langparams) => IngameErrors.Add((code, message));

    internal static FixturePlayer Make(EntityPlayer entity, IPlayerInventoryManager inventory)
    {
        var player = (FixturePlayer)RuntimeHelpers.GetUninitializedObject(typeof(FixturePlayer));
        player.Entity = entity;
        player.InventoryManager = inventory;
        return player;
    }
}

internal sealed class FixtureClientPlayer : ClientPlayer, IClientPlayer
{
    private FixtureClientPlayer() : base(null!) { }
    public new EntityPlayer Entity { get; private set; } = null!;
    public new IPlayerInventoryManager InventoryManager { get; private set; } = null!;
    internal static FixtureClientPlayer Make(EntityPlayer entity,IPlayerInventoryManager? inventory=null)
    {
        var player = (FixtureClientPlayer)RuntimeHelpers.GetUninitializedObject(typeof(FixtureClientPlayer));
        player.Entity = entity;
        player.InventoryManager = inventory!;
        return player;
    }
}
