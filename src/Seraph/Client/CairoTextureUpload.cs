using Cairo;
using Vintagestory.API.Client;

namespace Seraph.Client;

internal static class CairoTextureUpload
{
    internal static void Update(ICoreClientAPI api, ImageSurface surface, ref LoadedTexture? texture)
    {
        // VS 1.22.7 dereferences the destination immediately; ref does not allocate it.
        if (texture is null || texture.Disposed) texture = new LoadedTexture(api);
        surface.Flush();
        api.Gui.LoadOrUpdateCairoTexture(surface, true, ref texture);
    }
}
