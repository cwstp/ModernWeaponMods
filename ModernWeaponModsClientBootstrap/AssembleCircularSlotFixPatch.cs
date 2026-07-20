using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using Diz.LanguageExtensions;
using EFT.InventoryLogic;
using HarmonyLib;

namespace ModernWeaponModsClient;

/// <summary>
/// Fixes a vanilla EFT client bug in the weapon build screen's Assemble algorithm
/// (GClass3470.smethod_0), confirmed via decompilation, not a WeaponCustomizer or WTT-ServerCommonLib
/// interaction. Repro: a mod A sits in slot S on a weapon. In one continuous build-screen session,
/// remove A, attach an intermediary item I (which exposes its own nested version of slot S), then
/// reattach the same-template A into I's nested slot, then Assemble. The build reports success but I
/// never actually attaches.
///
/// Root cause: smethod_0's first pass matches every currently-installed real mod to a target slot by
/// template only (Slot.FullId is "{slotName} {parentItem.TemplateId}", i.e. purely structural -- not
/// instance-based). When A's real instance matches the *nested* target inside I, the original keeps A
/// exactly where it currently sits and just schedules a later move -- it never proactively vacates A's
/// current slot. But that current slot is exactly where I itself needs to go. Neither can be placed
/// first: I needs A's slot freed, A needs I's nested slot to exist (which only happens once I is
/// installed). GClass3470.smethod_2 retries the whole plan for 5 passes and then silently gives up on
/// both entries -- no exception, no rollback, no error shown to the player.
///
/// Fix: reimplement smethod_0, reusing its own (public) smethod_1-5 helpers unchanged, and add one
/// targeted check in the first pass: if a currently-installed mod's own current slot is exactly what
/// some other still-pending target entry needs, evacuate that mod to the sorting table right away
/// (instead of "keeping it in place") and hand it to the existing pool-based matching (loop 2 /
/// smethod_4) that already correctly handles multi-level nesting for brand-new items. This only
/// changes behavior for the genuinely circular case; every other (already-working) assemble path is
/// untouched.
/// </summary>
[HarmonyPatch(typeof(GClass3470), "smethod_0")]
public static class AssembleCircularSlotFixPatch
{
    private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("MWM-AssembleFix");

    [HarmonyPrefix]
    public static bool Prefix(Weapon itemBody, Weapon buildWeapon, InventoryController inventoryController, IEnumerable<Item> playerItems, bool simulate, ref GStruct156<IEnumerable<GClass3411>> __result)
    {
        try
        {
            __result = ComputeAssemblePlan(itemBody, buildWeapon, inventoryController, playerItems, simulate);
        }
        catch (Exception ex)
        {
            Log.LogError($"AssembleCircularSlotFixPatch threw, falling back to vanilla Assemble logic: {ex}");
            return true;
        }
        return false;
    }

