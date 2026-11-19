
using System.Reflection;
using System.Runtime.CompilerServices;

using Barotrauma;
using HarmonyLib;
using Barotrauma.LuaCs;
using Barotrauma.LuaCs.Compatibility;


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
    public Harmony harmony;

    private const string HookOwner = "despawnbutton";

    public static bool forceDespawn = false;
    public static int despawnPulse = 0;

    public void Initialize()
    {
      harmony = new Harmony("remove.corpses");

      patchAll();

      init();

      //遗物袋的 10 秒计时 / 传送调度（每帧一次，暂停时不走）
      StartRelicBagTicker();
    }

    public static void init()
    {
      //1.13+ 没有 GameMain.LuaCs 了，改用 LuaCsSetup.Instance.Hook（与 XML <LuaHook> 的 Hook.Call 同一个注册表）
      try
      {
        ILuaCsHook hook = LuaCsSetup.Instance.Hook;

        hook.Add("despawnpulse", HookOwner, args =>
        {
          despawnPulse = 2;
          return null;
        });

        hook.Add("setforcedespawn", HookOwner, args =>
        {
          Item item = args[2] as Item;
          if (item == null || item.Components.Count == 0) return null;
          Controller c = item.Components[0] as Controller;
          if (c == null) return null;

          forceDespawn = c.State;

          return null;
        });

        hook.Add("setforcedespawnfromrelay", HookOwner, args =>
        {
          Item item = args[2] as Item;
          if (item == null || item.Components.Count == 0) return null;
          RelayComponent relay = item.Components[0] as RelayComponent;
          if (relay == null) return null;

          forceDespawn = relay.IsOn;

          return null;
        });

        Log("[Despawn Button] LuaHook 已注册（despawnpulse / setforcedespawn / setforcedespawnfromrelay）");
      }
      catch (Exception e)
      {
        LuaCsLogger.Log("[Despawn Button] 注册 LuaHook 失败：" + e);
      }
    }

    public void patchAll()
    {
      harmony.Patch(
        original: typeof(Character).GetMethod("UpdateDespawn", AccessTools.all),
        prefix: new HarmonyMethod(typeof(Mod).GetMethod("Character_UpdateDespawn_Replace"))
      );

      harmony.Patch(
        original: typeof(Character).GetMethod("UpdateAll", AccessTools.all),
        prefix: new HarmonyMethod(typeof(Mod).GetMethod("Character_UpdateAll_Prefix"))
      );

      //原版 Despawn 生成遗物袋时会走这个重载，借用它的回调把袋子接过来
      MethodInfo spawnQueue = AccessTools.Method(typeof(EntitySpawner), "AddItemToSpawnQueue",
        new Type[] { typeof(ItemPrefab), typeof(Vector2), typeof(float?), typeof(int?), typeof(Action<Item>) });
      if (spawnQueue != null)
      {
        harmony.Patch(
          original: spawnQueue,
          prefix: new HarmonyMethod(typeof(Mod).GetMethod("Spawner_AddItemToSpawnQueue_Prefix"))
        );
      }
      else
      {
        Log("[Despawn Button] 未找到 EntitySpawner.AddItemToSpawnQueue，遗物袋不会传送");
      }
    }


    public static void Log(object msg, Color? cl = null)
    {
      cl ??= Color.Cyan;
      LuaCsLogger.LogMessage($"{msg ?? "null"}", cl * 0.8f, cl);
    }

    public void OnLoadCompleted() { }
    public void PreInitPatching() { }

    public void Dispose()
    {
      StopRelicBagTicker();

      try
      {
        ILuaCsHook hook = LuaCsSetup.Instance.Hook;
        hook.Remove("despawnpulse", HookOwner);
        hook.Remove("setforcedespawn", HookOwner);
        hook.Remove("setforcedespawnfromrelay", HookOwner);
      }
      catch { }

      harmony.UnpatchSelf();
    }
  }


}