using BepInEx;
using HarmonyLib;

namespace ModernWeaponModsClient;

/// <summary>
/// BepInEx entry point for MWM's client-side Harmony patches (see MagwellBoltCatchLayerWeightPatch.cs).
/// </summary>
[BepInPlugin("org.kilodyne.modernweaponmods.client", "Modern Weapon Mods (MWM) Client", "1.0.0")]
public class ModernWeaponModsClientPlugin : BaseUnityPlugin
{
    private Harmony _harmony;

    private void Awake()
    {
        _harmony = new Harmony("org.kilodyne.modernweaponmods.client");
        _harmony.PatchAll();
        Logger.LogInfo("Modern Weapon Mods (MWM) Client loaded.");
    }
}
