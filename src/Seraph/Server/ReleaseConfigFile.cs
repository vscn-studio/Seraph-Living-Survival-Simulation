using System.Text.Json;
using System.Text.Json.Serialization;
using Vintagestory.API.Common;

namespace Seraph.Server;

internal static class ReleaseConfigFile
{
    // Read the actual file: native LoadModConfig may return null after a parse error,
    // which would otherwise turn corrupt/RC data into a new config and overwrite it.
    internal static T? Read<T>(ICoreAPI api, string name) where T : class
    {
        var path = Path.Combine(api.GetOrCreateDataPath("ModConfig"), name);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length is <= 0 or > 1048576)
            throw new InvalidDataException("Unsupported configuration size; original file preserved: " + name);
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), new JsonSerializerOptions {
            MaxDepth = 32, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        }) ?? throw new InvalidDataException("Empty release configuration; original file preserved: " + name);
    }
}
