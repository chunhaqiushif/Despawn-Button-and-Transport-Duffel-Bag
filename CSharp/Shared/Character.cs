
using System.Reflection;
using System.Runtime.CompilerServices;

using Barotrauma;
using Barotrauma.LuaCs;
using HarmonyLib;


using Barotrauma.Networking;
using FarseerPhysics;
using Microsoft.Xna.Framework;
using System;
using Barotrauma.IO;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Barotrauma.Items.Components;
using FarseerPhysics.Dynamics;
using Barotrauma.Extensions;
using System.Collections.Immutable;
using Barotrauma.Abilities;
using System.Diagnostics;
#if SERVER
using System.Text;
#endif

namespace RemoveCorpses
{
  public partial class Mod : IAssemblyPlugin
  {
    public static void Character_UpdateAll_Prefix(float deltaTime, Camera cam)
    {
      if (despawnPulse > 0) despawnPulse--;
    }

    public static void Character_UpdateDespawn_Replace(Character __instance, ref bool __runOriginal, float deltaTime, bool createNetworkEvents)
    {
      Character _ = __instance;
      __runOriginal = false;

      if (!_.EnableDespawn) return;

      //clients don't despawn characters unless the server says so
      if (GameMain.NetworkMember != null && !GameMain.NetworkMember.IsServer) return;

      if (!_.IsDead || (_.CauseOfDeath?.Type == CauseOfDeathType.Disconnected && GameMain.GameSession?.Campaign != null)) return;

      if (_.SelectedBy != null)
      {
        _.despawnTimer = 0.0f;
        return;
      }

      if (despawnPulse > 0 || forceDespawn)
      {
        //despawnPriority = despawnPriority * 20;

        //尸体内的掉落物交给原版 Despawn 打包成遗物袋，生成在尸体消失的坐标
        DespawnWithRelicBag(_);

        //清除后再关掉自动清除，避免下一帧重复调用、生成多个遗物袋
        _.EnableDespawn = false;
        _.accessRemovedCharacterErrorShown = true;
        return;
      }

      float despawnDelay = GameSettings.CurrentConfig.CorpseDespawnDelay;
      float despawnPriority = 1.0f;
      if (GameMain.GameSession?.GameMode is PvPMode &&
          GameMain.NetworkMember?.RespawnManager != null)
      {
        //simpler despawning logic in PvP modes with respawning: just a short timer
        despawnDelay = GameSettings.CurrentConfig.CorpseDespawnDelayPvP;
      }
      else
      {
        int subCorpseCount = 0;
        if (_.Submarine != null)
        {
          subCorpseCount = Character.CharacterList.Count(c => c.IsDead && c.Submarine == _.Submarine);
          if (subCorpseCount < GameSettings.CurrentConfig.CorpsesPerSubDespawnThreshold) return;
        }

        if (subCorpseCount > GameSettings.CurrentConfig.CorpsesPerSubDespawnThreshold)
        {
          //despawn faster if there are lots of corpses in the sub (twice as many as the threshold -> despawn twice as fast)
          despawnPriority += (subCorpseCount - GameSettings.CurrentConfig.CorpsesPerSubDespawnThreshold) / (float)GameSettings.CurrentConfig.CorpsesPerSubDespawnThreshold;
        }

        float distToClosestPlayer = _.GetDistanceToClosestPlayer();
        if (distToClosestPlayer > _.Params.DisableDistance)
        {
          //despawn in 1 minute if very far from all human players
          _.despawnTimer = Math.Max(_.despawnTimer, despawnDelay - 60.0f);
        }

        if (_.AIController is EnemyAIController)
        {
          //enemies despawn faster
          despawnPriority *= 2.0f;
        }
      }

      _.despawnTimer += deltaTime * despawnPriority;
      if (_.despawnTimer < despawnDelay) return;

      //自然到时的尸体清除：原版会打包成遗物袋，这里同样把袋子登记传送
      DespawnAndRegisterBag(_);
    }

  }
}