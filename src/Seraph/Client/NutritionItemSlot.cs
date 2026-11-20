using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace Seraph.Client;

// A read-only preview using the game's inventory-slot background and item proportions.
// Draw the background dynamically so it follows virtual scrolling with the item.
internal sealed class NutritionItemSlot(ICoreClientAPI api, ElementBounds bounds, Func<ItemSlot?> current, Action<ItemStack> openRecipe)
    : GuiElement(api, bounds)
{
    private int background;
    private ItemStack? failed;
    private ItemstackComponentBase? tooltip;

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        if (args.Button != EnumMouseButton.Left || !IsPositionInside(args.X, args.Y) || current()?.Itemstack is not { } stack) return;
        args.Handled = true;
        openRecipe(stack.Clone());
    }

    public override void ComposeElements(Context ctx, ImageSurface surface) => Bounds.CalcWorldBounds();

    public override void RenderInteractiveElements(float deltaTime)
    {
        var slot = current();
        if (slot?.Itemstack is not { } stack) return;
        if (background == 0) background = GuiElementItemSlotGridBase.DrawSlotBackground(api, Bounds.InnerWidth, Bounds.InnerHeight,
            GuiStyle.DialogSlotBackColor, GuiStyle.DialogSlotFrontColor, null);
        api.Render.Render2DTexturePremultipliedAlpha(background, Bounds.renderX, Bounds.renderY, (float)Bounds.InnerWidth, (float)Bounds.InnerHeight);
        if (ReferenceEquals(failed, stack)) return;
        api.Render.PushScissor(Bounds, stacking: true);
        try
        {
            api.Render.RenderItemstackToGui(slot, Bounds.renderX + Bounds.InnerWidth / 2, Bounds.renderY + Bounds.InnerHeight / 2,
                100, (float)(Bounds.InnerWidth * GuiElementPassiveItemSlot.unscaledItemSize / GuiElementPassiveItemSlot.unscaledSlotSize),
                -1, deltaTime, showStackSize: false);
        }
        catch (Exception ex)
        {
            failed = stack;
            api.Logger.Warning("[vsslssmod] Nutrition preview failed for {0}: {1}", stack.Collectible.Code, ex.Message);
        }
        finally { api.Render.PopScissor(); }

        // Render tooltips during the GUI render pass. GuiComposer.PostRender runs after
        // the GUI shader is stopped, so drawing the native tooltip there crashes.
        if (IsPositionInside(api.Input.MouseX, api.Input.MouseY))
        {
            tooltip ??= new ItemstackComponentBase(api);
            tooltip.RenderItemstackTooltip(slot, api.Input.MouseX, api.Input.MouseY, deltaTime);
        }
    }

    public override void Dispose()
    {
        tooltip?.Dispose();
        tooltip = null;
        if (background != 0) api.Gui.DeleteTexture(background);
        background = 0;
        base.Dispose();
    }
}
