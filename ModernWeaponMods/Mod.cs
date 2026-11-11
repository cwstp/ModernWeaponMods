using System.Reflection;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;

namespace ModernWeaponMods;

/// <summary>
/// Mod metadata. This declares a hard dependency
/// on WTT-ServerCommonLib (com.wtt.commonlib), which provides the custom-item
/// loader we use to clone attachments.
/// </summary>
public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "org.kilodyne.modernweaponmods";

    public string Name { get; init; } = "Modern Weapon Mods (MWM)";

    public string Author { get; init; } = "Kilodyne";

    public List<string>? Contributors { get; init; }

    // Mod version is pulled from the compiled assembly version (see .csproj <Version>).
    public SemanticVersioning.Version Version { get; init; } =
        new SemanticVersioning.Version(typeof(ModMetadata).Assembly.GetName().Version?.ToString(3));

    // Built for SPT 4.1.x (4.1.6 tested target).
    public SemanticVersioning.Range SptVersion { get; init; } =
        new SemanticVersioning.Range("~4.1.0");

    public List<string>? Incompatibilities { get; init; }

    // Requires WTT-CommonLib 3.x (>= 3.0.6) for SPT 4.1 (provides CustomItemServiceExtended).
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; } =
        new Dictionary<string, SemanticVersioning.Range>
        {
            { "com.wtt.commonlib", new SemanticVersioning.Range("^3.0.6") }
        };

    public bool HasPrepatcher { get; init; }

    public string? Url { get; init; }

    public string License { get; init; } = "MIT";
}

/// <summary>
/// Entry point. Runs on server load (after the database is populated) and hands
/// our assembly to WTT-ServerCommonLib, which reads db/CustomItems/*.json sitting
/// next to this DLL and clones the configured item(s) into the live database.
/// </summary>
[Injectable(InjectionType.Transient, 400002)]
public class ModernWeaponMods : IOnLoad
{
    private readonly WTTServerCommonLib.WTTServerCommonLib _wttCommon;

    public ModernWeaponMods(WTTServerCommonLib.WTTServerCommonLib wttCommon)
    {
        _wttCommon = wttCommon;
    }

    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var assembly = Assembly.GetExecutingAssembly();

        // Clones every item defined in db/CustomItems/*.json (currently the one
        // foregrip) and, because addtoModSlots=true, wires it into compatible
        // weapon mod_foregrip slots. Inline "locales" are applied here too, so no
        // separate CustomLocales file is required.
        await _wttCommon.CustomItemServiceExtended.CreateCustomItems(assembly);

        // Mirrors the LDAG into loose loot at the RK-1's spawn points.
        // Reads db/CustomLootspawns/CustomSpawnpoints/*.json sitting next to this DLL.
        await _wttCommon.CustomLootspawnService.CreateCustomLootSpawns(assembly);
    }
}

/// <summary>
/// WTT's own ModSlot processing (see WTTServerCommonLib.Services.ItemServiceHelpers.ModSlotHelper)
/// is only supposed to whitelist our risers into a weapon slot that already allows our
/// itemTplToClone (the vanilla LaRue riser). A raw-DB check showed our riser's tpl appears
/// nowhere else in any mod's item files, so that automatic propagation isn't reliably landing
/// (it's an in-memory-only mutation, so it can never show up in a file grep either -- this
/// check instead runs its own independent pass and doesn't depend on WTT's mechanism at all).
/// This runs very late (int.MaxValue) so every other mod (WTT-Armory, WTT-ContentBackport, our own
/// item creation) has already populated the database, then walks every slot filter in the whole DB
/// and whitelists all four riser variants anywhere the vanilla LaRue riser OR the vanilla GBRS
/// Aimpoint Hydra Mount Kit line is allowed. Checks every filter entry on a slot, not just the
/// first, since some slots carry more than one.
///
/// EpicsAIO's "GBRS Hydra riser combo mount" (BLK/FDE) was dropped from the reference set
/// 2026-07-13 to remove the EpicsAIO dependency here. A DB-wide scan first confirmed its only real
/// effect was whitelisting our risers/OMM centers onto EpicsAIO's MCX Rattler upper receiver
/// (BLK/FDE) and WTT-Armory's Surgeon Rifles 1581 XL (.338 LM/.338 Norma Mag) -- 4 slots total, all
/// on other mods' weapons, not ours. Losing compat with those specific 4 slots was an accepted
/// tradeoff (user confirmed) rather than substituting a vanilla item, since the only vanilla item
/// with comparable "sits under an optic" placement (EOTech EXPS3-0) is whitelisted in 246 slots
/// game-wide -- using it as a reference would've expanded these risers/OMM centers onto basically
/// any weapon's scope slot, not just riser-appropriate ones. See mwm-epicsaio-dependencies.md
/// project memory for the full investigation.
/// </summary>
[Injectable(InjectionType.Transient, int.MaxValue)]
public class RailScalesRiserCompat : IOnLoad
{
    private readonly TemplateTable _templateTable;
    private readonly ISptLogger<RailScalesRiserCompat> _logger;

    // Anything that whitelists either of these should also whitelist our risers -- the vanilla
    // GBRS Aimpoint Hydra Mount Kit line plus the vanilla clone source. (EpicsAIO's GBRS Hydra riser
    // combo mount was removed from this set 2026-07-13 -- see class doc comment above.)
    private static readonly string[] ReferenceTpls =
    {
        "65392f611406374f82152ba5", // GBRS Aimpoint Hydra Mount Kit (Black)
        "653931da5db71d30ab1d6296", // GBRS Aimpoint Hydra Mount Kit (FDE)
        "5c064c400db834001d23f468", // vanilla LaRue LT101 QD Tactical Picatinny Riser mount
    };

    // Our four MonoLift riser variants (BLK, BLK Reversed, FDE, FDE Reversed), the four OMM
    // center pieces (Picatinny/Aimpoint footprint x BLK/FDE), and the four Scalarworks LEAP
    // footprint-specific risers (T-1/ACRO/RMR-SRO/Deltapoint) -- all meant to fit "anywhere
    // MonoLift fits," so they share this same reference set rather than getting their own pass.
    private static readonly string[] RiserTpls =
    {
        "7617e4c90ae38c485a828da4",
        "6f9baada578cf27c77556ba5",
        "fba779feb5b6ecc68ae3cb90",
        "6b0bd0b92f10e4349da624ef",
        "44e2382e3e3bfbe620c549f3", // OMM Picatinny center (BLK)
        "d8f4e7343f56989420671b5c", // OMM Picatinny center (FDE)
        "507ac32e9e171bc222ee23b4", // OMM Micro (Aimpoint-footprint) center (BLK)
        "827bfd52c2e5c3864468d94b", // OMM Micro (Aimpoint-footprint) center (FDE)
        "49463c018ed72e8d5f4548ef", // Scalarworks LEAP/01 T-1
        "6504f5cb4ac4d310ee94371e", // Scalarworks LEAP/03 ACRO
        "fbbb730ffe3edefc39ee35a9", // Scalarworks LEAP/04 RMR/SRO
        "95b745132cccd8b22aee3719", // Scalarworks LEAP/11 Deltapoint
    };

    public RailScalesRiserCompat(TemplateTable templateTable, ISptLogger<RailScalesRiserCompat> logger)
    {
        _templateTable = templateTable;
        _logger = logger;
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        int patched = 0;
        int slotsChecked = 0;
        int slotsMatched = 0;

        foreach (var (itemId, item) in _templateTable.Items)
        {
            var slots = item.Properties?.Slots;
            if (slots == null) continue;

            foreach (var slot in slots)
            {
                var filters = slot.Properties?.Filters;
                if (filters == null) continue;

                foreach (var slotFilter in filters)
                {
                    var filter = slotFilter.Filter;
                    if (filter == null) continue;
                    slotsChecked++;

                    bool isReferenceSlot = false;
                    foreach (var refTpl in ReferenceTpls)
                    {
                        if (filter.Contains(refTpl)) { isReferenceSlot = true; break; }
                    }
                    if (!isReferenceSlot) continue;
                    slotsMatched++;

                    foreach (var riserTpl in RiserTpls)
                    {
                        if (filter.Add(riserTpl))
                        {
                            patched++;
                            _logger.Debug($"[RailScalesRiserCompat] Added {riserTpl} to slot '{slot.Name}' for parent {itemId}");
                        }
                    }
                }
            }
        }

        _logger.Debug($"[RailScalesRiserCompat] Checked {slotsChecked} slot filter(s), {slotsMatched} matched GBRS/vanilla-riser whitelist, {patched} riser addition(s) made.");
        return Task.CompletedTask;
    }
}

/// <summary>
/// The Unity FAST Optic Riser, Laser variants are clones of EpicsAIO's own FAST riser, but they're
/// meant to carry a laser/IR device on top instead of an optic, so they should never land in a scope
/// slot -- even though the clone source (and other items, like our own MonoLift's mod_scope filter)
/// already whitelist that clone source tpl there. addtoModSlots is disabled entirely for these items
/// so WTT's built-in ModSlotHelper propagation (which matches on itemTplToClone, i.e. the FAST riser
/// itself) never runs for them; all slot whitelisting instead happens here, independently: anywhere
/// the vanilla AN/PEQ-15 or the vanilla NcSTAR MPR45 Backup mount is allowed, our stuff should be too.
/// AN/PEQ-15 was picked over EpicsAIO's own Perst 4 laser as the reference tpl because a DB check
/// confirmed every one of EpicsAIO's modded rails that whitelists Perst 4 also whitelists AN/PEQ-15
/// in that same filter (152 for 152, no exceptions) -- so this reaches the identical set of modded
/// rails while depending on nothing but vanilla content.
///
/// Also covers the Axon remote pressure switches, but NOT with the same scope-slot exclusion: a DB
/// check showed 59 of the 90 vanilla slots that accept MPR45 are actually named mod_scope (it's a
/// backup *sight* mount, not a laser), so excluding scope slots for Axon the same way as the risers
/// would cut it off from most of its real MPR45 footprint. The two riser variants keep the
/// scope-slot exclusion (that one's an explicit, intentional design choice for them); the Axons do
/// not. The Axons additionally match on the vanilla Insight WMX200 flashlight -- that reference is
/// Axon-only, the FAST risers don't get it, since a light isn't something a riser would ever carry.
/// Runs very late (int.MaxValue) so every other mod's items already exist in the DB.
/// </summary>
[Injectable(InjectionType.Transient, int.MaxValue)]
public class FastRiserLaserCompat : IOnLoad
{
    private readonly TemplateTable _templateTable;
    private readonly ISptLogger<FastRiserLaserCompat> _logger;

    // Anything that whitelists either of these tactical devices should also whitelist our stuff.
    private static readonly string[] ReferenceTpls =
    {
        "544909bb4bdc2d6f028b4577", // vanilla L3Harris AN/PEQ-15 tactical device
        "5649a2464bdc2d91118b45a8", // vanilla NcSTAR MPR45 Backup mount
    };

    // Extra reference(s) that only apply to the Axon switches, not the FAST risers.
    private static readonly string[] AxonOnlyReferenceTpls =
    {
        "626becf9582c3e319310b837", // vanilla Insight WMX200 tactical flashlight
    };

    // Our two FAST riser variants (BLK, FDE) -- excluded from scope slots, by design.
    private static readonly string[] ScopeExcludedTpls =
    {
        "72059f6f3247d38aa36d7580", // Unity FAST Optic Riser, Laser (BLK)
        "d7806318f10f5756811a1d3f", // Unity FAST Optic Riser, Laser (FDE)
    };

    // The Axon remote pressure switches -- allowed in scope slots too, since that's most of MPR45's footprint.
    private static readonly string[] ScopeAllowedTpls =
    {
        "886708de6cc8b950fa43ac97", // Unity Axon Remote Pressure Switch
        "d49ee3c29f70d7fbb93ac8b6", // Unity Axon SL Remote Pressure Switch
        "89cfbe182fe2f3b4b980c7dc", // Insight Technologies RMT-400-A8 Remote Pressure Switch
        "52c77113e19848cc5fba06d5", // Insight Technologies RMT-400-A8 Remote Pressure Switch (FDE)
        "4e0f7de513217dc64bd97c0e", // Arisaka Offset Scout Mount Picatinny (BLK)
        "b69a7ebfa566f510e91f02d6", // Arisaka Offset Scout Mount Picatinny (FDE)
        "44524a4f78e5e12b05cece3f", // Arisaka Offset Scout Mount Picatinny (Reverse) (BLK)
        "283ba079475b5bfe7b83f751", // Arisaka Offset Scout Mount Picatinny (Reverse) (FDE)
        "f6a58520ee765a6bf56694f2", // Arisaka Inline Scout Mount Picatinny (BLK)
        "2b3f25921683a313ba92de44", // Arisaka Inline Scout Mount Picatinny (FDE)
    };

    public FastRiserLaserCompat(TemplateTable templateTable, ISptLogger<FastRiserLaserCompat> logger)
    {
        _templateTable = templateTable;
        _logger = logger;
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        int patched = 0;
        int slotsChecked = 0;
        int slotsMatched = 0;

        foreach (var (itemId, item) in _templateTable.Items)
        {
            var slots = item.Properties?.Slots;
            if (slots == null) continue;

            foreach (var slot in slots)
            {
                bool isScopeSlot = slot.Name != null && slot.Name.Contains("scope", StringComparison.OrdinalIgnoreCase);

                var filters = slot.Properties?.Filters;
                if (filters == null) continue;

                foreach (var slotFilter in filters)
                {
                    var filter = slotFilter.Filter;
                    if (filter == null) continue;
                    slotsChecked++;

                    bool isCommonReferenceSlot = false;
                    foreach (var refTpl in ReferenceTpls)
                    {
                        if (filter.Contains(refTpl)) { isCommonReferenceSlot = true; break; }
                    }

                    bool isAxonReferenceSlot = isCommonReferenceSlot;
                    if (!isAxonReferenceSlot)
                    {
                        foreach (var refTpl in AxonOnlyReferenceTpls)
                        {
                            if (filter.Contains(refTpl)) { isAxonReferenceSlot = true; break; }
                        }
                    }

                    if (!isCommonReferenceSlot && !isAxonReferenceSlot) continue;
                    slotsMatched++;

                    if (isCommonReferenceSlot && !isScopeSlot)
                    {
                        foreach (var targetTpl in ScopeExcludedTpls)
                        {
                            if (filter.Add(targetTpl))
                            {
                                patched++;
                                _logger.Debug($"[FastRiserLaserCompat] Added {targetTpl} to slot '{slot.Name}' for parent {itemId}");
                            }
                        }
                    }

                    if (!isAxonReferenceSlot) continue;

                    foreach (var targetTpl in ScopeAllowedTpls)
                    {
                        if (filter.Add(targetTpl))
                        {
                            patched++;
                            _logger.Debug($"[FastRiserLaserCompat] Added {targetTpl} to slot '{slot.Name}' for parent {itemId}");
                        }
                    }
                }
            }
        }

        _logger.Debug($"[FastRiserLaserCompat] Checked {slotsChecked} slot filter(s), {slotsMatched} matched AN/PEQ-15/MPR45/WMX200 whitelist, {patched} addition(s) made.");
        return Task.CompletedTask;
    }
}

