using System.Reflection;
using System.Text.Json;
using Seraph.Core.Framework;
using Seraph.World;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

internal static class GroundwaterStartupChecks
{
    internal static void Run()
    {
        T Proxy<T>(System.Func<MethodInfo,object?[]?,object?> route)=>(T)RoutingProxy.Make(typeof(T),route);
        var state=GroundwaterHydrologyModel.Derive(42,0,0,110,.5,15,AquiferMaterialClass.Gravel);
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new {Format="seraph.release.groundwater",Schema=1,Entries=new[] {new {X=0,Z=0,Dimension=0,State=state}}});
        var calendarReady=false;var hours=240d;var warnings=0;var reads=0;
        var calendar=Proxy<IGameCalendar>((m,_)=>m.Name switch {"get_TotalHours"=>(object)hours,"get_HoursPerDay"=>24f,_=>throw new Exception(m.Name)});
        var accessor=Proxy<IBlockAccessor>((m,_)=>m.Name=="GetChunkAtBlockPos"?null:throw new Exception(m.Name));
        var world=Proxy<IServerWorldAccessor>((m,_)=>m.Name switch {
            "get_Seed"=>42,"get_Calendar"=>calendarReady?calendar:null,"get_BlockAccessor"=>accessor,_=>throw new Exception(m.Name)
        });
        var save=Proxy<ISaveGame>((m,_)=>m.Name=="GetData"?Read():throw new Exception(m.Name));
        byte[] Read() {reads++;return bytes;}
        var manager=Proxy<IWorldManagerAPI>((m,_)=>m.Name=="get_SaveGame"?save:throw new Exception(m.Name));
        var events=Proxy<IServerEventAPI>((m,_)=>m.Name.StartsWith("add_") || m.Name.StartsWith("remove_")?null:throw new Exception(m.Name));
        var logger=Proxy<ILogger>((m,_)=> {if(m.Name=="Warning")warnings++;return null;});
        var api=Proxy<ICoreServerAPI>((m,_)=>m.Name switch {
            "get_World"=>world,"get_WorldManager"=>manager,"get_Event"=>events,"get_Logger"=>logger,_=>throw new Exception(m.Name)
        });
        using var store=new GroundwaterWorldStore(api);
        void Require(bool value,string message) {if(!value)throw new Exception(message);}
        Require(reads==1 && store.PersistedRegionCount==1 && warnings==0,"Valid groundwater save failed to load before native calendar initialization");
        Require(WorldCalendarTime.Seconds(world)==0,"Uninitialized calendar advanced aquifer time");
        WorldCalendarTime.Reset(world);
        calendarReady=true;
        Require(WorldCalendarTime.Seconds(world)==0,"Calendar initialization earned offline time");
        Require(store.Query(new BlockPos(0,10,0,0))==state,"Startup changed saved aquifer storage or quality");
        hours+=.1;
        Require(Math.Abs(WorldCalendarTime.Seconds(world)-360)<1e-6,"Initialized calendar failed to accumulate elapsed time");
        hours+=100;WorldCalendarTime.Reset(world);
        Require(Math.Abs(WorldCalendarTime.Seconds(world)-360)<1e-6,"Calendar reset earned skipped time");
        Console.WriteLine("PASS saved groundwater loads before native calendar creation; initialization/reset earn no offline time and later ticks advance normally");
        var good = bytes;
        foreach (var invalid in new[] { "{}", "null", "{", "{\"Format\":\"seraph.release.groundwater\",\"Entries\":[]}",
            System.Text.Encoding.UTF8.GetString(good).Replace("\"Schema\":1", "\"Schema\":2"),
            System.Text.Encoding.UTF8.GetString(good).Replace("\"Schema\":1", "\"Schema\":1,\"LastRealSecond\":0") })
        {
            bytes = System.Text.Encoding.UTF8.GetBytes(invalid); var original = (byte[])bytes.Clone();
            try { using var rejected = new GroundwaterWorldStore(api); throw new Exception("Unsupported aquifer state accepted."); }
            catch (InvalidDataException) { }
            Require(bytes.SequenceEqual(original), "Rejected aquifer bytes changed.");
        }
        bytes = good;
        Console.WriteLine("PASS aquifer RC identities, missing schema, unknown fields and corrupt data refused without writing");
    }
}
