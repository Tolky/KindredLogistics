using Il2CppInterop.Runtime;
using ProjectM;
using ProjectM.CastleBuilding;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;

namespace KindredLogistics.Services;
class SalvageService
{
    readonly Dictionary<Entity, List<Entity>> salvageStationsByHeart = [];

    public SalvageService()
    {
        var entityQueryBuilder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(ComponentType.ReadOnly(Il2CppType.Of<Salvagestation>()))
            .WithOptions(EntityQueryOptions.IncludeDisabled);
        var salvageStationQuery = Core.EntityManager.CreateEntityQuery(ref entityQueryBuilder);
        entityQueryBuilder.Dispose();

        var stationArray = salvageStationQuery.ToEntityArray(Allocator.Temp);
        try
        {
            foreach (var station in stationArray)
                AddSalvageStation(station);
        }
        finally
        {
            stationArray.Dispose();
        }
        salvageStationQuery.Dispose();
    }

    internal void AddSalvageStation(Entity stationEntity)
    {
        var castleHeartEntity = stationEntity.Read<CastleHeartConnection>().CastleHeartEntity.GetEntityOnServer();

        if (!salvageStationsByHeart.TryGetValue(castleHeartEntity, out var list))
        {
            list = [];
            salvageStationsByHeart.Add(castleHeartEntity, list);
        }
        list.Add(stationEntity);
    }

    internal void RemoveSalvageStation(Entity stationEntity)
    {
        var castleHeartEntity = stationEntity.Read<CastleHeartConnection>().CastleHeartEntity.GetEntityOnServer();

        if (!salvageStationsByHeart.TryGetValue(castleHeartEntity, out var list)) return;

        list.Remove(stationEntity);
    }

    public IEnumerable<Entity> GetAllSalvageStations(int territoryId)
    {
        var castleHeartEntity = Core.TerritoryService.GetCastleHeart(territoryId);
        if (!salvageStationsByHeart.TryGetValue(castleHeartEntity, out var list)) yield break;

        foreach (var stationEntity in list)
        {
            if (stationEntity.Has<Disabled>()) continue;
            yield return stationEntity;
        }
    }
}