/// <summary>
/// Our CTR RISR stocks clone the vanilla Magpul CTR directly (itemTplToClone is the vanilla CTR
/// tpl itself), so in theory WTT's own ModSlotHelper propagation should whitelist them anywhere
/// the vanilla CTR fits, no extra work needed. In practice -- same story as RailScalesRiserCompat
/// -- that automatic propagation isn't reliably landing on every slot (reported missing on modded
/// receivers/buffer tubes like the SAG AK buffer tube), so this runs its own independent DB walk
/// instead of trusting WTT's mechanism. Unlike the FAST riser compat, there's no slot-type
/// exclusion here: the ask is literally "anywhere the vanilla CTR fits, ours should too," full
/// stop, on any weapon or modded part, vanilla or otherwise. Runs very late (int.MaxValue) so
/// every other mod's items already exist in the DB.
/// </summary>
[Injectable(InjectionType.Transient, int.MaxValue)]
public class CtrRisrStockCompat : IOnLoad
{
    private readonly TemplateTable _templateTable;
    private readonly ISptLogger<CtrRisrStockCompat> _logger;

    // Anything that whitelists either vanilla CTR color should also whitelist both of ours.
    private static readonly string[] ReferenceTpls =
    {
        "5d135e83d7ad1a21b83f42d8", // vanilla AR-15 Magpul CTR Carbine stock (Black)
        "5d135ecbd7ad1a21c176542e", // vanilla AR-15 Magpul CTR Carbine stock (FDE)
    };

    // Our three CTR RISR stock variants (BLK, FDE, OD).
    private static readonly string[] StockTpls =
    {
        "7b41f91293413b4f30e73d68", // AR-15 Magpul CTR Carbine Stock, LaRue RISR (BLK)
        "19c74cdb36e2a8b65b044ec3", // AR-15 Magpul CTR Carbine Stock, LaRue RISR (FDE)
        "6a672956e206c16778a0047d", // AR-15 Magpul CTR Carbine Stock, LaRue RISR (OD)
    };

    public CtrRisrStockCompat(TemplateTable templateTable, ISptLogger<CtrRisrStockCompat> logger)
    {
        _templateTable = templateTable;
        _logger = logger;
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        int patched = 0;
        int slotsChecked = 0;
        int slotsMatched = 0;

        foreach (var (itemId, item) in _templateTable.Items)
        {
            var slots = item.Properties?.Slots;
            if (slots == null) continue;

            foreach (var slot in slots)
            {
                var filters = slot.Properties?.Filters;
                if (filters == null) continue;

                foreach (var slotFilter in filters)
                {
                    var filter = slotFilter.Filter;
                    if (filter == null) continue;
                    slotsChecked++;

                    bool isReferenceSlot = false;
                    foreach (var refTpl in ReferenceTpls)
                    {
                        if (filter.Contains(refTpl)) { isReferenceSlot = true; break; }
                    }
                    if (!isReferenceSlot) continue;
                    slotsMatched++;

                    foreach (var stockTpl in StockTpls)
                    {
                        if (filter.Add(stockTpl))
                        {
                            patched++;
                            _logger.Debug($"[CtrRisrStockCompat] Added {stockTpl} to slot '{slot.Name}' for parent {itemId}");
                        }
                    }
                }
            }
        }

        _logger.Debug($"[CtrRisrStockCompat] Checked {slotsChecked} slot filter(s), {slotsMatched} matched vanilla-CTR whitelist, {patched} stock addition(s) made.");
        return Task.CompletedTask;
    }
}

/// <summary>
/// Our MOE SL-K stocks clone the vanilla Magpul MOE SL-K (Coyote Tan) directly (itemTplToClone is
/// the vanilla tpl itself), so in theory WTT's own ModSlotHelper propagation should whitelist them
/// anywhere the vanilla MOE SL-K fits, no extra work needed. In practice -- the exact same gap
/// CtrRisrStockCompat was built to close (that class's own doc comment names the SAG AK-545 buffer
/// tube as one of the modded items where the automatic broadcast was found missing) -- confirmed
/// user-reported (2026-07-18) missing on that same SAG AK-545 buffer tube, even though the vanilla
/// MOE SL-K IS present in that item's own mod_stock Filter. The fix was applied to CTR RISR at the
/// time but never extended to these stocks. Runs very late (int.MaxValue) so every other mod's
/// items already exist in the DB.
/// </summary>
[Injectable(InjectionType.Transient, int.MaxValue)]
public class MoeSlkStockCompat : IOnLoad
{
    private readonly TemplateTable _templateTable;
    private readonly ISptLogger<MoeSlkStockCompat> _logger;

    // Anything that whitelists the vanilla MOE SL-K (Coyote Tan) should also whitelist all three of
    // ours. Only one vanilla color variant of this stock exists (Coyote Tan) -- unlike the CTR,
    // there's no separate vanilla Black tpl to also reference.
    private static readonly string[] ReferenceTpls =
    {
        "6529370c405a5f51dd023db8", // vanilla AR-15 Magpul MOE SL-K buttstock (Coyote Tan)
    };

    // Our five MOE SL-K stock variants.
    private static readonly string[] StockTpls =
    {
        "03246ffd01a23f2d0bcef8e2", // AR-15 Magpul MOE SL-K Stock (BLK)
        "22b2d38ef697ab13d7f11361", // AR-15 Magpul MOE SL-K Stock w/Riser (BLK)
        "c1f2d46444d1193edc214192", // AR-15 Magpul MOE SL-K Stock w/Riser (FDE)
        "34c70908c0f30e60cd8f0b6e", // AR-15 Magpul MOE SL-K Stock (OD)
        "450e86286d83f307f2b1d089", // AR-15 Magpul MOE SL-K Stock w/Riser (OD)
    };

    public MoeSlkStockCompat(TemplateTable templateTable, ISptLogger<MoeSlkStockCompat> logger)
    {
        _templateTable = templateTable;
        _logger = logger;
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        int patched = 0;
        int slotsChecked = 0;
        int slotsMatched = 0;

        foreach (var (itemId, item) in _templateTable.Items)
        {
            var slots = item.Properties?.Slots;
            if (slots == null) continue;

            foreach (var slot in slots)
            {
                var filters = slot.Properties?.Filters;
                if (filters == null) continue;

                foreach (var slotFilter in filters)
                {
                    var filter = slotFilter.Filter;
                    if (filter == null) continue;
                    slotsChecked++;

                    bool isReferenceSlot = false;
                    foreach (var refTpl in ReferenceTpls)
                    {
                        if (filter.Contains(refTpl)) { isReferenceSlot = true; break; }
                    }
                    if (!isReferenceSlot) continue;
                    slotsMatched++;

                    foreach (var stockTpl in StockTpls)
                    {
                        if (filter.Add(stockTpl))
                        {
                            patched++;
                            _logger.Debug($"[MoeSlkStockCompat] Added {stockTpl} to slot '{slot.Name}' for parent {itemId}");
                        }
                    }
                }
            }
        }

        _logger.Debug($"[MoeSlkStockCompat] Checked {slotsChecked} slot filter(s), {slotsMatched} matched vanilla MOE SL-K whitelist, {patched} stock addition(s) made.");
        return Task.CompletedTask;
    }
}

/// <summary>
/// Our Arisaka Offset/Inline Scout Mounts all clone the vanilla Kiba Arms 25mm accessory ring mount
/// (57d17e212459775a1179a0f5), so in theory WTT's ModSlotHelper propagation should whitelist ours
/// anywhere that base fits, no extra work needed. Same story as CtrRisrStockCompat/RailScalesRiserCompat
/// -- that automatic propagation isn't reliably landing on every slot, so this runs its own independent
/// DB walk instead of trusting WTT's mechanism. The Picatinny and M-LOK reference/target pairs are kept
/// separate on purpose: a Picatinny mount shouldn't show up wherever an M-LOK rail fits, and vice versa.
/// Runs very late (int.MaxValue) so every other mod's items already exist in the DB.
///
/// The Picatinny reference set carries both the vanilla Kiba Arms ring mount (our clone base, present
/// for every user) AND the WTT-ContentBackport "SureFire M600 Scout Light Thumbscrew Mount" (added
/// 2026-07-13 as a bonus for WTT-ContentBackport users). Confirmed via DB scan: the WTT mount's 22 slots
/// are a strict subset of the Kiba base's 397, so vanilla-only users lose nothing by WTT-CB's absence --
/// the vanilla base already covers everything the WTT reference would have, plus more. The WTT tpl is
/// kept purely so the intent is explicit and survives any future narrowing of the vanilla reference.
/// (An earlier doc comment here wrongly claimed the Picatinny variants CLONE the WTT mount -- they don't,
/// they clone the vanilla Kiba base, same as the M-LOK variants. See mwm-epicsaio-dependencies.md.)
/// </summary>
[Injectable(InjectionType.Transient, int.MaxValue)]
public class ArisakaOsmCompat : IOnLoad
{
    private readonly TemplateTable _templateTable;
    private readonly ISptLogger<ArisakaOsmCompat> _logger;

    // Anything that whitelists either of these should also whitelist our Picatinny OSM: the vanilla Kiba
    // Arms ring mount (our own clone base -- guarantees vanilla-only users get full coverage) plus the
    // WTT-ContentBackport M600 mount (additive bonus for WTT-CB users; its 22 slots are a subset of the
    // Kiba base's 397, so this line requires nothing).
    private static readonly string[] PicatinnyReferenceTpls =
    {
        "57d17e212459775a1179a0f5", // vanilla Kiba Arms 25mm accessory ring mount (our clone base)
        "689c8981bce76ccfbf01862f", // WTT-ContentBackport SureFire M600 Scout Light Thumbscrew Mount
    };

    // Our Arisaka Offset Scout Mount Picatinny (both orientations) and Inline Scout Mount Picatinny.
    private static readonly string[] PicatinnyOsmTpls =
    {
        "4e0f7de513217dc64bd97c0e", // Arisaka Offset Scout Mount Picatinny (BLK)
        "b69a7ebfa566f510e91f02d6", // Arisaka Offset Scout Mount Picatinny (FDE)
        "44524a4f78e5e12b05cece3f", // Arisaka Offset Scout Mount Picatinny (Reverse) (BLK)
        "283ba079475b5bfe7b83f751", // Arisaka Offset Scout Mount Picatinny (Reverse) (FDE)
        "f6a58520ee765a6bf56694f2", // Arisaka Inline Scout Mount Picatinny (BLK)
        "2b3f25921683a313ba92de44", // Arisaka Inline Scout Mount Picatinny (FDE)
    };

    // Anything that whitelists the vanilla Magpul M-LOK 2.5" rail should also whitelist our MLOK OSM.
    private static readonly string[] MlokReferenceTpls =
    {
        "5b7be47f5acfc400170e2dd2", // vanilla Magpul M-LOK 2.5 inch rail
    };

    // Our Arisaka Offset Scout Mount MLOK (both orientations), plus other items that should only
    // fit wherever the vanilla M-LOK 2.5" rail fits.
    private static readonly string[] MlokOsmTpls =
    {
        "6b84b1e50872d3efb38d5e05", // Arisaka Offset Scout Mount MLOK (BLK)
        "044fd98008ac0b26edb448e7", // Arisaka Offset Scout Mount MLOK (FDE)
        "24871afd898bf6c9b7e9ea4b", // Arisaka Offset Scout Mount MLOK (Reverse) (BLK)
        "460da969f0ef8408a94d3cde", // Arisaka Offset Scout Mount MLOK (Reverse) (FDE)
        "0f73207955216d0d7f095ff8", // RailScales XOS MLOK (Long)
        "7f0284eba6fda800c17f7cdb", // RailScales XOS MLOK (Short)
        "99c41e1032b468759d406f4d", // Magpul M-LOK Rail Covers, Type 2 , Short (BLK) -- functional clone of XOS (Short)
        "de9b95f17c199774f126d90a", // Magpul M-LOK Rail Covers, Type 2 , Long (BLK) -- functional clone of XOS (Short)
        "684aec09ebd43dcda04c010a", // Magpul M-LOK Rail Covers, Type 2 , Short (FDE) -- functional clone of XOS (Short)
        "55ee87ae4e1ee843d3c1aa2b", // Magpul M-LOK Rail Covers, Type 2 , Long (FDE) -- functional clone of XOS (Short)
    };

    // Anything that whitelists the vanilla Magpul M-LOK 4.1" rail should also whitelist our XOS
    // items specifically -- kept separate from MlokReferenceTpls/MlokOsmTpls so this doesn't also
    // extend to the OSM MLOK items, which weren't asked to fit the 4.1" rail.
    private static readonly string[] Mlok41ReferenceTpls =
    {
        "5b7be4895acfc400170e2dd5", // vanilla Magpul M-LOK 4.1 inch rail
    };

