using HarmonyLib;
using KindredLogistics.Services;
using ProjectM;
using ProjectM.CastleBuilding;
using ProjectM.Network;
using Unity.Collections;
using Unity.Entities;

namespace KindredLogistics.Patches;

[HarmonyPatch(typeof(CastleHasItemsOnSpawnSystem), nameof(CastleHasItemsOnSpawnSystem.OnUpdate))]
internal class CastleStationSpawnSystemPatch
{
    public static bool Prefix(CastleHasItemsOnSpawnSystem __instance)
    {
        var entities = __instance.__query_60442477_0.ToEntityArray(Allocator.Temp);
        foreach (var castleConnectionEntity in entities)
        {
            if (castleConnectionEntity.Has<Bonfire>()) Core.BrazierService.AddBrazier(castleConnectionEntity);
            if (castleConnectionEntity.Has<Refinementstation>())
            {
                Core.RefinementStations.AddRefinementStation(castleConnectionEntity);
                var territoryId = Core.TerritoryService.GetTerritoryId(castleConnectionEntity);
                if (territoryId >= 0)
                    ConveyorService.MarkTerritoryPending(territoryId);
            }
            if (castleConnectionEntity.Has<Salvagestation>()) Core.SalvageService.AddSalvageStation(castleConnectionEntity);
            if (castleConnectionEntity.Has<UnitSpawnerstation>()) Core.UnitSpawnerstationService.AddUnitSpawnerStation(castleConnectionEntity);
        }
        entities.Dispose();
        return true;
    }

    public static void Postfix(CastleHasItemsOnSpawnSystem __instance)
    {
        var entities = __instance.__query_60442477_0.ToEntityArray(Allocator.Temp);
        foreach (var castleConnectionEntity in entities)
        {
            if (!castleConnectionEntity.Has<Refinementstation>()) continue;
            if (!castleConnectionEntity.Has<CastleHeartConnection>()) continue;

            var heart = castleConnectionEntity.Read<CastleHeartConnection>().CastleHeartEntity.GetEntityOnServer();
            if (heart == Entity.Null || !Core.EntityManager.Exists(heart) || !heart.Has<UserOwner>()) continue;

            var ownerEntity = heart.Read<UserOwner>().Owner.GetEntityOnServer();
            if (ownerEntity == Entity.Null || !ownerEntity.Has<User>()) continue;

            var pid = ownerEntity.Read<User>().PlatformId;
            if (!Core.PlayerSettings.IsAutoBaseEnabled(pid)) continue;

            var recipes = Core.EntityManager.GetBuffer<RefinementstationRecipesBuffer>(castleConnectionEntity);
            for (var ri = 0; ri < recipes.Length; ri++)
            {
                var recipe = recipes[ri];
                recipe.Disabled = true;
                recipes[ri] = recipe;
            }
        }
        entities.Dispose();
    }
}

[HarmonyPatch(typeof(CastleHasItemsOnDestroySystem), nameof(CastleHasItemsOnDestroySystem.OnUpdate))]
internal class CastleStationDestroySystemPatch
{
    public static bool Prefix(CastleHasItemsOnDestroySystem __instance)
    {
        var entities = __instance._DestroyConnectedCastleItem.ToEntityArray(Allocator.Temp);
        foreach (var castleConnectionEntity in entities)
        {
            if (castleConnectionEntity.Has<Bonfire>()) Core.BrazierService.RemoveBrazier(castleConnectionEntity);
            if (castleConnectionEntity.Has<Refinementstation>())
            {
                Core.RefinementStations.RemoveRefinementStation(castleConnectionEntity);
                var territoryId = Core.TerritoryService.GetTerritoryId(castleConnectionEntity);
                if (territoryId >= 0)
                {
                    RefinementStationsService.InvalidateTerritory(territoryId);
                    ConveyorService.MarkTerritoryPending(territoryId);
                }
            }
            if (castleConnectionEntity.Has<Salvagestation>()) Core.SalvageService.RemoveSalvageStation(castleConnectionEntity);
            if (castleConnectionEntity.Has<UnitSpawnerstation>()) Core.UnitSpawnerstationService.RemoveUnitSpawnerStation(castleConnectionEntity);
        }
        entities.Dispose();
        return true;
    }
}
