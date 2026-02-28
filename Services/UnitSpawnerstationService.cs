using Il2CppInterop.Runtime;
using ProjectM;
using ProjectM.CastleBuilding;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;


namespace KindredLogistics.Services;
class UnitSpawnerstationService
{
    readonly Dictionary<Entity, List<Entity>> unitSpawnerStationsByHeart = [];

    public UnitSpawnerstationService()
    {
        var entityQueryBuilder = new EntityQueryBuilder(Allocator.Temp)
        .AddAll(ComponentType.ReadOnly(Il2CppType.Of<Team>()))
        .AddAll(ComponentType.ReadOnly(Il2CppType.Of<CastleHeartConnection>()))
        .AddAll(ComponentType.ReadOnly(Il2CppType.Of<UnitSpawnerstation>()))
        .AddAll(ComponentType.ReadOnly(Il2CppType.Of<NameableInteractable>()))
        .AddAll(ComponentType.ReadOnly(Il2CppType.Of<UserOwner>()))
        .AddAll(ComponentType.ReadOnly(Il2CppType.Of<RefinementstationRecipesBuffer>()))
        .AddAll(ComponentType.ReadOnly(Il2CppType.Of<CastleWorkstation>()))
        .WithOptions(EntityQueryOptions.IncludeDisabled);

        var stationsQuery = Core.EntityManager.CreateEntityQuery(ref entityQueryBuilder);
        entityQueryBuilder.Dispose();
        var stationArray = stationsQuery.ToEntityArray(Allocator.Temp);
        try
        {
            foreach (var station in stationArray)
                AddUnitSpawnerStation(station);
        }
        finally
        {
            stationArray.Dispose();
        }
        stationsQuery.Dispose();
    }

    internal void AddUnitSpawnerStation(Entity stationEntity)
    {
        var castleHeartEntity = stationEntity.Read<CastleHeartConnection>().CastleHeartEntity.GetEntityOnServer();

        if (!unitSpawnerStationsByHeart.TryGetValue(castleHeartEntity, out var list))
        {
            list = [];
            unitSpawnerStationsByHeart.Add(castleHeartEntity, list);
        }
        list.Add(stationEntity);
    }

    internal void RemoveUnitSpawnerStation(Entity stationEntity)
    {
        var castleHeartEntity = stationEntity.Read<CastleHeartConnection>().CastleHeartEntity.GetEntityOnServer();

        if (!unitSpawnerStationsByHeart.TryGetValue(castleHeartEntity, out var list)) return;

        list.Remove(stationEntity);
    }

    public IEnumerable<Entity> GetAllUnitSpawners(int territoryId)
    {
        var castleHeartEntity = Core.TerritoryService.GetCastleHeart(territoryId);
        if (!unitSpawnerStationsByHeart.TryGetValue(castleHeartEntity, out var list)) yield break;

        for (var i = list.Count - 1; i >= 0; i--)
        {
            var stationEntity = list[i];
            if (!Core.EntityManager.Exists(stationEntity))
            {
                list.RemoveAt(i);
                continue;
            }
            if (stationEntity.Has<Disabled>()) continue;
            yield return stationEntity;
        }
    }
}
