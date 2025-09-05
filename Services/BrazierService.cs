using Il2CppInterop.Runtime;
using ProjectM;
using ProjectM.CastleBuilding;
using ProjectM.Network;
using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;
using UnityEngine;

namespace KindredLogistics.Services;
class BrazierService
{
    readonly Dictionary<Entity, List<Entity>> braziersByHeart = [];
    Dictionary<int, HashSet<Entity>> modifiedBraziers = [];

    public BrazierService()
    {
        var entityQueryBuilder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(ComponentType.ReadOnly(Il2CppType.Of<Bonfire>()))
            .WithOptions(EntityQueryOptions.IncludeDisabled);
        var brazierQuery = Core.EntityManager.CreateEntityQuery(ref entityQueryBuilder);
        entityQueryBuilder.Dispose();

        var stationArray = brazierQuery.ToEntityArray(Allocator.Temp);
        try
        {
            foreach (var station in stationArray)
                AddBrazier(station);
        }
        finally
        {
            stationArray.Dispose();
        }
        brazierQuery.Dispose();

        Core.TerritoryService.RegisterTerritoryUpdateCallback(UpdateIfBraziersActiveOnTerritory);

        for(var i = TerritoryService.MIN_TERRITORY_ID; i <= TerritoryService.MAX_TERRITORY_ID; i++)
        {
            modifiedBraziers.Add(i, []);
        }
    }

    internal void AddBrazier(Entity stationEntity)
    {
        var castleHeartEntity = stationEntity.Read<CastleHeartConnection>().CastleHeartEntity.GetEntityOnServer();

        if (!braziersByHeart.TryGetValue(castleHeartEntity, out var list))
        {
            list = [];
            braziersByHeart.Add(castleHeartEntity, list);
        }
        list.Add(stationEntity);
    }

    internal void RemoveBrazier(Entity stationEntity)
    {
        var castleHeartEntity = stationEntity.Read<CastleHeartConnection>().CastleHeartEntity.GetEntityOnServer();

        if (!braziersByHeart.TryGetValue(castleHeartEntity, out var list)) return;

        list.Remove(stationEntity);
    }

    public IEnumerable<Entity> GetAllBraziers(int territoryId)
    {
        var castleHeartEntity = Core.TerritoryService.GetCastleHeart(territoryId);
        if (!braziersByHeart.TryGetValue(castleHeartEntity, out var list)) yield break;

        foreach (var stationEntity in list)
        {
            if (stationEntity.Has<Disabled>()) continue;
            yield return stationEntity;
        }
    }

    IEnumerator UpdateIfBraziersActiveOnTerritory(int territoryId, Entity castleHeartEntity)
    {
        if (!Core.PlayerSettings.IsSolarEnabled(0)) yield break;

        // Check if any of the clan mates are online and on the territory
        var userOwner = castleHeartEntity.Read<UserOwner>();
        if (userOwner.Owner.GetEntityOnServer() == Entity.Null) yield break;

        var entitiesToCheckForProximity = new List<Entity>();
        var proxEnable = true;
        var ownerEntity = userOwner.Owner.GetEntityOnServer();
        var user = ownerEntity.Read<User>();
        var clanEntity = user.ClanEntity.GetEntityOnServer();
        if (clanEntity == Entity.Null)
        {
            var character = user.LocalCharacter.GetEntityOnServer();
            // No clan, so check only the owner
            if (!user.IsConnected || Core.TerritoryService.GetTerritoryId(character) != territoryId)
            {
                proxEnable = false;
            }
            else
            {
                entitiesToCheckForProximity.Add(character);
            }
        }
        else
        {
            var foundOnlineMemberOnTerritory = false;
            var members = Core.EntityManager.GetBuffer<ClanMemberStatus>(clanEntity);
            var userBuffer = Core.EntityManager.GetBuffer<SyncToUserBuffer>(clanEntity);
            for (var i = 0; i < members.Length; ++i)
            {
                if (!members[i].IsConnected) continue;

                var character = userBuffer[i].UserEntity.Read<User>().LocalCharacter.GetEntityOnServer();
                if (Core.TerritoryService.GetTerritoryId(character) == territoryId)
                {
                    foundOnlineMemberOnTerritory = true;
                    entitiesToCheckForProximity.Add(character);
                }
            }

            if (!foundOnlineMemberOnTerritory)
            {
                proxEnable = false;
                entitiesToCheckForProximity.Clear();
            }
        }

        var allBraziers = GetAllBraziers(territoryId);
        var modified = modifiedBraziers[territoryId];
        foreach (var brazier in allBraziers)
        {
            var nameableInteractable = brazier.Read<NameableInteractable>();
            var name = nameableInteractable.Name.ToString().ToLower();
            if (name.Contains("prox"))
            {
                const float proxDistance = 20f;

                var shouldEnable = proxEnable;
                if (shouldEnable)
                {
                    var brazierPosition = brazier.Read<Translation>().Value.xz;
                    shouldEnable = false;
                    foreach (var entity in entitiesToCheckForProximity)
                    {
                        var entityPosition = entity.Read<Translation>().Value.xz;
                        if (Vector2.Distance(brazierPosition, entityPosition) <= proxDistance)
                        {
                            shouldEnable = true;
                            break;
                        }
                    }
                }

                var burnContainer = brazier.Read<BurnContainer>();
                if (burnContainer.Enabled != shouldEnable)
                {
                    burnContainer.Enabled = shouldEnable;
                    brazier.Write(burnContainer);

                    if (!modified.Contains(brazier))
                    {
                        var bonfireTime = brazier.Read<Bonfire>();
                        bonfireTime.TimeToGetToFullStrength = 0.5f;
                        brazier.Write(bonfireTime);
                        modified.Add(brazier);
                    }
                }
            }
            else
            {
                if (modified.Contains(brazier))
                {
                    modified.Remove(brazier);
                    var bonfireTime = brazier.Read<Bonfire>();
                    bonfireTime.TimeToGetToFullStrength = 15;
                    brazier.Write(bonfireTime);
                }
            }
        }
    }
}