    private static readonly string[] Mlok41Tpls =
    {
        "0f73207955216d0d7f095ff8", // RailScales XOS MLOK (Long)
        "7f0284eba6fda800c17f7cdb", // RailScales XOS MLOK (Short)
        "99c41e1032b468759d406f4d", // Magpul M-LOK Rail Covers, Type 2 , Short (BLK) -- functional clone of XOS (Short)
        "de9b95f17c199774f126d90a", // Magpul M-LOK Rail Covers, Type 2 , Long (BLK) -- functional clone of XOS (Short)
        "684aec09ebd43dcda04c010a", // Magpul M-LOK Rail Covers, Type 2 , Short (FDE) -- functional clone of XOS (Short)
        "55ee87ae4e1ee843d3c1aa2b", // Magpul M-LOK Rail Covers, Type 2 , Long (FDE) -- functional clone of XOS (Short)
    };

    // Anything that whitelists the vanilla AN/PEQ-15, Daniel Defense 25mm ring mount, KAC vertical
    // foregrip, BT10 V8 Atlas Bipod, or NcSTAR MPR45 should also whitelist our KAC 11-Rib panel.
    private static readonly string[] XosPicatinnyReferenceTpls =
    {
        "544909bb4bdc2d6f028b4577", // vanilla L3Harris AN/PEQ-15 tactical device
        "6267c6396b642f77f56f5c1c", // vanilla Daniel Defense 25mm accessory ring mount ("25mm DD")
        "5c87ca002e221600114cb150", // vanilla KAC vertical foregrip ("KAC VFG")
        "6644920d49817dc7d505ca71", // BT10 V8 Atlas Bipod
        "5649a2464bdc2d91118b45a8", // vanilla NcSTAR MPR45 Backup mount
    };

    private static readonly string[] XosPicatinnyTpls =
    {
        "d87fde76c280d61c4be43a46", // KAC 11-Rib Picatinny Rail Cover
        "9890222fd5cf9dc6f06f2986", // KAC 5-Rib Picatinny Rail Cover
        "219139bd88a935fafe8a0656", // Magpul XTM Rail Cover Short (BLK) -- functional clone of KAC 5-Rib
        "68da4335bb10ab485ce375f1", // Magpul XTM Rail Cover Long (BLK) -- functional clone of KAC 5-Rib
        "2ae24de4753a467630f5ecbe", // Magpul XTM Rail Cover Short (FDE) -- functional clone of KAC 5-Rib
        "d6e0f16ec4d758552e39cd07", // Magpul XTM Rail Cover Short (OD) -- functional clone of KAC 5-Rib
        "ac74edede57f06789236f4c9", // Magpul XTM Rail Cover Long (FDE) -- functional clone of KAC 5-Rib
        "e621f9a5e1b03f21220aba6f", // Magpul XTM Rail Cover Long (OD) -- functional clone of KAC 5-Rib
    };

    public ArisakaOsmCompat(TemplateTable templateTable, ISptLogger<ArisakaOsmCompat> logger)
    {
        _templateTable = templateTable;
        _logger = logger;
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        int patched = 0;
        int slotsChecked = 0;
        int slotsMatched = 0;

        foreach (var (itemId, item) in _templateTable.Items)
        {
            var slots = item.Properties?.Slots;
            if (slots == null) continue;

            foreach (var slot in slots)
            {
                var filters = slot.Properties?.Filters;
                if (filters == null) continue;

                foreach (var slotFilter in filters)
                {
                    var filter = slotFilter.Filter;
                    if (filter == null) continue;
                    slotsChecked++;

                    bool isPicatinnyReferenceSlot = false;
                    foreach (var refTpl in PicatinnyReferenceTpls)
                    {
                        if (filter.Contains(refTpl)) { isPicatinnyReferenceSlot = true; break; }
                    }

                    bool isMlokReferenceSlot = false;
                    foreach (var refTpl in MlokReferenceTpls)
                    {
                        if (filter.Contains(refTpl)) { isMlokReferenceSlot = true; break; }
                    }

                    bool isMlok41ReferenceSlot = false;
                    foreach (var refTpl in Mlok41ReferenceTpls)
                    {
                        if (filter.Contains(refTpl)) { isMlok41ReferenceSlot = true; break; }
                    }

                    bool isXosPicatinnyReferenceSlot = false;
                    foreach (var refTpl in XosPicatinnyReferenceTpls)
                    {
                        if (filter.Contains(refTpl)) { isXosPicatinnyReferenceSlot = true; break; }
                    }

                    if (!isPicatinnyReferenceSlot && !isMlokReferenceSlot && !isMlok41ReferenceSlot && !isXosPicatinnyReferenceSlot) continue;
                    slotsMatched++;

                    if (isPicatinnyReferenceSlot)
                    {
                        foreach (var osmTpl in PicatinnyOsmTpls)
                        {
                            if (filter.Add(osmTpl))
                            {
                                patched++;
                                _logger.Debug($"[ArisakaOsmCompat] Added {osmTpl} to slot '{slot.Name}' for parent {itemId}");
                            }
                        }
                    }

                    if (isMlokReferenceSlot)
                    {
                        foreach (var osmTpl in MlokOsmTpls)
                        {
                            if (filter.Add(osmTpl))
                            {
                                patched++;
                                _logger.Debug($"[ArisakaOsmCompat] Added {osmTpl} to slot '{slot.Name}' for parent {itemId}");
                            }
                        }
                    }

                    if (isMlok41ReferenceSlot)
                    {
                        foreach (var osmTpl in Mlok41Tpls)
                        {
                            if (filter.Add(osmTpl))
                            {
                                patched++;
                                _logger.Debug($"[ArisakaOsmCompat] Added {osmTpl} to slot '{slot.Name}' for parent {itemId}");
                            }
                        }
                    }

                    if (isXosPicatinnyReferenceSlot)
                    {
                        foreach (var osmTpl in XosPicatinnyTpls)
                        {
                            if (filter.Add(osmTpl))
                            {
                                patched++;
                                _logger.Debug($"[ArisakaOsmCompat] Added {osmTpl} to slot '{slot.Name}' for parent {itemId}");
                            }
                        }
                    }
                }
            }
        }

        _logger.Debug($"[ArisakaOsmCompat] Checked {slotsChecked} slot filter(s), {slotsMatched} matched M600 thumbscrew mount / M-LOK rail / XOS Picatinny whitelist, {patched} addition(s) made.");
        return Task.CompletedTask;
    }
}

/// <summary>
/// Our Arisaka Finger Stop Picatinny clones the same vanilla BCM GUNFIGHTER MOD 3 M-LOK foregrip as
/// the M-LOK Finger Stop (for base stats), but that shared clone source is itself an M-LOK item -- if
/// this one also used addtoModSlots/ModSlotHelper propagation off that tpl, it would incorrectly
/// auto-whitelist onto M-LOK slots too (same bug already fixed on the OSM MLOK variant). So this item
/// has addtoModSlots disabled entirely and instead relies on this DB walk, targeting wherever the
/// vanilla BCM GUNFIGHTER MOD 3 vertical foregrip (the real Picatinny/rail-clamp version of the same
/// product) already fits. Runs very late (int.MaxValue) so every other mod's items already exist in the DB.
/// </summary>
[Injectable(InjectionType.Transient, int.MaxValue)]
public class ArisakaFsCompat : IOnLoad
{
    private readonly TemplateTable _templateTable;
    private readonly ISptLogger<ArisakaFsCompat> _logger;

    // Anything that whitelists the vanilla BCM GUNFIGHTER MOD 3 vertical foregrip (Picatinny) should also whitelist ours.
    private static readonly string[] ReferenceTpls =
    {
        "5c7fc87d2e221644f31c0298", // vanilla BCM GUNFIGHTER MOD 3 vertical foregrip (Picatinny)
    };

    // Our Arisaka Finger Stop Picatinny.
    private static readonly string[] FsTpls =
    {
        "cb3722d6c766170002bc2628", // Arisaka Finger Stop Picatinny (BLK)
        "d9dd28ce797081ede516a64a", // Arisaka Finger Stop Picatinny (FDE)
    };

    public ArisakaFsCompat(TemplateTable templateTable, ISptLogger<ArisakaFsCompat> logger)
    {
        _templateTable = templateTable;
        _logger = logger;
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        int patched = 0;
        int slotsChecked = 0;
        int slotsMatched = 0;

        foreach (var (itemId, item) in _templateTable.Items)
        {
            var slots = item.Properties?.Slots;
            if (slots == null) continue;

            foreach (var slot in slots)
            {
                var filters = slot.Properties?.Filters;
                if (filters == null) continue;

                foreach (var slotFilter in filters)
                {
                    var filter = slotFilter.Filter;
                    if (filter == null) continue;
                    slotsChecked++;

                    bool isReferenceSlot = false;
                    foreach (var refTpl in ReferenceTpls)
                    {
                        if (filter.Contains(refTpl)) { isReferenceSlot = true; break; }
                    }
                    if (!isReferenceSlot) continue;
                    slotsMatched++;

                    foreach (var fsTpl in FsTpls)
                    {
                        if (filter.Add(fsTpl))
                        {
                            patched++;
                            _logger.Debug($"[ArisakaFsCompat] Added {fsTpl} to slot '{slot.Name}' for parent {itemId}");
                        }
                    }
                }
            }
        }

        _logger.Debug($"[ArisakaFsCompat] Checked {slotsChecked} slot filter(s), {slotsMatched} matched BCM GUNFIGHTER MOD 3 (Picatinny) whitelist, {patched} addition(s) made.");
        return Task.CompletedTask;
    }
}

/// <summary>
/// Our Arisaka Finger Stop (Forward Mount) variants are decorative finger/barricade stops that sit
/// in a weapon's bipod position (mod_bipod, or a mod_mount_XXX slot on some M-LOK handguards) instead
/// of the standard foregrip slot, so they can be run alongside a separate foregrip further back on the
/// rail. Picatinny and M-LOK bipod attachment points are targeted separately using reference items:
/// the BT10 V8 Atlas Bipod (Picatinny), and for M-LOK, BOTH the EpicsAIO "Magpul M-Lok bipod" ("Magpul
/// BP", tpl 8af8534244b4e4189d53bec2) AND the vanilla "Magpul M-LOK Bipod Mount" (tpl
/// 671126b049e181972e0681fa) -- wherever any of those already fit, ours should too. These two M-LOK
/// references were confirmed (2026-07-13) to have ZERO slot overlap -- they're whitelisted on entirely
/// different, non-overlapping sets of handguards (64 EpicsAIO-associated slots vs. 50 genuinely
/// vanilla ones, sample-checked to confirm the vanilla set is legitimate M-LOK handguards like SA58,
/// MPX, HK416, AR-15/AR-10, MCX-SPEAR, not a false-positive match) -- so keeping both is additive, not
/// redundant: EpicsAIO users keep their existing 64-slot coverage unchanged, and vanilla-only users now
/// get real coverage across the 50 vanilla slots instead of none. (An earlier pass had rejected the
/// vanilla tpl as "the wrong item" without checking overlap -- it's not a substitute for the EpicsAIO
/// one, but it's a valid complement to it. See mwm-epicsaio-dependencies.md project memory.) Runs very
/// late (int.MaxValue) so every other mod's items already exist in the DB.
/// </summary>
[Injectable(InjectionType.Transient, int.MaxValue)]
public class ArisakaFsForwardCompat : IOnLoad
{
    private readonly TemplateTable _templateTable;
    private readonly ISptLogger<ArisakaFsForwardCompat> _logger;

    // Anything that whitelists the BT10 V8 Atlas Bipod (Picatinny) should also whitelist our Picatinny Forward Mount FS.
    private static readonly string[] PicatinnyReferenceTpls =
    {
        "6644920d49817dc7d505ca71", // BT10 V8 Atlas Bipod
    };

    private static readonly string[] PicatinnyFsTpls =
    {
        "51f7d3a08a9dc443a1e817ed", // Arisaka Finger Stop Picatinny (Forward Mount) (BLK)
        "f30ddd1f892fd5b5f078b7c1", // Arisaka Finger Stop Picatinny (Forward Mount) (FDE)
    };

    // Anything that whitelists either M-LOK bipod reference should also whitelist our M-LOK Forward
    // Mount FS. Zero slot overlap between the two (confirmed via DB scan), so both are kept together.
    private static readonly string[] MlokReferenceTpls =
    {
        "8af8534244b4e4189d53bec2", // Magpul M-Lok bipod ("Magpul BP", EpicsAIO)
        "671126b049e181972e0681fa", // Magpul M-LOK Bipod Mount (vanilla)
    };

    private static readonly string[] MlokFsTpls =
    {
        "bdc0939ffe23d398936ab6c1", // Arisaka Finger Stop MLOK (Forward Mount) (BLK)
        "e5b2a4e78c6ad51c9636e188", // Arisaka Finger Stop MLOK (Forward Mount) (FDE)
    };

    public ArisakaFsForwardCompat(TemplateTable templateTable, ISptLogger<ArisakaFsForwardCompat> logger)
    {
        _templateTable = templateTable;
        _logger = logger;
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        int patched = 0;
        int slotsChecked = 0;
        int slotsMatched = 0;

        foreach (var (itemId, item) in _templateTable.Items)
        {
            var slots = item.Properties?.Slots;
            if (slots == null) continue;

            foreach (var slot in slots)
            {
                var filters = slot.Properties?.Filters;
                if (filters == null) continue;

                foreach (var slotFilter in filters)
                {
                    var filter = slotFilter.Filter;
                    if (filter == null) continue;
                    slotsChecked++;

                    bool isPicatinnyReferenceSlot = false;
                    foreach (var refTpl in PicatinnyReferenceTpls)
                    {
                        if (filter.Contains(refTpl)) { isPicatinnyReferenceSlot = true; break; }
                    }

                    bool isMlokReferenceSlot = false;
                    foreach (var refTpl in MlokReferenceTpls)
                    {
                        if (filter.Contains(refTpl)) { isMlokReferenceSlot = true; break; }
                    }

                    if (!isPicatinnyReferenceSlot && !isMlokReferenceSlot) continue;
                    slotsMatched++;

                    if (isPicatinnyReferenceSlot)
                    {
                        foreach (var fsTpl in PicatinnyFsTpls)
                        {
                            if (filter.Add(fsTpl))
                            {
                                patched++;
                                _logger.Debug($"[ArisakaFsForwardCompat] Added {fsTpl} to slot '{slot.Name}' for parent {itemId}");
                            }
                        }
                    }

                    if (isMlokReferenceSlot)
                    {
                        foreach (var fsTpl in MlokFsTpls)
                        {
                            if (filter.Add(fsTpl))
                            {
                                patched++;
                                _logger.Debug($"[ArisakaFsForwardCompat] Added {fsTpl} to slot '{slot.Name}' for parent {itemId}");
                            }
                        }
                    }
                }
            }
        }

        _logger.Debug($"[ArisakaFsForwardCompat] Checked {slotsChecked} slot filter(s), {slotsMatched} matched BT10 Atlas / Magpul M-LOK Bipod Mount whitelist, {patched} addition(s) made.");
        return Task.CompletedTask;
    }
}

