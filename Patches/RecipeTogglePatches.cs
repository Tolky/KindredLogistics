using HarmonyLib;
using Il2CppInterop.Runtime;
using KindredLogistics.Services;
using ProjectM;
using ProjectM.Network;
using ProjectM.Shared;
using Unity.Collections;
using Unity.Entities;

namespace KindredLogistics.Patches;

[HarmonyPatch(typeof(ServerBootstrapSystem), "OnUpdate")]
public static class RecipeTogglePatches
{
    static EntityQuery _toggleRecipeQuery;
    static EntityQuery _toggleRefiningQuery;
    static EntityQuery _userConnectedQuery;
    static EntityQuery _workstationLevelQuery;
    static EntityQuery _moveTileQuery;
    static bool _workstationQueryFailed;

    public static void Postfix()
    {
        if (!Core.HasInitialized) return;

        try
        {
            if (_toggleRecipeQuery == default)
            {
                var eqb = new EntityQueryBuilder(Allocator.Temp)
                    .AddAll(new(Il2CppType.Of<ToggleRefiningRecipeEvent>(), ComponentType.AccessMode.ReadOnly));
                _toggleRecipeQuery = Core.EntityManager.CreateEntityQuery(ref eqb);
                eqb.Dispose();

                eqb = new EntityQueryBuilder(Allocator.Temp)
                    .AddAll(new(Il2CppType.Of<ToggleRefiningEvent>(), ComponentType.AccessMode.ReadOnly));
                _toggleRefiningQuery = Core.EntityManager.CreateEntityQuery(ref eqb);
                eqb.Dispose();

                eqb = new EntityQueryBuilder(Allocator.Temp)
                    .AddAll(new(Il2CppType.Of<UserConnectedServerEvent>(), ComponentType.AccessMode.ReadOnly));
                _userConnectedQuery = Core.EntityManager.CreateEntityQuery(ref eqb);
                eqb.Dispose();

                eqb = new EntityQueryBuilder(Allocator.Temp)
                    .AddAll(new(Il2CppType.Of<MoveTileModelEvent>(), ComponentType.AccessMode.ReadOnly));
                _moveTileQuery = Core.EntityManager.CreateEntityQuery(ref eqb);
                eqb.Dispose();

                // CastleWorkstationLevelModified may not exist as a real IL2CPP component at runtime
                try
                {
                    eqb = new EntityQueryBuilder(Allocator.Temp)
                        .AddAll(new(Il2CppType.Of<CastleWorkstationLevelModified>(), ComponentType.AccessMode.ReadOnly));
                    _workstationLevelQuery = Core.EntityManager.CreateEntityQuery(ref eqb);
                    eqb.Dispose();
                    Core.Log.LogInfo("[RecipeToggle] CastleWorkstationLevelModified query registered");
                }
                catch (System.Exception ex)
                {
                    _workstationQueryFailed = true;
                    Core.Log.LogWarning($"[RecipeToggle] CastleWorkstationLevelModified unavailable: {ex.Message}");
                }
            }

            // Player connection → full territory init
            if (!_userConnectedQuery.IsEmptyIgnoreFilter)
            {
                Core.RefinementStations.FlushNameCache();
                Core.Stash.FlushNameCache();
                RefinementStationsService.InvalidateAllTerritories();
                Core.Stash.InvalidateAllTerritories();
                ConveyorService.RefreshReverseMap();
                for (int t = TerritoryService.MIN_TERRITORY_ID; t <= TerritoryService.MAX_TERRITORY_ID; t++)
                    if (Core.TerritoryService.GetCastleHeart(t) != Entity.Null)
                        ConveyorService.MarkTerritoryPending(t);
                Core.Log.LogInfo("[Startup] Player connected — all territories initialized");
            }

            // Workstation level changed (MatchingFloor gained/lost) → mark territory pending
            if (!_workstationQueryFailed && !_workstationLevelQuery.IsEmptyIgnoreFilter)
            {
                var entities = _workstationLevelQuery.ToEntityArray(Allocator.Temp);
                try
                {
                    foreach (var entity in entities)
                    {
                        var territoryId = Core.TerritoryService.GetTerritoryId(entity);
                        if (territoryId >= 0)
                            ConveyorService.MarkTerritoryPending(territoryId);
                    }
                }
                finally
                {
                    entities.Dispose();
                }
            }

            // Building moved → mark territory pending (MatchingFloor may change)
            if (!_moveTileQuery.IsEmptyIgnoreFilter)
            {
                var entities = _moveTileQuery.ToEntityArray(Allocator.Temp);
                try
                {
                    foreach (var entity in entities)
                    {
                        if (!entity.Has<FromCharacter>()) continue;
                        var character = entity.Read<FromCharacter>().Character;
                        var territoryId = Core.TerritoryService.GetTerritoryId(character);
                        if (territoryId >= 0)
                            ConveyorService.MarkTerritoryPending(territoryId);
                    }
                }
                finally
                {
                    entities.Dispose();
                }
            }

            if (!_toggleRecipeQuery.IsEmptyIgnoreFilter)
            {
                var entities = _toggleRecipeQuery.ToEntityArray(Allocator.Temp);
                try
                {
                    foreach (var entity in entities)
                    {
                        if (!entity.Has<FromCharacter>()) continue;
                        var character = entity.Read<FromCharacter>().Character;
                        var territoryId = Core.TerritoryService.GetTerritoryId(character);
                        if (territoryId >= 0)
                            ConveyorService.MarkTerritoryPending(territoryId);
                    }
                }
                finally
                {
                    entities.Dispose();
                }
            }

            if (!_toggleRefiningQuery.IsEmptyIgnoreFilter)
            {
                var entities = _toggleRefiningQuery.ToEntityArray(Allocator.Temp);
                try
                {
                    foreach (var entity in entities)
                    {
                        if (!entity.Has<FromCharacter>()) continue;
                        var character = entity.Read<FromCharacter>().Character;
                        var territoryId = Core.TerritoryService.GetTerritoryId(character);
                        if (territoryId >= 0)
                            ConveyorService.MarkTerritoryPending(territoryId);
                    }
                }
                finally
                {
                    entities.Dispose();
                }
            }
        }
        catch (System.Exception ex)
        {
            Core.Log.LogError($"[RecipeToggle] Error: {ex.Message}");
        }
    }
}
