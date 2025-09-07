using Il2CppInterop.Runtime;
using ProjectM;
using ProjectM.CastleBuilding;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Unity.Collections;
using Unity.Entities;

namespace KindredLogistics.Services
{
    internal class RefinementStationsService
    {

        readonly Regex receiverRegex;
        readonly Regex senderRegex;

        readonly Dictionary<Entity, List<Entity>> refinementStationsByHeart = [];

        public RefinementStationsService() 
        {
            var entityQueryBuilder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(ComponentType.ReadOnly(Il2CppType.Of<Team>()))
            .AddAll(ComponentType.ReadOnly(Il2CppType.Of<CastleHeartConnection>()))
            .AddAll(ComponentType.ReadOnly(Il2CppType.Of<Refinementstation>()))
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
                    AddRefinementStation(station);
            }
            finally
            {
                stationArray.Dispose();
            }
            stationsQuery.Dispose();

            receiverRegex = new Regex(Const.RECEIVER_REGEX, RegexOptions.Compiled);
            senderRegex = new Regex(Const.SENDER_REGEX, RegexOptions.Compiled);
        }

        internal void AddRefinementStation(Entity stationEntity)
        {
            var castleHeartEntity = stationEntity.Read<CastleHeartConnection>().CastleHeartEntity.GetEntityOnServer();

            if (!refinementStationsByHeart.TryGetValue(castleHeartEntity, out var list))
            {
                list = [];
                refinementStationsByHeart.Add(castleHeartEntity, list);
            }
            list.Add(stationEntity);
        }

        internal void RemoveRefinementStation(Entity stationEntity)
        {
            var castleHeartEntity = stationEntity.Read<CastleHeartConnection>().CastleHeartEntity.GetEntityOnServer();

            if (!refinementStationsByHeart.TryGetValue(castleHeartEntity, out var list)) return;

            list.Remove(stationEntity);
        }

        public IEnumerable<(int group, Entity station)> GetAllReceivingStations(int territoryId)
        {
            foreach (var result in GetAllGroupStations(receiverRegex, territoryId))
            {
                yield return result;
            }
        }

        public IEnumerable<(int group, Entity station)> GetAllSendingStations(int territoryId)
        {
            foreach(var result in GetAllGroupStations(senderRegex, territoryId))
            {
                yield return result;
            }
        }

        IEnumerable<(int group, Entity station)> GetAllGroupStations(Regex groupRegex, int territoryId)
        {

            var castleHeartEntity = Core.TerritoryService.GetCastleHeart(territoryId);
            if (!refinementStationsByHeart.TryGetValue(castleHeartEntity, out var list)) yield break;

            for (var i = list.Count - 1; i >= 0; i--)
            {
                var stationEntity = list[i];
                if (!Core.EntityManager.Exists(stationEntity))
                {
                    list.RemoveAt(i);
                    continue;
                }
                if (stationEntity.Has<Disabled>()) continue;
                var name = stationEntity.Read<NameableInteractable>().Name.ToString().ToLower();
                foreach (Match match in groupRegex.Matches(name))
                {
                    var group = int.Parse(match.Groups[1].Value);
                    yield return (group, stationEntity);
                }
            }
        }


    }
}