/// <summary>
/// The Zenit PT-1 Picatinny Stock and the KDG 1913 ACR Stock both clone the vanilla SIG Sauer Thin
/// Side-Folding Stock directly (itemTplToClone is the vanilla SIG stock tpl itself), so in theory
/// WTT's own ModSlotHelper propagation should whitelist them anywhere the vanilla SIG stock fits, no
/// extra work needed. In practice -- same story as CtrRisrStockCompat/RailScalesRiserCompat -- that
/// automatic propagation isn't reliably landing on every slot, so this runs its own independent DB
/// walk instead of trusting WTT's mechanism, targeting wherever the vanilla SIG Sauer Thin Side-Folding
/// Stock already fits: the SIG MPX/MCX, the MCX Spear, the MK16 JMAC RSA adapter, and the Uzi
/// Pro/Stormwerkz rear stock adapters. The ACR Stock was explicitly asked to be "the same in all
/// aspects" as the PT-1 stock, so it shares this same targeting rather than getting its own pass.
/// Runs very late (int.MaxValue) so every other mod's items already exist in the DB.
/// </summary>
[Injectable(InjectionType.Transient, int.MaxValue)]
public class SigStockCompat : IOnLoad
{
    private readonly TemplateTable _templateTable;
    private readonly ISptLogger<SigStockCompat> _logger;

    // Anything that whitelists the vanilla SIG Sauer Thin Side-Folding Stock should also whitelist ours.
    private static readonly string[] ReferenceTpls =
    {
        "5fbcc437d724d907e2077d5c", // vanilla SIG Sauer Thin Side-Folding Stock
    };

    // Our Zenit PT-1 Picatinny Stock, KDG 1913 ACR Stock (BLK + FDE), and SIG TSFS w/Riser.
    private static readonly string[] TargetTpls =
    {
        "3cef6bf10fe0616624480529", // Zenit PT-1 "Klassika" Picatinny Stock
        "5b7cb402657e8c2a723e6417", // Kinetic Development Group 1913 ACR Stock
        "c445f6abe23dc887e6adff8d", // Kinetic Development Group 1913 ACR Stock (FDE)
        "6fa394ebc653b3454e264ba3", // SIG Sauer Thin Side-Folding Stock w/Riser
    };

    public SigStockCompat(TemplateTable templateTable, ISptLogger<SigStockCompat> logger)
    {
        _templateTable = templateTable;
        _logger = logger;
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        int patched = 0;
        int slotsChecked = 0;
        int slotsMatched = 0;

        foreach (var (itemId, item) in _templateTable.Items)
        {
            var slots = item.Properties?.Slots;
            if (slots == null) continue;

            foreach (var slot in slots)
            {
                var filters = slot.Properties?.Filters;
                if (filters == null) continue;

                foreach (var slotFilter in filters)
                {
                    var filter = slotFilter.Filter;
                    if (filter == null) continue;
                    slotsChecked++;

                    bool isReferenceSlot = false;
                    foreach (var refTpl in ReferenceTpls)
                    {
                        if (filter.Contains(refTpl)) { isReferenceSlot = true; break; }
                    }
                    if (!isReferenceSlot) continue;
                    slotsMatched++;

                    foreach (var targetTpl in TargetTpls)
                    {
                        if (filter.Add(targetTpl))
                        {
                            patched++;
                            _logger.Debug($"[SigStockCompat] Added {targetTpl} to slot '{slot.Name}' for parent {itemId}");
                        }
                    }
                }
            }
        }

        _logger.Debug($"[SigStockCompat] Checked {slotsChecked} slot filter(s), {slotsMatched} matched SIG Sauer Thin Side-Folding Stock whitelist, {patched} addition(s) made.");
        return Task.CompletedTask;
    }
}

/// <summary>
/// Both our MP5 stock adapters (JMAC Customs' 1913 Stock Adapter, and HKParts' Buffer Tube Adapter)
/// clone the vanilla AR-15 Colt Carbine buffer tube for base properties (an adapter block, same
/// reasoning as our earlier PT-1 Base design), so they need addtoModSlots disabled entirely and their
/// own independent targeting -- otherwise they'd inherit the vanilla buffer tube's normal AR-15
/// footprint instead of the one narrow slot each is actually meant for. The three vanilla HK MP5-native
/// stocks (A2, A3 old model, End Cap) are ONLY ever whitelisted in one place in the whole DB: the
/// mod_stock slot on the standard and SD MP5 upper receivers (a DB check confirmed exactly those two
/// matches, nothing else -- notably NOT the MP5K, which has its own separate upper receiver tpl
/// entirely). So matching on those three vanilla tpls lands both adapters exactly where the user
/// asked -- the MP5 (and MP5SD, which shares the same stock-mount hardware), never the MP5K -- without
/// needing to hardcode item/slot IDs directly. Named for the shared function (MP5 stock-slot adapters)
/// rather than either brand, since it now covers two different real-world manufacturers. Runs very
/// late (int.MaxValue) so every other mod's items already exist in the DB.
/// </summary>
[Injectable(InjectionType.Transient, int.MaxValue)]
public class Mp5StockAdapterCompat : IOnLoad
{
    private readonly TemplateTable _templateTable;
    private readonly ISptLogger<Mp5StockAdapterCompat> _logger;

    // Anything that whitelists any of the vanilla HK MP5-native stocks should also whitelist ours.
    private static readonly string[] ReferenceTpls =
    {
        "5926d3c686f77410de68ebc8", // vanilla HK MP5 A2 stock
        "5926d40686f7740f152b6b7e", // vanilla HK MP5 A3 old model stock
        "5c07c9660db834001a66b588", // vanilla HK MP5 End Cap stock
    };

    // Our JMAC Customs MP5 1913 Stock Adapter and HKParts MP5 AR Buffer Tube Adapter.
    private static readonly string[] TargetTpls =
    {
        "0c921ceb4b0dcacf94d9a414", // JMAC Customs MP5 1913 Stock Adapter
        "cb908c916f8cad0afaf8e40d", // HKParts MP5 AR Buffer Tube Adapter
    };

    public Mp5StockAdapterCompat(TemplateTable templateTable, ISptLogger<Mp5StockAdapterCompat> logger)
    {
        _templateTable = templateTable;
        _logger = logger;
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        int patched = 0;
        int slotsChecked = 0;
        int slotsMatched = 0;

        foreach (var (itemId, item) in _templateTable.Items)
        {
            var slots = item.Properties?.Slots;
            if (slots == null) continue;

            foreach (var slot in slots)
            {
                var filters = slot.Properties?.Filters;
                if (filters == null) continue;

                foreach (var slotFilter in filters)
                {
                    var filter = slotFilter.Filter;
                    if (filter == null) continue;
                    slotsChecked++;

                    bool isReferenceSlot = false;
                    foreach (var refTpl in ReferenceTpls)
                    {
                        if (filter.Contains(refTpl)) { isReferenceSlot = true; break; }
                    }
                    if (!isReferenceSlot) continue;
                    slotsMatched++;

                    foreach (var targetTpl in TargetTpls)
                    {
                        if (filter.Add(targetTpl))
                        {
                            patched++;
                            _logger.Debug($"[Mp5StockAdapterCompat] Added {targetTpl} to slot '{slot.Name}' for parent {itemId}");
                        }
                    }
                }
            }
        }

        _logger.Debug($"[Mp5StockAdapterCompat] Checked {slotsChecked} slot filter(s), {slotsMatched} matched HK MP5-native stock whitelist, {patched} addition(s) made.");
        return Task.CompletedTask;
    }
}

/// <summary>
/// HRF Concepts RCM Magwell migrated 2026-07-17 from mod_charge_001 (B.A.D. Lever slot, narrow
/// coverage -- only M4A1/ADAR 2-15) to the same mod_pistol_grip nested-slot design as
/// [[mwm-ak-magwell-pistolgrip-slot]]'s AK Test Magwell, now that the build-screen reattachment bug
/// blocking that design is fixed (see mwm-assemble-circular-slot-bug project memory). No more
/// client-side animation-suppression patch needed either -- pistol grips have no bolt-catch-lever
/// rotation animation to fight, so MagwellBoltCatchLayerWeightPatch.cs was deleted outright.
///
/// The item now clones a real, broadly-compatible AR-15 pistol grip (Magpul MOE,
/// 55802f5d4bdc2dac148b458f) instead of a stat-only stub, so the standard WTT addtoModSlots broadcast
/// (modSlot: ["mod_pistol_grip"]) already makes it fit everywhere a Magpul MOE does -- no whitelist
/// Compat class needed for the top-level mounting itself, same as AK's CG101-clone approach.
///
/// This class only handles the NESTED grip-mount slot (mwm_hrf_grip_mount): mirrors a real M4A1's live
/// mod_pistol_grip Filter into each magwell color variant's own nested slot, so any AR-15/M4-pattern
/// grip -- vanilla or third-party -- fits on top of the magwell too, with no hardcoded tpl list.
/// </summary>
[Injectable(InjectionType.Transient, int.MaxValue)]
public class HrfRcmMagwellPistolGripCompat : IOnLoad
{
    private readonly TemplateTable _templateTable;
    private readonly ISptLogger<HrfRcmMagwellPistolGripCompat> _logger;

    // Colt M4A1 -- a real, standard AR-15/M4-pattern receiver. Its own mod_pistol_grip slot is the
    // live reference for "what fits an AR-15/M4 grip slot."
    private const string ReferenceReceiverTpl = "5447a9cd4bdc2dbd208b4567";
    private const string ReferenceSlotName = "mod_pistol_grip";

    private const string OurSlotName = "mwm_hrf_grip_mount";

    // Every color/weapon variant of the magwell needs its own nested slot mirrored -- the HK416,
    // SCAR-L, and MCX variants all use the exact same mwm_hrf_grip_mount slot name and accept the same
    // AR-15/M4 grips.
    private static readonly string[] TargetMagwellTpls =
    {
        "c984c5a469a4423033026950", // HRF Concepts RCM Milspec AR-15 Magwell
        "4fa49b740705d7bf5f3d0be6", // HRF Concepts RCM Milspec AR-15 Magwell (FDE)
        "3e58d049c95008d162b3fb6d", // HRF Concepts RCM HK416 Magwell
        "6881caf875719354716dcbc0", // HRF Concepts RCM HK416 Magwell (FDE)
        "3e87eb9c57ae18a5f4be25c0", // HRF Concepts RCM SCAR-L Magwell
        "cbbe5de32eac15d64ae03022", // HRF Concepts RCM SCAR-L Magwell (FDE)
        "d71ecb009595c1decbadd340", // HRF Concepts RCM MCX Magwell
        "906b0021d4381dab57fcc027", // HRF Concepts RCM MCX Magwell (FDE)
    };