    private static GStruct156<IEnumerable<GClass3411>> ComputeAssemblePlan(Weapon itemBody, Weapon buildWeapon, InventoryController inventoryController, IEnumerable<Item> playerItems, bool simulate)
    {
        GClass3470.Struct904 struct904_ = default;
        struct904_.inventoryController = inventoryController;

        Stack<Slot> targetSlots = new Stack<Slot>(buildWeapon.Slots.Concat(buildWeapon.Chambers));
        struct904_.modsWithSlots = GClass3470.smethod_3(targetSlots, (Mod mod, Slot slot) => new GClass3470.Class2492(mod, slot.FullId));

        struct904_.playerMods = playerItems.OfType<Mod>()
            .Where(item => item.Parent.Container.ParentItem.IsContainer && item.PinLockState != EItemPinLockState.Locked)
            .ToList();

        SortingTableItemClass sortingTable = struct904_.inventoryController.Inventory.SortingTable;
        struct904_.sortingTableGrid = sortingTable.Grid;

        List<Mod> currentlyInstalled = GClass3470.smethod_3(new Stack<Slot>(itemBody.Slots.Concat(itemBody.Chambers)), (Mod mod, Slot _) => mod);
        struct904_.modsToInstall = new List<GClass3470.Class2492>();
        struct904_.moveOperations = new List<GClass3411>();

        try
        {
            for (int num = currentlyInstalled.Count - 1; num >= 0; num--)
            {
                Mod item2 = currentlyInstalled[num];
                GClass3470.Class2492 matchedTarget = struct904_.modsWithSlots.FirstOrDefault(x => x.Mod.TemplateId == item2.TemplateId);

                if (matchedTarget != null)
                {
                    bool blocksAnotherPendingTarget = item2.Parent.Container is Slot currentSlot
                        && struct904_.modsWithSlots.Any(x => x != matchedTarget && x.SlotId == currentSlot.FullId);

                    if (blocksAnotherPendingTarget)
                    {
                        Log.LogInfo($"Deadlock guard fired: {item2.TemplateId} currently occupies a slot another pending item needs -- evacuating to sorting table instead of keeping it in place.");
                        EvacuateMod(item2, ref struct904_);
                        struct904_.playerMods.Add(item2);
                    }
                    else
                    {
                        struct904_.modsWithSlots.Remove(matchedTarget);
                        struct904_.modsToInstall.Add(new GClass3470.Class2492(item2, matchedTarget.SlotId));
                    }
                }
                else
                {
                    EvacuateMod(item2, ref struct904_);
                }
            }

            while (struct904_.modsWithSlots.Count > 0)
            {
                GClass3470.Class2492 slot2 = struct904_.modsWithSlots[0];
                struct904_.modsWithSlots.RemoveAt(0);
                Mod mod2 = struct904_.playerMods.FirstOrDefault(playerItem => playerItem.TemplateId == slot2.Mod.TemplateId);
                if (mod2 != null)
                {
                    GClass3470.smethod_4(mod2, slot2, ref struct904_);
                }
            }

            foreach (GStruct154<GClass3411> item3 in GClass3470.smethod_2(itemBody, struct904_.modsToInstall, struct904_.inventoryController))
            {
                GClass3470.smethod_5(item3, ref struct904_);
            }

            StashGridClass[] mainStorage = struct904_.inventoryController.MainStorage;
            foreach (Item item4 in struct904_.sortingTableGrid.Items.ToList())
            {
                GClass3393 to3 = GClass3119.FindLocationForItem(mainStorage, item4);
                GClass3470.smethod_5(InteractionsHandlerClass.Move(item4, to3, struct904_.inventoryController), ref struct904_);
            }
        }
        catch (GClass3470.Exception4 exception)
        {
            GClass3399.RollBack(struct904_.moveOperations);
            return exception.Error;
        }
        catch (Exception ex)
        {
            GClass3399.RollBack(struct904_.moveOperations);
            return new GClass1522(ex.Message);
        }

        if (simulate)
        {
            GClass3399.RollBack(struct904_.moveOperations);
        }
        return new GStruct156<IEnumerable<GClass3411>>((IEnumerable<GClass3411>)struct904_.moveOperations);
    }

    private static void EvacuateMod(Mod item2, ref GClass3470.Struct904 struct904_0)
    {
        foreach (Slot slot3 in item2.Slots)
        {
            if (slot3.ContainedItem != null)
            {
                GClass3393 to = struct904_0.sortingTableGrid.FindLocationForItem(slot3.ContainedItem);
                GClass3470.smethod_5(InteractionsHandlerClass.Move(slot3.ContainedItem, to, struct904_0.inventoryController), ref struct904_0);
            }
        }
        GClass3393 to2 = struct904_0.sortingTableGrid.FindLocationForItem(item2);
        GClass3470.smethod_5(InteractionsHandlerClass.Move(item2, to2, struct904_0.inventoryController), ref struct904_0);
    }
}
