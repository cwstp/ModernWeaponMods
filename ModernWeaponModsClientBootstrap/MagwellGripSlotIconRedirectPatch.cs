using System.Collections.Generic;
using EFT.InventoryLogic;
using EFT.UI.DragAndDrop;
using HarmonyLib;
using UnityEngine;

namespace ModernWeaponModsClient;

/// <summary>
/// Every magwell's nested pistol-grip slot is deliberately given a globally-unique name (not
/// mod_pistol_grip) to avoid the collision issues chased down in mwm-ak-magwell-pistolgrip-slot
/// project memory. But EFT.UI.DragAndDrop.ModSlotView.Show looks up each slot's empty-state
/// placeholder sprite (the striped background + generic silhouette shown when a slot has nothing
/// installed) by resource key "Slots/" + slot.Name -- a real, pre-made per-slot-type asset that only
/// exists for actual vanilla slot names. For our made-up names, that lookup returns null, so
/// ModSlotView.Show just skips setting the background image entirely and logs an error -- leaving the
/// slot rendered as a blank white square when empty in the inspect window (confirmed by decompile).
///
/// IMPORTANT: an earlier version of this patch tried to Harmony-patch EFT.Utilities.ResourcesCache.
/// Pop&lt;TV&gt; directly (the generic resource-cache lookup ModSlotView.Show calls internally) and swap
/// just the one key. That crashed MonoMod's IL detour builder with a NotSupportedException while
/// importing the open generic parameter TV -- confirmed via BepInEx's log once WriteUnityLog was
/// temporarily enabled to reveal it (see mwm-ak-magwell-pistolgrip-slot memory's WriteUnityLog gotcha
/// note for the general pattern). That exception propagated out of Harmony's PatchAll() and aborted
/// this whole plugin's Awake() -- which is why every other magwell render/label fix broke too, not just
/// this one. Patching open generic methods isn't viable in this runtime; the fix instead Prefixes the
/// concrete, non-generic ModSlotView.Show and calls EFT.Utilities.ResourcesCache.Pop&lt;Sprite&gt;
/// normally (an ordinary compiled call, not a Harmony detour target) to fetch the real mod_pistol_grip
/// placeholder and set it directly for our slot, before the original method runs. The original's own
/// (failed) lookup for our slot's real name still returns null afterward, so it never overwrites what
/// this Prefix already set.
/// </summary>
[HarmonyPatch(typeof(ModSlotView), "Show")]
public static class MagwellGripSlotIconRedirectPatch
{
    // Every magwell's own nested grip-mount slot name -- add new entries here as more magwells migrate
    // to this design, rather than duplicating patches per item.
    private static readonly HashSet<string> OurSlotNames = new()
    {
        "mwm_akts_grip_mount", // LCT Steel Magwell (AK)
        "mwm_hrf_grip_mount", // HRF Concepts RCM Milspec AR-15 Magwell (both color variants)
    };

    private const string RedirectKey = "Slots/mod_pistol_grip";

    [HarmonyPrefix]
    public static void Prefix(ModSlotView __instance, Slot slot)
    {
        if (slot == null || !OurSlotNames.Contains(slot.Name)) return;

        Sprite sprite = EFT.Utilities.ResourcesCache.Pop<Sprite>(RedirectKey);
        if (sprite != null)
        {
            __instance.SetSlotBackImage(sprite);
        }
    }
}

/// <summary>
/// Same root cause as MagwellGripSlotIconRedirectPatch, different lookup: ModSlotView.Show also sets
/// the slot's text label from EFT.LocalizationExtensions.Localized(slot.Name, ...).ToUpper() -- a locale-table lookup
/// keyed by the slot's raw name. Vanilla slot names like "mod_pistol_grip" have a real locale entry
/// ("Pistol Grip"); our made-up slot names don't, so Localized falls back to returning the raw key
/// unchanged, showing the raw slot name as the label instead of "PISTOL GRIP".
///
/// Redirects the same set of slot names, this time through EFT.LocalizationExtensions.Localized(string, string) -- the
/// (id, prefix) overload ModSlotView.Show actually calls -- so the localized label matches whatever
/// language the player's client is set to, not just a hardcoded English string.
/// </summary>
[HarmonyPatch(typeof(EFT.LocalizationExtensions), "Localized", typeof(string), typeof(string))]
public static class MagwellGripSlotLabelRedirectPatch
{
    private static readonly HashSet<string> OurSlotNames = new()
    {
        "mwm_akts_grip_mount",
        "mwm_hrf_grip_mount",
    };

    private const string RedirectName = "mod_pistol_grip";

    [HarmonyPrefix]
    public static void Prefix(ref string id)
    {
        if (OurSlotNames.Contains(id))
        {
            id = RedirectName;
        }
    }
}
