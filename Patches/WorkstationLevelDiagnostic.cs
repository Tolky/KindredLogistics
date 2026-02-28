using HarmonyLib;
using KindredLogistics.Services;
using ProjectM;
using ProjectM.CastleBuilding;

namespace KindredLogistics.Patches;

/// <summary>
/// Detects floor tile changes (add/move/remove) via CastleFloorAndWallsUpdateSystem.
/// Floor entities don't have territory references, so we mark ALL territories pending.
/// Floor changes are rare events, so the overhead is negligible.
/// After this system runs, UnitSpawnerUpdateSystem will update CastleWorkstation.WorkstationLevel
/// (including MatchingFloor), so by the time conveyors process, the flag is correct.
/// </summary>
[HarmonyPatch(typeof(CastleFloorAndWallsUpdateSystem), "OnUpdate")]
public static class FloorChangePatches
{
    static CastleFloorAndWallsUpdateSystem _system;

    public static void Prefix()
    {
        if (!Core.HasInitialized) return;

        try
        {
            if (_system == null)
                _system = Core.Server.GetExistingSystemManaged<CastleFloorAndWallsUpdateSystem>();

            bool hasChanges =
                !_system._AddedFloorsQuery.IsEmptyIgnoreFilter ||
                !_system._MovedFloorsQuery.IsEmptyIgnoreFilter ||
                !_system._RemovedFloorsQuery.IsEmptyIgnoreFilter;

            if (hasChanges)
            {
                // Floor entities lack territory info (territory=-1), so mark all territories
                for (int t = TerritoryService.MIN_TERRITORY_ID; t <= TerritoryService.MAX_TERRITORY_ID; t++)
                    ConveyorService.MarkTerritoryPending(t);
            }
        }
        catch (System.Exception ex)
        {
            Core.Log.LogError($"[FloorChange] Error: {ex.Message}");
        }
    }
}
