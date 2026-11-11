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
/// (WeaponAssembler.AssembleAsync), confirmed via decompilation, not a WeaponCustomizer or WTT-ServerCommonLib
/// interaction. Repro: a mod A sits in slot S on a weapon. In one continuous build-screen session,
/// remove A, attach an intermediary item I (which exposes its own nested version of slot S), then
/// reattach the same-template A into I's nested slot, then Assemble. The build reports success but I
/// never actually attaches.
///
/// Root cause: AssembleAsync's first pass matches every currently-installed real mod to a target slot by
/// template only (Slot.FullId is "{slotName} {parentItem.TemplateId}", i.e. purely structural -- not
/// instance-based). When A's real instance matches the *nested* target inside I, the original keeps A
/// exactly where it currently sits and just schedules a later move -- it never proactively vacates A's
/// current slot. But that current slot is exactly where I itself needs to go. Neither can be placed
/// first: I needs A's slot freed, A needs I's nested slot to exist (which only happens once I is
/// installed). The vanilla planner retries the whole plan for 5 passes and then silently gives up on
/// both entries -- no exception, no rollback, no error shown to the player.
///
/// Fix: reimplement AssembleAsync, reusing its public helpers, and add one
/// targeted check in the first pass: if a currently-installed mod's own current slot is exactly what
/// some other still-pending target entry needs, evacuate that mod to the sorting table right away
/// (instead of "keeping it in place") and hand it to the existing pool-based matching that already
/// correctly handles multi-level nesting for brand-new items. This only
/// changes behavior for the genuinely circular case; every other (already-working) assemble path is
/// untouched.
/// </summary>
[HarmonyPatch(typeof(WeaponAssembler), "AssembleAsync")]
public static class AssembleCircularSlotFixPatch
{
    private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("MWM-AssembleFix");

    [HarmonyPrefix]
    public static bool Prefix(Weapon itemBody, Weapon buildWeapon, InventoryController inventoryController, IEnumerable<Item> playerItems, bool simulate, ref Option<IEnumerable<MoveResult>> __result)
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

    private static Option<IEnumerable<MoveResult>> ComputeAssemblePlan(Weapon itemBody, Weapon buildWeapon, InventoryController inventoryController, IEnumerable<Item> playerItems, bool simulate)
    {
        WeaponAssembler.CG_AssembleAsync2 state = default;
        state.inventoryController = inventoryController;

        Stack<Slot> targetSlots = new Stack<Slot>(buildWeapon.Slots.Concat(buildWeapon.Chambers));
        state.modsWithSlots = WeaponAssembler.ConvertSlotsLayerByLayer(targetSlots, (Mod mod, Slot slot) => new WeaponAssembler.ModWithSlot(mod, slot.FullId));

        state.playerMods = playerItems.OfType<Mod>()
            .Where(item => item.Parent.Container.ParentItem.IsContainer && item.PinLockState != EItemPinLockState.Locked)
            .ToList();

        state.sortingTableGrid = state.inventoryController.Inventory.SortingTable.Grid;

        List<Mod> currentlyInstalled = WeaponAssembler.ConvertSlotsLayerByLayer(new Stack<Slot>(itemBody.Slots.Concat(itemBody.Chambers)), (Mod mod, Slot _) => mod);
        state.modsToInstall = new List<WeaponAssembler.ModWithSlot>();
        state.moveOperations = new List<MoveResult>();

        try
        {
            for (int num = currentlyInstalled.Count - 1; num >= 0; num--)
            {
                Mod item2 = currentlyInstalled[num];
                WeaponAssembler.ModWithSlot matchedTarget = state.modsWithSlots.FirstOrDefault(x => x.Mod.TemplateId == item2.TemplateId);

                if (matchedTarget != null)
                {
                    bool blocksAnotherPendingTarget = item2.Parent.Container is Slot currentSlot
                        && state.modsWithSlots.Any(x => x != matchedTarget && x.SlotId == currentSlot.FullId);

                    if (blocksAnotherPendingTarget)
                    {
                        Log.LogInfo($"Deadlock guard fired: {item2.TemplateId} currently occupies a slot another pending item needs -- evacuating to sorting table instead of keeping it in place.");
                        EvacuateMod(item2, ref state);
                        state.playerMods.Add(item2);
                    }
                    else
                    {
                        state.modsWithSlots.Remove(matchedTarget);
                        state.modsToInstall.Add(new WeaponAssembler.ModWithSlot(item2, matchedTarget.SlotId));
                    }
                }
                else
                {
                    EvacuateMod(item2, ref state);
                }
            }

            while (state.modsWithSlots.Count > 0)
            {
                WeaponAssembler.ModWithSlot slot2 = state.modsWithSlots[0];
                state.modsWithSlots.RemoveAt(0);
                Mod mod2 = state.playerMods.FirstOrDefault(playerItem => playerItem.TemplateId == slot2.Mod.TemplateId);
                if (mod2 != null)
                {
                    WeaponAssembler.CG_AssembleAsync(mod2, slot2, ref state);
                }
            }

            foreach (OperationResult<MoveResult> item3 in WeaponAssembler.AssembleOperationsList(itemBody, state.modsToInstall, state.inventoryController))
            {
                WeaponAssembler.CG_AssembleAsync1(item3, ref state);
            }

            foreach (Item item4 in state.sortingTableGrid.Items.ToList())
            {
                GridItemAddress to3 = GridExtensions.FindLocationForItem(state.inventoryController.MainStorage, item4);
                WeaponAssembler.CG_AssembleAsync1(ItemManipulator.Move(item4, to3, state.inventoryController, false), ref state);
            }
        }
        catch (WeaponAssembler.AbortException exception)
        {
            InventoryOperationExtensions.RollBack<MoveResult>(state.moveOperations);
            return exception.Error;
        }
        catch (Exception ex)
        {
            InventoryOperationExtensions.RollBack<MoveResult>(state.moveOperations);
            return new StringError(ex.Message);
        }

        if (simulate)
        {
            InventoryOperationExtensions.RollBack<MoveResult>(state.moveOperations);
        }
        return new Option<IEnumerable<MoveResult>>(state.moveOperations);
    }

    private static void EvacuateMod(Mod item2, ref WeaponAssembler.CG_AssembleAsync2 state)
    {
        foreach (Slot slot3 in item2.Slots)
        {
            if (slot3.ContainedItem != null)
            {
                GridItemAddress to = state.sortingTableGrid.FindLocationForItem(slot3.ContainedItem);
                WeaponAssembler.CG_AssembleAsync1(ItemManipulator.Move(slot3.ContainedItem, to, state.inventoryController, false), ref state);
            }
        }
        GridItemAddress to2 = state.sortingTableGrid.FindLocationForItem(item2);
        WeaponAssembler.CG_AssembleAsync1(ItemManipulator.Move(item2, to2, state.inventoryController, false), ref state);
    }
}
