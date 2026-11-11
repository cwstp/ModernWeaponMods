using System.Collections.Generic;
using System.Threading.Tasks;
using BepInEx.Logging;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace ModernWeaponModsClient;

/// <summary>
/// Every magwell in this mod hosts the real pistol grip in its own nested slot purely as a data-model
/// convenience -- see mwm-ak-magwell-pistolgrip-slot project memory. Visually the grip sits a few
/// inches behind the magwell's own mesh (the real weapon's receiver fills that gap, so it looks correct
/// on the actual gun). But any ISOLATED render of the magwell alone -- the item-inspect 3D preview, and
/// separately the auto-generated 2D inventory icon used for per-slot thumbnails (e.g. the slot grid
/// below the inspect preview) -- shows just the magwell + its full nested-mod subtree on an empty
/// background, with nothing to hide the gap, so the grip appears to float disconnected.
///
/// Both isolated-render paths bottom out in EFT.ObjectsFactory, but via two DIFFERENT overloads that each
/// need their own Harmony patch:
/// - CreateCleanLootPrefab (sync) -- used by EFT.UI.WeaponModding.WeaponPreview for the big 3D preview.
/// - CreateCleanLootPrefabAsync (Task-returning) -- used by EFT's auto-generated
///   icon renderer used anywhere an ItemView shows this item (including ModSlotView's per-slot grid).
///
/// Both patches only touch the freshly-spawned, throwaway GameObject built for that one render call --
/// never the live Item data model or the real equipped weapon's GameObject -- and only act when the
/// magwell itself is the item being rendered (not when it's just one mod among many on a whole-weapon
/// render, where the grip should keep showing normally since the real receiver geometry fills the gap
/// there).
/// </summary>
internal static class MagwellRenderFix
{
    public static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("MWM-PreviewFix");

    // Every magwell that hosts a real pistol grip in its own nested slot -- add new entries here as
    // more magwells migrate to this design, rather than duplicating patches per item.
    public static readonly Dictionary<string, string> NestedGripSlotByMagwellTpl = new()
    {
        ["352765af2bdf69fc66bae7d1"] = "mwm_akts_grip_mount", // LCT Steel Magwell (AK)
        ["c984c5a469a4423033026950"] = "mwm_hrf_grip_mount", // HRF Concepts RCM Milspec AR-15 Magwell
        ["4fa49b740705d7bf5f3d0be6"] = "mwm_hrf_grip_mount", // HRF Concepts RCM Milspec AR-15 Magwell (FDE)
        ["3e58d049c95008d162b3fb6d"] = "mwm_hrf_grip_mount", // HRF Concepts RCM HK416 Magwell
        ["6881caf875719354716dcbc0"] = "mwm_hrf_grip_mount", // HRF Concepts RCM HK416 Magwell (FDE)
        ["3e87eb9c57ae18a5f4be25c0"] = "mwm_hrf_grip_mount", // HRF Concepts RCM SCAR-L Magwell
        ["cbbe5de32eac15d64ae03022"] = "mwm_hrf_grip_mount", // HRF Concepts RCM SCAR-L Magwell (FDE)
        ["d71ecb009595c1decbadd340"] = "mwm_hrf_grip_mount", // HRF Concepts RCM MCX Magwell
        ["906b0021d4381dab57fcc027"] = "mwm_hrf_grip_mount", // HRF Concepts RCM MCX Magwell (FDE)
    };

    public static void HideNestedGrip(Item item, GameObject spawned, string context)
    {
        if (item == null || spawned == null) return;
        if (!NestedGripSlotByMagwellTpl.TryGetValue(item.TemplateId.ToString(), out string slotName)) return;

        Transform found = FindChildRecursive(spawned.transform, slotName);
        if (found == null)
        {
            Log.LogInfo($"[MagwellRenderFix] ({context}) Previewing magwell {item.TemplateId} but no '{slotName}' transform found in the spawned prefab -- nothing to hide.");
            return;
        }

        found.gameObject.SetActive(false);
        Log.LogInfo($"[MagwellRenderFix] ({context}) Hid nested grip subtree ('{found.name}') for magwell {item.TemplateId}.");
    }

    private static Transform FindChildRecursive(Transform parent, string name)
    {
        foreach (Transform child in parent)
        {
            if (child.name == name) return child;
            Transform found = FindChildRecursive(child, name);
            if (found != null) return found;
        }
        return null;
    }
}

[HarmonyPatch(typeof(EFT.ObjectsFactory), "CreateCleanLootPrefab")]
public static class MagwellPreviewGripHidePatch
{
    [HarmonyPostfix]
    public static void Postfix(Item item, ref GameObject __result)
    {
        MagwellRenderFix.HideNestedGrip(item, __result, "3D preview");
    }
}

[HarmonyPatch(typeof(EFT.ObjectsFactory), "CreateCleanLootPrefabAsync")]
public static class MagwellIconGripHidePatch
{
    [HarmonyPostfix]
    public static void Postfix(Item item, ref Task<GameObject> __result)
    {
        if (item == null || !MagwellRenderFix.NestedGripSlotByMagwellTpl.ContainsKey(item.TemplateId.ToString())) return;
        __result = HideAfterLoad(item, __result);
    }

    private static async Task<GameObject> HideAfterLoad(Item item, Task<GameObject> original)
    {
        GameObject spawned = await original;
        MagwellRenderFix.HideNestedGrip(item, spawned, "auto-generated icon");
        return spawned;
    }
}