    public HrfRcmMagwellPistolGripCompat(TemplateTable templateTable, ISptLogger<HrfRcmMagwellPistolGripCompat> logger)
    {
        _templateTable = templateTable;
        _logger = logger;
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var items = _templateTable.Items;

        // Local function: find the named Slot's Filter on the given item, if any.
        HashSet<MongoId>? FindFilter(string ownerTpl, string slotName)
        {
            if (!items.TryGetValue(ownerTpl, out var owner)) return null;
            var slots = owner.Properties?.Slots;
            if (slots == null) return null;

            foreach (var slot in slots)
            {
                if (!string.Equals(slot.Name, slotName, StringComparison.OrdinalIgnoreCase)) continue;
                var filters = slot.Properties?.Filters;
                if (filters == null) continue;
                foreach (var slotFilter in filters)
                {
                    if (slotFilter.Filter != null) return slotFilter.Filter;
                }
            }
            return null;
        }

        var referenceFilter = FindFilter(ReferenceReceiverTpl, ReferenceSlotName);
        if (referenceFilter == null)
        {
            _logger.Debug("[HrfRcmMagwellPistolGripCompat] Could not find the M4A1's mod_pistol_grip filter -- nothing to mirror.");
            return Task.CompletedTask;
        }

        foreach (var magwellTpl in TargetMagwellTpls)
        {
            var ourFilter = FindFilter(magwellTpl, OurSlotName);
            if (ourFilter == null)
            {
                _logger.Debug($"[HrfRcmMagwellPistolGripCompat] Could not find magwell {magwellTpl}'s own {OurSlotName} filter -- nothing to mirror.");
                continue;
            }

            int added = 0;
            int referenceCount = 0;
            foreach (var tpl in referenceFilter)
            {
                referenceCount++;
                if (tpl == magwellTpl) continue;
                if (ourFilter.Add(tpl))
                {
                    added++;
                }
            }

            _logger.Debug($"[HrfRcmMagwellPistolGripCompat] Mirrored M4A1's mod_pistol_grip filter ({referenceCount} tpl(s)) into {magwellTpl} -- {added} new addition(s).");
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// The HRF Concepts RCM Magwell no longer broadcasts via the standard addtoModSlots mechanism
/// (addtoModSlots: false in both color variants' JSON) -- cloning a real, broadly-compatible AR-15 grip
/// (Magpul MOE) would otherwise make it fit EVERY weapon that accepts that grip, but only a specific
/// subset of AR-15/M4-pattern receivers actually have the right geometry to accept the magwell itself
/// (user-confirmed 2026-07-17, geometry issues on the rest). This explicitly injects both magwell color
/// variants into ONLY the listed weapons' own mod_pistol_grip Filter -- the inclusion-list mirror of
/// AkTestMagwellExclusionsCompat's exclusion-list approach.
/// </summary>
[Injectable(InjectionType.Transient, int.MaxValue)]
public class HrfRcmMagwellWeaponWhitelistCompat : IOnLoad
{
    private readonly TemplateTable _templateTable;
    private readonly ISptLogger<HrfRcmMagwellWeaponWhitelistCompat> _logger;

    private const string PistolGripSlotName = "mod_pistol_grip";

    // Both color variants of the magwell.
    private static readonly string[] MagwellTpls =
    {
        "c984c5a469a4423033026950", // HRF Concepts RCM Milspec AR-15 Magwell
        "4fa49b740705d7bf5f3d0be6", // HRF Concepts RCM Milspec AR-15 Magwell (FDE)
    };

    // Weapons confirmed by the user to actually fit the magwell's geometry -- 2026-07-17.
    private static readonly (string Tpl, string Name)[] WhitelistedWeapons =
    {
        ("5c07c60e0db834002330051f", "ADAR-15"),
        ("68a639748e1fe612970728e9", "M16A1"),
        ("68a6399922b1e0bd360afe56", "M16A2"),
        ("5447a9cd4bdc2dbd208b4567", "M4A1"),
        ("4b81488c78c8a8ac7d37f9b9", "Mechanic's M4A1 .300 Blackout"),
        ("6761b213607f9a6f79017d40", "Singuard Arms \"Wages of Sin\" (Anodized Red)"),
        ("676b4e2ff185a450a0b300b4", "Singuard Arms \"Wages of Sin\""),
        ("676c3cac01023283e5f6a562", "Singuard Arms \"Wages of Sin\" (FDE)"),
        ("690ebacfc047a9a9f1a98782", "Colt RO991"),
    };

    public HrfRcmMagwellWeaponWhitelistCompat(TemplateTable templateTable, ISptLogger<HrfRcmMagwellWeaponWhitelistCompat> logger)
    {
        _templateTable = templateTable;
        _logger = logger;
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var items = _templateTable.Items;
        int addedTo = 0;

        foreach (var (weaponTpl, name) in WhitelistedWeapons)
        {
            if (!items.TryGetValue(weaponTpl, out var weapon))
            {
                _logger.Debug($"[HrfRcmMagwellWeaponWhitelistCompat] {name} ({weaponTpl}) not found -- skipping.");
                continue;
            }

            var slots = weapon.Properties?.Slots;
            if (slots == null) continue;

            bool added = false;
            foreach (var slot in slots)
            {
                if (!string.Equals(slot.Name, PistolGripSlotName, StringComparison.OrdinalIgnoreCase)) continue;
                var filters = slot.Properties?.Filters;
                if (filters == null) continue;
                foreach (var slotFilter in filters)
                {
                    if (slotFilter.Filter == null) continue;
                    foreach (var magwellTpl in MagwellTpls)
                    {
                        if (slotFilter.Filter.Add(magwellTpl))
                        {
                            added = true;
                        }
                    }
                }
            }

            if (added)
            {
                addedTo++;
                _logger.Debug($"[HrfRcmMagwellWeaponWhitelistCompat] Added magwell(s) to {name}'s mod_pistol_grip filter.");
            }
            else
            {
                _logger.Debug($"[HrfRcmMagwellWeaponWhitelistCompat] {name}'s mod_pistol_grip filter already had the magwell(s), or no mod_pistol_grip slot found.");
            }
        }

        _logger.Debug($"[HrfRcmMagwellWeaponWhitelistCompat] Whitelisted magwell(s) on {addedTo}/{WhitelistedWeapons.Length} target weapon(s).");
        return Task.CompletedTask;
    }
}

/// <summary>
/// The HK416's slightly different receiver geometry means the standard HRF Concepts RCM Magwell sits
/// in the wrong position on it, so it gets its own dedicated pair of items (HrfRcmMagwell_HK416.json /
/// _FDE.json) with their own Prefab bundles repositioned for this receiver specifically, rather than
/// being added to HrfRcmMagwellWeaponWhitelistCompat's shared list (which would cross-whitelist the
/// M4-pattern-tuned magwells onto the HK416 and vice versa). Same whitelist-injection mechanism as
/// HrfRcmMagwellWeaponWhitelistCompat, just scoped to these two tpls and this one weapon.
/// </summary>
[Injectable(InjectionType.Transient, int.MaxValue)]
public class HrfRcmMagwellHk416WeaponWhitelistCompat : IOnLoad
{
    private readonly TemplateTable _templateTable;
    private readonly ISptLogger<HrfRcmMagwellHk416WeaponWhitelistCompat> _logger;

    private const string PistolGripSlotName = "mod_pistol_grip";
    private const string Hk416Tpl = "5bb2475ed4351e00853264e3";

    // Both color variants of the HK416-specific magwell.
    private static readonly string[] MagwellTpls =
    {
        "3e58d049c95008d162b3fb6d", // HRF Concepts RCM HK416 Magwell
        "6881caf875719354716dcbc0", // HRF Concepts RCM HK416 Magwell (FDE)
    };

    public HrfRcmMagwellHk416WeaponWhitelistCompat(TemplateTable templateTable, ISptLogger<HrfRcmMagwellHk416WeaponWhitelistCompat> logger)
    {
        _templateTable = templateTable;
        _logger = logger;
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var items = _templateTable.Items;

        if (!items.TryGetValue(Hk416Tpl, out var weapon))
        {
            _logger.Debug($"[HrfRcmMagwellHk416WeaponWhitelistCompat] HK416A5 ({Hk416Tpl}) not found -- skipping.");
            return Task.CompletedTask;
        }

        var slots = weapon.Properties?.Slots;
        if (slots == null)
        {
            _logger.Debug("[HrfRcmMagwellHk416WeaponWhitelistCompat] HK416A5 has no Slots -- skipping.");
            return Task.CompletedTask;
        }

        bool added = false;
        foreach (var slot in slots)
        {
            if (!string.Equals(slot.Name, PistolGripSlotName, StringComparison.OrdinalIgnoreCase)) continue;
            var filters = slot.Properties?.Filters;
            if (filters == null) continue;
            foreach (var slotFilter in filters)
            {
                if (slotFilter.Filter == null) continue;
                foreach (var magwellTpl in MagwellTpls)
                {
                    if (slotFilter.Filter.Add(magwellTpl))
                    {
                        added = true;
                    }
                }
            }
        }

        _logger.Debug(added
            ? "[HrfRcmMagwellHk416WeaponWhitelistCompat] Added HK416 magwell(s) to HK416A5's mod_pistol_grip filter."
            : "[HrfRcmMagwellHk416WeaponWhitelistCompat] HK416A5's mod_pistol_grip filter already had the magwell(s), or no mod_pistol_grip slot found.");
        return Task.CompletedTask;
    }
}

/// <summary>
/// The FN SCAR-L's receiver geometry also differs enough from the M4-pattern-tuned magwell that it
/// needs its own dedicated pair of items (HrfRcmMagwell_SCARL.json / _FDE.json) with their own Prefab
/// bundles, same as the HK416 pair. Same whitelist-injection mechanism as
/// HrfRcmMagwellHk416WeaponWhitelistCompat, scoped to these two tpls and both SCAR-L color variants
/// (there's no separate "SCAR-L LB" weapon tpl in the item DB -- long-barrel SCAR-L builds use the same
/// weapon-root tpl with a different barrel attached, so whitelisting the weapon-root covers it too).
/// </summary>
[Injectable(InjectionType.Transient, int.MaxValue)]
public class HrfRcmMagwellScarLWeaponWhitelistCompat : IOnLoad
{
    private readonly TemplateTable _templateTable;
    private readonly ISptLogger<HrfRcmMagwellScarLWeaponWhitelistCompat> _logger;

    private const string PistolGripSlotName = "mod_pistol_grip";

    // FN SCAR-L, both color variants -- covers "SCAR-L LB" builds too since that's the same
    // weapon-root tpl with a longer barrel attached, not a separate item.
    private static readonly string[] ScarLTpls =
    {
        "6184055050224f204c1da540", // FN SCAR-L 5.56x45 assault rifle
        "618428466ef05c2ce828f218", // FN SCAR-L 5.56x45 assault rifle (FDE)
    };

    // Both color variants of the SCAR-L-specific magwell.
    private static readonly string[] MagwellTpls =
    {
        "3e87eb9c57ae18a5f4be25c0", // HRF Concepts RCM SCAR-L Magwell
        "cbbe5de32eac15d64ae03022", // HRF Concepts RCM SCAR-L Magwell (FDE)
    };

    public HrfRcmMagwellScarLWeaponWhitelistCompat(TemplateTable templateTable, ISptLogger<HrfRcmMagwellScarLWeaponWhitelistCompat> logger)
    {
        _templateTable = templateTable;
        _logger = logger;
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var items = _templateTable.Items;
        int addedTo = 0;

        foreach (var weaponTpl in ScarLTpls)
        {
            if (!items.TryGetValue(weaponTpl, out var weapon))
            {
                _logger.Debug($"[HrfRcmMagwellScarLWeaponWhitelistCompat] SCAR-L ({weaponTpl}) not found -- skipping.");
                continue;
            }

            var slots = weapon.Properties?.Slots;
            if (slots == null) continue;

            bool added = false;
            foreach (var slot in slots)
            {
                if (!string.Equals(slot.Name, PistolGripSlotName, StringComparison.OrdinalIgnoreCase)) continue;
                var filters = slot.Properties?.Filters;
                if (filters == null) continue;
                foreach (var slotFilter in filters)
                {
                    if (slotFilter.Filter == null) continue;
                    foreach (var magwellTpl in MagwellTpls)
                    {
                        if (slotFilter.Filter.Add(magwellTpl))
                        {
                            added = true;
                        }
                    }
                }
            }

            if (added)
            {
                addedTo++;
                _logger.Debug($"[HrfRcmMagwellScarLWeaponWhitelistCompat] Added SCAR-L magwell(s) to {weaponTpl}'s mod_pistol_grip filter.");
            }
            else
            {
                _logger.Debug($"[HrfRcmMagwellScarLWeaponWhitelistCompat] {weaponTpl}'s mod_pistol_grip filter already had the magwell(s), or no mod_pistol_grip slot found.");
            }
        }

        _logger.Debug($"[HrfRcmMagwellScarLWeaponWhitelistCompat] Whitelisted magwell(s) on {addedTo}/{ScarLTpls.Length} target weapon(s).");
        return Task.CompletedTask;
    }
}

/// <summary>
/// The SIG MCX's receiver geometry also differs enough from the M4-pattern-tuned magwell that it needs
/// its own dedicated pair of items (HrfRcmMagwell_MCX.json / _FDE.json) with their own Prefab bundles,
/// same as the HK416 and SCAR-L pairs. Covers both the vanilla .300 Blackout MCX and the separate 5.56
/// MCX added by EpicsAIO -- two genuinely different weapon-root tpls, unlike SCAR-L's LB variant which
/// shares one tpl.
/// </summary>
[Injectable(InjectionType.Transient, int.MaxValue)]
public class HrfRcmMagwellMcxWeaponWhitelistCompat : IOnLoad
{
    private readonly TemplateTable _templateTable;
    private readonly ISptLogger<HrfRcmMagwellMcxWeaponWhitelistCompat> _logger;

    private const string PistolGripSlotName = "mod_pistol_grip";

    // Vanilla .300 Blackout MCX, and the separate 5.56x45 MCX added by EpicsAIO.
    private static readonly (string Tpl, string Name)[] McxWeapons =
    {
        ("5fbcc1d9016cce60e8341ab3", "SIG MCX .300 Blackout (vanilla)"),
        ("72bbf927bf5b1d4a0837485b", "SIG MCX 5.56x45 (EpicsAIO)"),
    };

    // Both color variants of the MCX-specific magwell.
    private static readonly string[] MagwellTpls =
    {
        "d71ecb009595c1decbadd340", // HRF Concepts RCM MCX Magwell
        "906b0021d4381dab57fcc027", // HRF Concepts RCM MCX Magwell (FDE)
    };

    public HrfRcmMagwellMcxWeaponWhitelistCompat(TemplateTable templateTable, ISptLogger<HrfRcmMagwellMcxWeaponWhitelistCompat> logger)
    {
        _templateTable = templateTable;
        _logger = logger;
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var items = _templateTable.Items;
        int addedTo = 0;

        foreach (var (weaponTpl, name) in McxWeapons)
        {
            if (!items.TryGetValue(weaponTpl, out var weapon))
            {
                _logger.Debug($"[HrfRcmMagwellMcxWeaponWhitelistCompat] {name} ({weaponTpl}) not found -- skipping.");
                continue;
            }

            var slots = weapon.Properties?.Slots;
            if (slots == null) continue;

            bool added = false;
            foreach (var slot in slots)
            {
                if (!string.Equals(slot.Name, PistolGripSlotName, StringComparison.OrdinalIgnoreCase)) continue;
                var filters = slot.Properties?.Filters;
                if (filters == null) continue;
                foreach (var slotFilter in filters)
                {
                    if (slotFilter.Filter == null) continue;
                    foreach (var magwellTpl in MagwellTpls)
                    {
                        if (slotFilter.Filter.Add(magwellTpl))
                        {
                            added = true;
                        }
                    }
                }
            }

            if (added)
            {
                addedTo++;
                _logger.Debug($"[HrfRcmMagwellMcxWeaponWhitelistCompat] Added MCX magwell(s) to {name}'s mod_pistol_grip filter.");
            }
            else
            {
                _logger.Debug($"[HrfRcmMagwellMcxWeaponWhitelistCompat] {name}'s mod_pistol_grip filter already had the magwell(s), or no mod_pistol_grip slot found.");
            }
        }

        _logger.Debug($"[HrfRcmMagwellMcxWeaponWhitelistCompat] Whitelisted magwell(s) on {addedTo}/{McxWeapons.Length} target weapon(s).");
        return Task.CompletedTask;
    }
}

/// <summary>
/// AK Test Magwell mounts in the weapon's own mod_pistol_grip slot (CG101-style), hosting the real
/// pistol grip as a nested child in its own uniquely-named slot (mwm_akts_grip_mount) instead of
/// replacing it outright. This design was abandoned 2026-07-16 after hitting what looked like an
/// unresolved SPT build-screen bug: reattaching the exact grip instance just displaced from
/// mod_pistol_grip into the magwell's nested slot silently failed to persist. The magwell was
/// switched to a dedicated new slot (mod_magazine_new) as a workaround at the time.
///
/// That bug has since been root-caused and fixed client-side: it was a real deadlock in vanilla EFT's
/// own weapon-build-screen Assemble algorithm (GClass3470.smethod_0), not anything specific to this
/// slot or this item -- see mwm-assemble-circular-slot-bug project memory. AssembleCircularSlotFixPatch
/// in the client bootstrap project now handles it generally, so this design is reinstated 2026-07-17.
///
/// This mirrors a real AK-pattern receiver's own live mod_pistol_grip Filter into our magwell's child
/// slot at load time, so any pistol grip that fits a real AK -- vanilla or from any other installed
/// mod -- automatically fits on top of the magwell too, with no hardcoded tpl list to maintain.
/// </summary>
[Injectable(InjectionType.Transient, int.MaxValue)]
public class AkTestMagwellPistolGripCompat : IOnLoad
{
    private readonly TemplateTable _templateTable;
    private readonly ISptLogger<AkTestMagwellPistolGripCompat> _logger;

    // Kalashnikov AK-74M -- a real, standard AK-pattern receiver. Its own mod_pistol_grip slot is
    // the live reference for "what fits an AK pistol grip slot."
    private const string ReferenceReceiverTpl = "5ac4cd105acfc40016339859";
    private const string ReferenceSlotName = "mod_pistol_grip";

    // AK Test Magwell -- never let it whitelist itself. Its own child slot uses a name confirmed
    // globally unique across vanilla items and every installed mod (2026-07-16) -- an earlier attempt
    // reused CG101's own "mod_pistolgrip_000" naming, which turned out to also be used by the vanilla
    // M60 family and WTT-Armory's MSBS as their own top-level grip slot name.
    private const string OurMagwellTpl = "352765af2bdf69fc66bae7d1";
    private const string OurSlotName = "mwm_akts_grip_mount";

    public AkTestMagwellPistolGripCompat(TemplateTable templateTable, ISptLogger<AkTestMagwellPistolGripCompat> logger)
    {
        _templateTable = templateTable;
        _logger = logger;
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var items = _templateTable.Items;

        // Local function: find the named Slot's Filter on the given item, if any. Left as a local
        // function (not a static method) so its parameter type is inferred from the closure instead
        // of having to name TemplateItem directly -- this file has no `using` for that namespace.
        HashSet<MongoId>? FindFilter(string ownerTpl, string slotName)
        {
            if (!items.TryGetValue(ownerTpl, out var owner)) return null;
            var slots = owner.Properties?.Slots;
            if (slots == null) return null;

            foreach (var slot in slots)
            {
                if (!string.Equals(slot.Name, slotName, StringComparison.OrdinalIgnoreCase)) continue;
                var filters = slot.Properties?.Filters;
                if (filters == null) continue;
                foreach (var slotFilter in filters)
                {
                    if (slotFilter.Filter != null) return slotFilter.Filter;
                }
            }
            return null;
        }

        var referenceFilter = FindFilter(ReferenceReceiverTpl, ReferenceSlotName);
        if (referenceFilter == null)
        {
            _logger.Debug("[AkTestMagwellPistolGripCompat] Could not find the AK-74M's mod_pistol_grip filter -- nothing to mirror.");
            return Task.CompletedTask;
        }

        var ourFilter = FindFilter(OurMagwellTpl, OurSlotName);
        if (ourFilter == null)
        {
            _logger.Debug($"[AkTestMagwellPistolGripCompat] Could not find AK Test Magwell's own {OurSlotName} filter -- nothing to mirror.");
            return Task.CompletedTask;
        }

        int added = 0;
        int referenceCount = 0;
        foreach (var tpl in referenceFilter)
        {
            referenceCount++;
            if (tpl == OurMagwellTpl) continue;
            if (ourFilter.Add(tpl))
            {
                added++;
            }
        }

        _logger.Debug($"[AkTestMagwellPistolGripCompat] Mirrored AK-74M's mod_pistol_grip filter ({referenceCount} tpl(s)) -- {added} new addition(s).");
        return Task.CompletedTask;
    }
}

/// <summary>
/// AK Test Magwell mounts wherever CG101 (the item it clones) already fits, via WTT-ServerCommonLib's
/// own addtoModSlots broadcast -- see mwm-modslot-broadcast-mechanism project memory. That broadcast
/// has no per-weapon opt-out, so a handful of AK-pattern-ish receivers the magwell is technically
/// compatible with turn out to have real geometry/clipping issues in-game (user-confirmed 2026-07-17).
/// This runs after that broadcast (same int.MaxValue late-load tier as every other Compat class here)
/// and strips the magwell's own tpl back out of each listed weapon's mod_pistol_grip Filter.
/// </summary>
[Injectable(InjectionType.Transient, int.MaxValue)]
public class AkTestMagwellExclusionsCompat : IOnLoad
{
    private readonly TemplateTable _templateTable;
    private readonly ISptLogger<AkTestMagwellExclusionsCompat> _logger;

    private const string OurMagwellTpl = "352765af2bdf69fc66bae7d1";
    private const string PistolGripSlotName = "mod_pistol_grip";

    // Weapons where the magwell is otherwise moddable but doesn't fit visually due to receiver
    // geometry -- user-confirmed 2026-07-17.
    private static readonly (string Tpl, string Name)[] ExcludedWeapons =
    {
        ("59984ab886f7743e98271174", "PP-19-01 Vityaz"),
        ("59f9cabd86f7743a10721f46", "Saiga-9"),
        ("628b5638ad252a16da6dd245", "SAG AK-545"),
        ("628b9c37a733087d0d7fe84b", "SAG AK-545 Short"),
        ("576165642459773c7a400233", "Saiga-12k (Izhmash 10rd)"),
        ("674fe9a75e51f1c47c04ec23", "Saiga-12k (Kiba full-auto)"),
        ("678fe4a4906c7bd23722c71f", "AN-94"),
        ("679a6a534f3d279c99b135b9", "AN-94 \"Kirov's Custom\""),
    };

    public AkTestMagwellExclusionsCompat(TemplateTable templateTable, ISptLogger<AkTestMagwellExclusionsCompat> logger)
    {
        _templateTable = templateTable;
        _logger = logger;
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var items = _templateTable.Items;
        int removedFrom = 0;

        foreach (var (tpl, name) in ExcludedWeapons)
        {
            if (!items.TryGetValue(tpl, out var weapon))
            {
                _logger.Debug($"[AkTestMagwellExclusionsCompat] {name} ({tpl}) not found -- skipping.");
                continue;
            }

            var slots = weapon.Properties?.Slots;
            if (slots == null) continue;

            bool removed = false;
            foreach (var slot in slots)
            {
                if (!string.Equals(slot.Name, PistolGripSlotName, StringComparison.OrdinalIgnoreCase)) continue;
                var filters = slot.Properties?.Filters;
                if (filters == null) continue;
                foreach (var slotFilter in filters)
                {
                    if (slotFilter.Filter != null && slotFilter.Filter.Remove(OurMagwellTpl))
                    {
                        removed = true;
                    }
                }
            }

            if (removed)
            {
                removedFrom++;
                _logger.Debug($"[AkTestMagwellExclusionsCompat] Removed AK Test Magwell from {name}'s mod_pistol_grip filter (geometry incompatibility).");
            }
            else
            {
                _logger.Debug($"[AkTestMagwellExclusionsCompat] {name}'s mod_pistol_grip filter didn't contain the magwell -- nothing to remove.");
            }
        }

        _logger.Debug($"[AkTestMagwellExclusionsCompat] Excluded AK Test Magwell from {removedFrom}/{ExcludedWeapons.Length} target weapon(s).");
        return Task.CompletedTask;
    }
}

/// <summary>
/// Every optic with a built-in magnifier -- vanilla HHS-1 (both colors) and every EpicsAIO
/// HHS-1-clone combo sight in Magnifiers.json (both the G33-magnifier and Unity-FTC-offset-mount
/// variants, per the user's "all of them" 2026-07-18) -- shows red/incompatible against rear iron
/// sights. Two separate root causes, both in data we don't own and won't edit directly:
/// (1) EpicsAIO's own 9 combo items explicitly declare ConflictingItems against a hardcoded list of
/// mostly-rear-sight tpls (plus 2 legitimate Leupold D-EVO scope entries, which are NOT rear sights
/// and are deliberately left untouched -- out of scope for this fix). (2) Vanilla rear sights (e.g.
/// AR-15 Colt A2) declare ConflictingItems against the two vanilla HHS-1 tpls directly -- HHS-1's own
/// ConflictingItems is empty -- and since the in-game compatibility check is bidirectional, this
/// conflicts the vanilla HHS-1 items even though they declare nothing themselves.
///
/// Fixes both directions via our own DB walk, purely additive/subtractive on ConflictingItems sets,
/// no vanilla/EpicsAIO/WTT file edits: strips every rear-sight tpl from each of our 11 target optics'
/// own ConflictingItems, AND strips every one of our 11 optic tpls from every rear sight's own
/// ConflictingItems. The 113-entry rear-sight list is every vanilla + EpicsAIO + WTT-Armory +
/// WTT-ContentBackport item sharing the vanilla "front/rear sight" parent category
/// (55818ac54bdc2d5b648b456e), name-filtered down to rear entries only -- front sights under that
/// same parent were excluded on purpose, they were never part of the user's ask and don't appear to
/// conflict with these optics in the data. Runs very late (int.MaxValue) so every other mod's items
/// already exist in the DB.
/// </summary>
[Injectable(InjectionType.Transient, int.MaxValue)]
public class MagnifiedOpticRearSightCompat : IOnLoad
{
    private readonly TemplateTable _templateTable;
    private readonly ISptLogger<MagnifiedOpticRearSightCompat> _logger;

    // Every optic with a built-in magnifier: vanilla HHS-1 (both colors) + all 9 EpicsAIO
    // HHS-1-clone combos (Magnifiers.json) -- both the G33-magnifier and Unity-FTC-offset-mount
    // variants.
    private static readonly string[] MagnifiedOpticTpls =
    {
        "5c07dd120db834001c39092d", // vanilla HHS-1 (Black)
        "5c0a2cec0db834001b7ce47d", // vanilla HHS-1 (Tan)
        "7ca248803b89788492384ba2", // EpicsAIO Eotech EXPS3 w/ G33
        "41e97223a2ad6374a096ae14", // EpicsAIO Eotech EXPS3 w/ Unity FTC (FDE)
        "2e31bdb165e6f6f7ac19b0d8", // EpicsAIO Leupold LCO w/ G33
        "8d9e9ff3020f92eb5fc46680", // EpicsAIO Leupold LCO w/ Unity FTC (Black)
        "22345a1c542354f86796efda", // EpicsAIO Vortex UH-1 w/ G33
        "a5c171496c1870f205a3c972", // EpicsAIO Vortex UH-1 w/ Unity FTC (Black)
        "0377e32842c4024a423c472e", // EpicsAIO SIG Sauer ROMEO8T w/ G33
        "81fb194d053a4ccedce3782d", // EpicsAIO SIG Sauer ROMEO8T w/ Unity FTC (Black)
        "08940f5c853aa6acf4049bc2", // EpicsAIO Aimpoint Micro T-1 w/ Unity FTC (Black)
    };

    // Every rear iron sight tpl across vanilla + every installed mod (see class doc comment for how
    // this list was derived). Front sights sharing the same parent category are deliberately excluded.
    private static readonly string[] RearSightTpls =
    {
        // Vanilla rear iron sights (74)
        "55d5f46a4bdc2d1b198b4567", // AR-15 Colt A2 rear sight
        "56083e1b4bdc2dc8488b4572", // SV-98 rear sight
        "5649b0544bdc2d1b2b8b458a", // AK-74 rear sight
        "5649d9a14bdc2d79388b4580", // AK Taktika Tula TT01 rear sight rail
        "56d5a77ed2720b90418b4568", // P226 rear sight
        "56ea7293d2720b8d4b8b45ba", // P226 Sight Mount 220-239 rear sight bearing
        "574db213245977459a2f3f5d", // SKS rear sight
        "57838e1b2459774a256959b1", // VSS rear sight
        "57a9b9ce2459770ee926038d", // SOK-12 rear sight
        "57c44e7b2459772d28133248", // AS VAL rear sight
        "58272b842459774abc128d50", // SOK-12 CSS rear sight rail mount
        "5894a81786f77427140b8347", // MPX flip-up rear sight
        "5926d2be86f774134d668e4e", // HK MP5 Drum rear sight
        "599860e986f7743bb57573a6", // PP-19-01 Vityaz rear sight
        "59d650cf86f7741b846413a4", // AKM rear sight
        "59e8977386f77415a553c453", // VPO-209 rear sight
        "5a0eb980fcdbcb001a3b00a6", // AKMB system rear sight
        "5a0ed824fcdbcb0176308b0d", // AKMP system rear sight device
        "5a6f5d528dc32e00094b97d9", // Glock rear sight
        "5a71e0fb8dc32e00094b97f2", // Glock ZEV Tech rear sight
        "5a7d9122159bd4001438dbf4", // Glock Dead Ringer Snake Eye rear sight
        "5a7d912f159bd400165484f3", // Glock TruGlo TFX rear sight
        "5aba637ad8ce87001773e17f", // APS rear sight
        "5aba639ed8ce8700182ece67", // APB rear sight
        "5abcbb20d8ce87001773e258", // M14 SA Enlarged Military Aperture rear sight
        "5ac72e475acfc400180ae6fe", // AK-74M rear sight
        "5ac733a45acfc400192630e2", // AK-105 rear sight
        "5ae099925acfc4001a5fc7b3", // Mosin Rifle rear sight
        "5ae30bad5acfc400185c2dc4", // AR-15 rear sight carry handle
        "5b0bc22d5acfc47a8607f085", // SA58 Holland Type rear sight
        "5ba26b17d4351e00367f9bdd", // HK MP7 flip-up rear sight
        "5bb20e49d4351e3bac1212de", // HK 416A5 flip-up rear sight
        "5bc09a18d4351e003562b68e", // Magpul MBUS Gen2 flip-up rear sight
        "5beec9450db83400970084fd", // RPK-16 rear sight base
        "5bf3f59f0db834001a6fa060", // RPK-16 rear sight
        "5bfd4c980db834001b73449d", // Mosin Rifle carbine rear sight
        "5c05295e0db834001a66acbb", // Trijicon ACOG backup rear sight
        "5c07b3850db834002330045b", // P226 Meprolight TRU-DOT Night rear sight
        "5c1780312e221602b66cc189", // KAC Folding rear sight
        "5c18b9192e2216398b5a8104", // Magpul MBUS Gen2 flip-up rear sight (FDE)
        "5c471b7e2e2216152006e46c", // SVDS rear sight
        "5c503b1c2e221602b21d6e9d", // VPO-101 rear sight
        "5cadd940ae9215051e1c2316", // M9A3 rear sight
        "5cadd954ae921500103bb3c2", // M9A3 Sight Mount rear sight rail
        "5caf1691ae92152ac412efb9", // ASh-12 rear sight carry handle
        "5d3eb4aba4b93650d64e497d", // FN Five-seveN MK2 rear sight
        "5de8fb539f98ac2bc659513a", // MP9 rear sight
        "5dfa3d7ac41b2312ea33362a", // KAC Folding Micro rear sight
        "5e81ee4dcb2b95385c177582", // M1911A1 rear sight
        "5e87114fe2db31558c75a120", // Mossberg 590 Ghost Ring rear sight
        "5f3e7897ddc4f03b010e204a", // M45A1 Novak Lomount rear sight
        "5fb6564947ce63734e3fa1da", // KRISS Defiance low profile flip-up rear sight
        "5fc0fa957283c4046c58147e", // MCX flip-up rear sight
        "602293f023506e50807090cb", // PL-15 extended rear sight
        "60229948cacb6b0506369e27", // PL-15 rear sight
        "61817865d3a39d50044c13a4", // FN SCAR flip-up rear sight
        "6194f2912d2c397d6600348d", // HK USP rear sight
        "6194f2df645b5d229654ad77", // HK USP Tactical rear sight
        "619f4cee4c58466fe1228435", // Chiappa Rhino rear sight
        "619f4f8c4c58466fe1228439", // Chiappa Rhino Red Fiber Optic rear sight
        "6231670f0b8aa5472d060095", // HK G36 rear sight
        "625ebcef6f53af4aa66b44dc", // Benelli M3 Ghost Ring rear sight
        "628a7b23b0f75035732dd565", // AK RD Enhanced V2 Rear Sight
        "628b9471078f94059a4b9bfb", // AK-545 rear sight
        "630765cb962d0247b029dc45", // Glock 19X rear sight
        "63c6adcfb4ba094317063742", // PM rear sight
        "64119d90dcf48d656f0aa275", // SVT-40 rear sight
        "6492fb8253acae0af00a29b6", // PK rear sight
        "649ec2cec93611967b03495e", // AK-12 rear sight
        "649ec2da59cbb3c813042dca", // AK-12 rear sight mount
        "6513f153e63f29908d0ffaba", // RPD rear sight
        "6601265f98a610c1aa0ea637", // M60 rear sight
        "668fe5e1800f0244f9036e46", // Desert Eagle rear sight
        "674fe8cf4472d471fb0f07df", // Chaos HK Style rear sight

        // EpicsAIO rear iron sights (3)
        "ef29cf641c7f772a1c0237ad", // Glock 19X Suppressor Height rear sight
        "50ea224769f3c313d2aaac40", // Glock 19 rear sight
        "e8d92f66b5cf4553048c5f5b", // Matech Folding rear sight (Epics AIO)

        // WTT-Armory rear iron sights (30)
        "66ba297b859e57cf91e38a5d", // Magpul MBUS Gen3 flip-up rear sight
        "66ba29d44744d98a4df0073b", // Magpul MBUS Gen3 flip-up rear sight
        "69555c7739fb0a1a6f0ad132", // A-545 Rear Sight
        "68609cdd6100b1899111ff9f", // AK series CQB rear sight
        "68c23b3d4a286357245eb641", // Simonov AG-042/043 rear sight
        "687fb5224b7bf5a5702898ee", // CZ Scorpion EVO 3 rear sight
        "696801b85966cfdf3708c746", // FN F2000 Rear flip-up sight
        "69680a1b5966cfdf3708c756", // FN F2000S Rear flip-up sight
        "677ca78263bc534f0b830a42", // LAR Grizzly Tritium rear sight (Green)
        "677c9e554dde113597355fad", // LAR Grizzly rear sight
        "677c9e4595c08963e941a146", // LAR Grizzly Tritium rear sight (Red)
        "664a5ff61c4610dc01e84917", // HK 417 Drum rear sight
        "6937485fb8feebf8aff61b85", // Barrett M107A1 folding rear sight
        "66e887cf09ede6c7c42def11", // Zastava M76 rear sight
        "691226c24afdbfd9b59f5092", // Magpul Masada flip-up rear sight
        "691226dc484b05df2aa39d50", // Magpul Masada flip-up rear sight (FDE)
        "68b7f7317385935fb8a06488", // HK MK23 rear sight
        "68b9ce4e6ac219f4b866616d", // HK MK23 rear sight bearing
        "68b7f777a2a0395b35545877", // HK MK23 tritium rear sight
        "6683957f2e516f6146af483d", // Patriot rear sight carry handle
        "684b271ce72d12cf50b2a0dd", // TTI Pit Viper rear sight
        "684b66d1a91f8b28868236a4", // TTI Pit Viper rear sight (Stainless)
        "665fe63876838d24231faa26", // Springfield Armory DS Prodigy Rear Pistol Sight
        "68bf450e899a7ddb21e01d0f", // PSA 5.7 Rock rear sight
        "692f704e5371aadbbb0ada20", // Ruger 57 Rear Sight
        "69777201f57d45a2ec02aafe", // SA-1 rear sight
        "6846039a06cd8ba4da4b764c", // Staccato XC rear sight with sight mount cap
        "684374b008d0b9c0aa535a62", // HK UCP rear sight
        "68678220f740d20e8625095f", // SR-2 Udav tritium rear pistol sight
        "66a47ed3f97061a20b99f188", // IWI Tavor X95 rear sight

        // WTT-ContentBackport rear iron sights (6)
        "68a63d3c8e977b40b2032286", // MaTech BUIS rear sight
        "67c5428ab032bbdb530201c4", // MXLR standard rear sight
        "680b884f1a335f66240ecde7", // HK G36 KAC 600m Rear Sight Rail
        "68916765fa628c6b9f0ed6f6", // AK-308 rear sight base
        "68caad70269e10396503ad00", // A.R.M.S. #40 Flip-Up Rear Sight
        "67c5422726265106dd0697a2", // M1895/MXLR XS Marlin Ghost Ring rear sight
    };

    public MagnifiedOpticRearSightCompat(TemplateTable templateTable, ISptLogger<MagnifiedOpticRearSightCompat> logger)
    {
        _templateTable = templateTable;
        _logger = logger;
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var items = _templateTable.Items;
        int magnifiersPatched = 0;
        int rearSightsPatched = 0;

        foreach (var magnifierTpl in MagnifiedOpticTpls)
        {
            if (!items.TryGetValue(magnifierTpl, out var item)) continue;
            var conflicts = item.Properties?.ConflictingItems;
            if (conflicts == null) continue;

            int removed = 0;
            foreach (var rearSightTpl in RearSightTpls)
            {
                if (conflicts.Remove(rearSightTpl)) removed++;
            }
            if (removed > 0)
            {
                magnifiersPatched++;
                _logger.Debug($"[MagnifiedOpticRearSightCompat] Removed {removed} rear-sight conflict(s) from magnified optic {magnifierTpl}.");
            }
        }

        foreach (var rearSightTpl in RearSightTpls)
        {
            if (!items.TryGetValue(rearSightTpl, out var item)) continue;
            var conflicts = item.Properties?.ConflictingItems;
            if (conflicts == null) continue;

            int removed = 0;
            foreach (var magnifierTpl in MagnifiedOpticTpls)
            {
                if (conflicts.Remove(magnifierTpl)) removed++;
            }
            if (removed > 0)
            {
                rearSightsPatched++;
                _logger.Debug($"[MagnifiedOpticRearSightCompat] Removed {removed} magnified-optic conflict(s) from rear sight {rearSightTpl}.");
            }
        }

        _logger.Debug($"[MagnifiedOpticRearSightCompat] Done: {magnifiersPatched}/{MagnifiedOpticTpls.Length} magnified optics patched, {rearSightsPatched}/{RearSightTpls.Length} rear sights patched.");
        return Task.CompletedTask;
    }
}

/// <summary>
/// EpicsAIO's Precision Reflex Carry Handle Mount "Gooseneck" (Misc_Mounts.json) already attaches to
/// the vanilla AR-15 detachable carry handle. User asked (2026-07-18) for it to also fit the two
/// WTT-ContentBackport M16A1/M16A2 upper receivers, in the same mod_scope slot the WTT-ContentBackport
/// "AR-15 ProMag Delta Style scope mount" already occupies there -- both items serve the same
/// carry-handle-replacement-rail role on these fixed-carry-handle uppers. Scoped narrowly to just
/// these two receivers (not a broad "wherever the Delta rail fits" mirror), per the user's explicit
/// ask. Runs very late (int.MaxValue) so both other mods' items already exist in the DB.
/// </summary>
[Injectable(InjectionType.Transient, int.MaxValue)]
public class GooseneckM16UpperCompat : IOnLoad
{
    private readonly TemplateTable _templateTable;
    private readonly ISptLogger<GooseneckM16UpperCompat> _logger;

    private const string GooseneckTpl = "66713838ca123f9df8e7584e"; // EpicsAIO Precision Reflex Carry Handle Mount "Gooseneck"
    private const string ScopeSlotName = "mod_scope";

    // WTT-ContentBackport M16A1/M16A2 upper receivers.
    private static readonly (string Tpl, string Name)[] TargetReceivers =
    {
        ("68a63b90e1fb670852024659", "M16A1E1 5.56x45 upper receiver"),
        ("68a63b9b8e1fe612970728f7", "M16A2 5.56x45 upper receiver"),
    };

    public GooseneckM16UpperCompat(TemplateTable templateTable, ISptLogger<GooseneckM16UpperCompat> logger)
    {
        _templateTable = templateTable;
        _logger = logger;
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var items = _templateTable.Items;
        int addedTo = 0;

        foreach (var (tpl, name) in TargetReceivers)
        {
            if (!items.TryGetValue(tpl, out var receiver))
            {
                _logger.Debug($"[GooseneckM16UpperCompat] {name} ({tpl}) not found -- skipping.");
                continue;
            }

            var slots = receiver.Properties?.Slots;
            if (slots == null) continue;

            bool added = false;
            foreach (var slot in slots)
            {
                if (!string.Equals(slot.Name, ScopeSlotName, StringComparison.OrdinalIgnoreCase)) continue;
                var filters = slot.Properties?.Filters;
                if (filters == null) continue;
                foreach (var slotFilter in filters)
                {
                    if (slotFilter.Filter != null && slotFilter.Filter.Add(GooseneckTpl))
                    {
                        added = true;
                    }
                }
            }

            if (added)
            {
                addedTo++;
                _logger.Debug($"[GooseneckM16UpperCompat] Added Gooseneck to {name}'s mod_scope filter.");
            }
            else
            {
                _logger.Debug($"[GooseneckM16UpperCompat] {name}'s mod_scope filter already had the Gooseneck, or no mod_scope slot found.");
            }
        }

        _logger.Debug($"[GooseneckM16UpperCompat] Whitelisted Gooseneck on {addedTo}/{TargetReceivers.Length} target receiver(s).");
        return Task.CompletedTask;
    }
}

/// <summary>
/// The Irregular Defense Front Ironsight Riser (BLK/FDE) mounts in a weapon's real mod_sight_front
/// slot (CG101 nested-hosting shape, applied to front sights instead of grips/optics) and hosts the
/// real front sight in its own nested mod_sight_front child slot -- same slot NAME reused at both
/// levels, proven safe by the magwell work (see mwm-magwell-pistolgrip-design project memory; an
/// earlier investigation ruled out same-name-collision as ever having been the actual bug there).
///
/// "Picatinny front ironsight" is defined as a base 9-tpl set that vanilla AR-15/AR-10/HK416/MDR
/// handguards use identically for their own mod_sight_front filter (KAC Folding, KAC Folding Micro,
/// Magpul MBUS Gen2 BLK/FDE, MPX flip-up, HK MP7 flip-up, KRISS Defiance low-profile flip-up, MCX
/// flip-up, Leapers UTG Low Profile A2) -- confirmed via a DB-wide frequency scan (2026-07-18): these 9
/// cluster tightly at 161-215 distinct owner items each, while every platform-specific front sight
/// (Glock, HK USP, P226, etc.) sits at 1-26 owners. Reference item: AR-15 Daniel Defense RIS II 9.5
/// handguard (Black), one of 35 vanilla items whose mod_sight_front filter is EXACTLY these 9 tpls (no
/// platform-specific extras mixed in) -- both classes below key off this single item for that base set.
///
/// PLUS 3 extra tpls added explicitly (2026-07-18, user request) on top of the reference mirror: Magpul
/// MBUS Gen3 flip-up front sight (both variants) and Scalarworks PEAK front sight -- real generic AR
/// BUIS products that were initially excluded for having much lower cross-reference counts in this data
/// set (26 and 13 owners respectively, vs. 161-215 for the base 9), but are being folded in directly
/// here rather than waiting for other mods' data to catch up. These don't come from the reference
/// handguard's own filter (it doesn't list them), so they're added as a flat addition alongside the
/// mirrored set, in both directions (hosted by our riser, and treated as trigger tpls for where our
/// riser fits).
/// </summary>
[Injectable(InjectionType.Transient, int.MaxValue)]
public class FsRiserFrontSightHostCompat : IOnLoad
{
    private readonly TemplateTable _templateTable;
    private readonly ISptLogger<FsRiserFrontSightHostCompat> _logger;

    private const string ReferenceHandguardTpl = "588b56d02459771481110ae2"; // AR-15 Daniel Defense RIS II 9.5 handguard (Black)
    private const string FrontSightSlotName = "mod_sight_front";

    // Our two color variants.
    private static readonly string[] OurRiserTpls =
    {
        "5485b2419854d122298ef0a1", // Irregular Defense Front Ironsight Riser (BLK)
        "aaeeaf7b054119901161401f", // Irregular Defense Front Ironsight Riser (FDE)
    };

    // Extra front sights added on top of the reference handguard's own 9-tpl mirror -- see class doc
    // comment.
    private static readonly string[] ExtraHostedTpls =
    {
        "48dd8b7edd39c0ab164435ad", // Scalarworks PEAK front sight (WTT-Armory)
        "66ba2a6be18ed84221edd72d", // Magpul MBUS Gen3 flip-up front sight (WTT-Armory)
        "66ba2a7583204e25695233c0", // Magpul MBUS Gen3 flip-up front sight (WTT-Armory)
    };

    public FsRiserFrontSightHostCompat(TemplateTable templateTable, ISptLogger<FsRiserFrontSightHostCompat> logger)
    {
        _templateTable = templateTable;
        _logger = logger;
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var items = _templateTable.Items;

        HashSet<MongoId>? FindFilter(string ownerTpl, string slotName)
        {
            if (!items.TryGetValue(ownerTpl, out var owner)) return null;
            var slots = owner.Properties?.Slots;
            if (slots == null) return null;

            foreach (var slot in slots)
            {
                if (!string.Equals(slot.Name, slotName, StringComparison.OrdinalIgnoreCase)) continue;
                var filters = slot.Properties?.Filters;
                if (filters == null) continue;
                foreach (var slotFilter in filters)
                {
                    if (slotFilter.Filter != null) return slotFilter.Filter;
                }
            }
            return null;
        }

        var referenceFilter = FindFilter(ReferenceHandguardTpl, FrontSightSlotName);
        if (referenceFilter == null)
        {
            _logger.Debug("[FsRiserFrontSightHostCompat] Could not find the reference handguard's mod_sight_front filter -- nothing to mirror.");
            return Task.CompletedTask;
        }

        foreach (var riserTpl in OurRiserTpls)
        {
            var ourFilter = FindFilter(riserTpl, FrontSightSlotName);
            if (ourFilter == null)
            {
                _logger.Debug($"[FsRiserFrontSightHostCompat] Could not find {riserTpl}'s own mod_sight_front filter -- nothing to mirror.");
                continue;
            }

            int added = 0;
            foreach (var tpl in referenceFilter)
            {
                bool isOwnTpl = false;
                foreach (var ownTpl in OurRiserTpls)
                {
                    if (tpl == ownTpl) { isOwnTpl = true; break; }
                }
                if (isOwnTpl) continue;
                if (ourFilter.Add(tpl)) added++;
            }
            foreach (var extraTpl in ExtraHostedTpls)
            {
                if (ourFilter.Add(extraTpl)) added++;
            }

            _logger.Debug($"[FsRiserFrontSightHostCompat] Populated {riserTpl}'s mod_sight_front filter with {added} tpl(s) (reference mirror + extras).");
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Companion to FsRiserFrontSightHostCompat -- whitelists our own tpls wherever a "picatinny front
/// ironsight" mount already exists, using the same reference set as the trigger (an OR match: any slot
/// containing at least one reference tpl counts). See the doc comment on FsRiserFrontSightHostCompat
/// for how that reference set was derived, including the 3 extra tpls added 2026-07-18. Runs very late
/// (int.MaxValue) so every other mod's items already exist in the DB.
/// </summary>
[Injectable(InjectionType.Transient, int.MaxValue)]
public class FsRiserMountCompat : IOnLoad
{
    private readonly TemplateTable _templateTable;
    private readonly ISptLogger<FsRiserMountCompat> _logger;

    private const string FrontSightSlotName = "mod_sight_front";

    // Our two color variants.
    private static readonly string[] OurRiserTpls =
    {
        "5485b2419854d122298ef0a1", // Irregular Defense Front Ironsight Riser (BLK)
        "aaeeaf7b054119901161401f", // Irregular Defense Front Ironsight Riser (FDE)
    };

    // The base 9-tpl "universal Picatinny front ironsight" set, plus 3 extras (Scalarworks PEAK,
    // Magpul MBUS Gen3 x2) added 2026-07-18 -- see FsRiserFrontSightHostCompat's doc comment. Any slot
    // containing at least one of these counts as a picatinny front-sight mount.
    private static readonly string[] ReferenceTpls =
    {
        "5c17804b2e2216152006c02f", // KAC Folding front sight
        "5dfa3d950dee1b22f862eae0", // KAC Folding Micro front sight
        "5bc09a30d4351e00367fb7c8", // Magpul MBUS Gen2 flip-up front sight (Black)
        "5c18b90d2e2216152142466b", // Magpul MBUS Gen2 flip-up front sight (FDE)
        "5894a73486f77426d259076c", // MPX flip-up front sight
        "5ba26b01d4351e0085325a51", // HK MP7 flip-up front sight
        "5fb6567747ce63734e3fa1dc", // KRISS Defiance low profile flip-up front sight
        "5fc0fa362770a0045c59c677", // MCX flip-up front sight
        "55d4af3a4bdc2d972f8b456f", // AR-15 Leapers UTG Low Profile A2 front sight
        "48dd8b7edd39c0ab164435ad", // Scalarworks PEAK front sight (WTT-Armory)
        "66ba2a6be18ed84221edd72d", // Magpul MBUS Gen3 flip-up front sight (WTT-Armory)
        "66ba2a7583204e25695233c0", // Magpul MBUS Gen3 flip-up front sight (WTT-Armory)
    };

    public FsRiserMountCompat(TemplateTable templateTable, ISptLogger<FsRiserMountCompat> logger)
    {
        _templateTable = templateTable;
        _logger = logger;
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        int patched = 0;
        int slotsChecked = 0;
        int slotsMatched = 0;

        foreach (var (itemId, item) in _templateTable.Items)
        {
            var slots = item.Properties?.Slots;
            if (slots == null) continue;

            foreach (var slot in slots)
            {
                if (!string.Equals(slot.Name, FrontSightSlotName, StringComparison.OrdinalIgnoreCase)) continue;
                var filters = slot.Properties?.Filters;
                if (filters == null) continue;

                foreach (var slotFilter in filters)
                {
                    var filter = slotFilter.Filter;
                    if (filter == null) continue;
                    slotsChecked++;

                    bool isReferenceSlot = false;
                    foreach (var refTpl in ReferenceTpls)
                    {
                        if (filter.Contains(refTpl)) { isReferenceSlot = true; break; }
                    }
                    if (!isReferenceSlot) continue;
                    slotsMatched++;

                    foreach (var riserTpl in OurRiserTpls)
                    {
                        if (filter.Add(riserTpl))
                        {
                            patched++;
                            _logger.Debug($"[FsRiserMountCompat] Added {riserTpl} to slot '{slot.Name}' for parent {itemId}");
                        }
                    }
                }
            }
        }

        _logger.Debug($"[FsRiserMountCompat] Checked {slotsChecked} mod_sight_front filter(s), {slotsMatched} matched the picatinny-universal whitelist, {patched} addition(s) made.");
        return Task.CompletedTask;
    }
}

/// <summary>
/// The LMT PDW Buffer Tube clones the vanilla AR-15 Colt Carbine buffer tube directly (itemTplToClone
/// is the vanilla tpl itself), so in theory WTT's own ModSlotHelper propagation should whitelist it
/// anywhere the vanilla tube fits, no extra work needed. In practice -- same story as
/// CtrRisrStockCompat/MoeSlkStockCompat/SigStockCompat -- that automatic propagation isn't reliably
/// landing on every slot (user-confirmed 2026-07-18: not attachable anywhere in-game despite
/// addtoModSlots: true), so this runs its own independent DB walk instead of trusting WTT's mechanism.
/// Runs very late (int.MaxValue) so every other mod's items already exist in the DB.
/// </summary>
[Injectable(InjectionType.Transient, int.MaxValue)]
public class LmtPdwBufferTubeCompat : IOnLoad
{
    private readonly TemplateTable _templateTable;
    private readonly ISptLogger<LmtPdwBufferTubeCompat> _logger;

    // Anything that whitelists the vanilla AR-15 Colt Carbine buffer tube should also whitelist ours.
    private static readonly string[] ReferenceTpls =
    {
        "5649be884bdc2d79388b4577", // vanilla AR-15 Colt Carbine buffer tube
    };

    // Our LMT PDW Buffer Tube.
    private static readonly string[] TargetTpls =
    {
        "62068c0e5c79b2d4921ce5b2", // LMT PDW Buffer Tube
    };

    public LmtPdwBufferTubeCompat(TemplateTable templateTable, ISptLogger<LmtPdwBufferTubeCompat> logger)
    {
        _templateTable = templateTable;
        _logger = logger;
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        int patched = 0;
        int slotsChecked = 0;
        int slotsMatched = 0;

        foreach (var (itemId, item) in _templateTable.Items)
        {
            var slots = item.Properties?.Slots;
            if (slots == null) continue;

            foreach (var slot in slots)
            {
                var filters = slot.Properties?.Filters;
                if (filters == null) continue;

                foreach (var slotFilter in filters)
                {
                    var filter = slotFilter.Filter;
                    if (filter == null) continue;
                    slotsChecked++;

                    bool isReferenceSlot = false;
                    foreach (var refTpl in ReferenceTpls)
                    {
                        if (filter.Contains(refTpl)) { isReferenceSlot = true; break; }
                    }
                    if (!isReferenceSlot) continue;
                    slotsMatched++;

                    foreach (var targetTpl in TargetTpls)
                    {
                        if (filter.Add(targetTpl))
                        {
                            patched++;
                            _logger.Debug($"[LmtPdwBufferTubeCompat] Added {targetTpl} to slot '{slot.Name}' for parent {itemId}");
                        }
                    }
                }
            }
        }

        _logger.Debug($"[LmtPdwBufferTubeCompat] Checked {slotsChecked} slot filter(s), {slotsMatched} matched vanilla-buffer-tube whitelist, {patched} addition(s) made.");
        return Task.CompletedTask;
    }
}

/// <summary>
/// The MPX/MCX PMM ULSS stock w/Riser clones the vanilla MPX/MCX PMM ULSS stock directly
/// (itemTplToClone is the vanilla tpl itself), so in theory WTT's own ModSlotHelper propagation
/// should whitelist it anywhere the vanilla ULSS fits, no extra work needed. Same story as
/// CtrRisrStockCompat/MoeSlkStockCompat/SigStockCompat/LmtPdwBufferTubeCompat -- that automatic
/// propagation isn't reliably landing on every slot, so this runs its own independent DB walk
/// instead of trusting WTT's mechanism. Runs very late (int.MaxValue) so every other mod's items
/// already exist in the DB.
/// </summary>
[Injectable(InjectionType.Transient, int.MaxValue)]
public class UlssStockCompat : IOnLoad
{
    private readonly TemplateTable _templateTable;
    private readonly ISptLogger<UlssStockCompat> _logger;

    // Anything that whitelists the vanilla MPX/MCX PMM ULSS stock should also whitelist ours.
    private static readonly string[] ReferenceTpls =
    {
        "5c5db6f82e2216003a0fe914", // vanilla MPX/MCX PMM ULSS stock
    };

    // Our MPX/MCX PMM ULSS stock w/Riser.
    private static readonly string[] TargetTpls =
    {
        "ba7860823b20502455fce227", // MPX/MCX PMM ULSS stock w/Riser
    };

    public UlssStockCompat(TemplateTable templateTable, ISptLogger<UlssStockCompat> logger)
    {
        _templateTable = templateTable;
        _logger = logger;
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        int patched = 0;
        int slotsChecked = 0;
        int slotsMatched = 0;

        foreach (var (itemId, item) in _templateTable.Items)
        {
            var slots = item.Properties?.Slots;
            if (slots == null) continue;

            foreach (var slot in slots)
            {
                var filters = slot.Properties?.Filters;
                if (filters == null) continue;

                foreach (var slotFilter in filters)
                {
                    var filter = slotFilter.Filter;
                    if (filter == null) continue;
                    slotsChecked++;

                    bool isReferenceSlot = false;
                    foreach (var refTpl in ReferenceTpls)
                    {
                        if (filter.Contains(refTpl)) { isReferenceSlot = true; break; }
                    }
                    if (!isReferenceSlot) continue;
                    slotsMatched++;

                    foreach (var targetTpl in TargetTpls)
                    {
                        if (filter.Add(targetTpl))
                        {
                            patched++;
                            _logger.Debug($"[UlssStockCompat] Added {targetTpl} to slot '{slot.Name}' for parent {itemId}");
                        }
                    }
                }
            }
        }

        _logger.Debug($"[UlssStockCompat] Checked {slotsChecked} slot filter(s), {slotsMatched} matched vanilla-ULSS whitelist, {patched} addition(s) made.");
        return Task.CompletedTask;
    }
}
