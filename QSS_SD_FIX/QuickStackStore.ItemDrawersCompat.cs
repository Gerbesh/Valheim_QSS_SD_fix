#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace QuickStackStore.ItemDrawersCompat
{
    [BepInPlugin(ModGuid, ModName, ModVersion)]
    [BepInDependency("goldenrevolver.quick_stack_store", BepInDependency.DependencyFlags.HardDependency)]
    public sealed class QssItemDrawersCompatPlugin : BaseUnityPlugin
    {
        public const string ModGuid = "gerbesh.QuickStackStore.ItemDrawersCompat";
        public const string ModName = "QuickStackStore ItemDrawers Compat";
        public const string ModVersion = "1.1.2";

        private static ManualLogSource? Log;

        private static ConfigEntry<float>? CfgSearchRadius;
        private static ConfigEntry<bool>? CfgFillEmptyDrawers;
        private static ConfigEntry<bool>? CfgDebug;
        private static ConfigEntry<float>? CfgPlacementCheckDelay;

        private static bool _drawersInstalled;
        private static MethodInfo? _miAllDrawers;
        private static MethodInfo? _miReportQuickStackResult;

        private static readonly MethodInfo? MemberwiseCloneMethod =
            AccessTools.DeclaredMethod(typeof(object), "MemberwiseClone");

        private void Awake()
        {
            Log = Logger;

            CfgSearchRadius = Config.Bind("General", "SearchRadius", 15f, "");
            CfgFillEmptyDrawers = Config.Bind("General", "FillEmptyDrawers", false, "");
            CfgDebug = Config.Bind("General", "DebugLogging", false, "");
            CfgPlacementCheckDelay = Config.Bind("General", "PlacementCheckDelay", 0.5f, "");

            TryInitItemDrawersApi();

            var h = new Harmony(ModGuid);
            TryPatchQss(h);
            h.PatchAll();
        }

        private static void TryInitItemDrawersApi()
        {
            var t = Type.GetType("API.ClientSideV2, kg_ItemDrawers");
            if (t == null) return;

            _miAllDrawers = t.GetMethod("AllDrawers", BindingFlags.Public | BindingFlags.Static);
            _drawersInstalled = _miAllDrawers != null;
        }

        private static void TryPatchQss(Harmony h)
        {
            var helperType = AccessTools.TypeByName("QuickStackStore.Helper");
            if (helperType == null) return;

            _miReportQuickStackResult = AccessTools.Method(helperType, "ReportQuickStackResult");
            if (_miReportQuickStackResult == null) return;

            h.Patch(
                original: _miReportQuickStackResult,
                postfix: new HarmonyMethod(typeof(QssItemDrawersCompatPlugin), nameof(ReportQuickStackResult_Postfix))
            );
        }

        public static void ReportQuickStackResult_Postfix()
        {
            if (!_drawersInstalled || _miAllDrawers == null) return;

            var player = Player.m_localPlayer;
            if (player == null) return;

            var inv = player.GetInventory();
            if (inv == null) return;

            var drawers = GetDrawersInRange(player.transform.position, CfgSearchRadius!.Value);
            if (drawers.Count == 0) return;

            var filled = new Dictionary<string, List<DrawerState>>();
            var empties = new List<DrawerState>();

            foreach (var znv in drawers)
            {
                var zdo = znv.GetZDO();
                if (zdo == null) continue;

                string prefab = zdo.GetString("Prefab") ?? string.Empty;
                int quality = zdo.GetInt("Quality", 1);

                var ds = new DrawerState(znv, prefab, quality);

                if (string.IsNullOrEmpty(prefab))
                {
                    empties.Add(ds);
                    continue;
                }

                string key = MakeKey(prefab, quality);
                if (!filled.TryGetValue(key, out var list))
                    filled[key] = list = new List<DrawerState>();

                list.Add(ds);
            }

            foreach (var item in inv.GetAllItems().ToList())
            {
                if (item.m_stack <= 0) continue;
                if (item.m_customData?.Count > 0) continue;

                string? prefabMaybe = item.m_dropPrefab?.name;
                if (string.IsNullOrEmpty(prefabMaybe)) continue;

                string prefab = prefabMaybe;
                int q = item.m_quality;
                string key = MakeKey(prefab, q);

                DrawerState? target = null;

                if (filled.TryGetValue(key, out var list) && list.Count > 0)
                    target = list.OrderBy(d => (d.NView.transform.position - player.transform.position).sqrMagnitude).First();

                if (target == null && CfgFillEmptyDrawers!.Value && empties.Count > 0)
                {
                    target = empties[0];
                    empties.RemoveAt(0);
                    filled[key] = new List<DrawerState> { target };
                }

                if (target == null) continue;

                int amount = item.m_stack;
                int prevAmount = target.NView.GetZDO()?.GetInt("Amount", 0) ?? 0;

                var backup = CloneItem(item);
                if (backup == null) continue;

                if (!TryDepositToDrawer(player, inv, item, target.NView, prefab, q, amount))
                    continue;

                player.StartCoroutine(CheckPlacementCoroutine(
                    target.NView,
                    prefab,
                    q,
                    amount,
                    prevAmount,
                    backup,
                    inv,
                    player));
            }
        }

        private static IEnumerator CheckPlacementCoroutine(
            ZNetView drawer,
            string prefab,
            int quality,
            int expected,
            int before,
            ItemDrop.ItemData backup,
            Inventory inv,
            Player player)
        {
            yield return new WaitForSeconds(CfgPlacementCheckDelay!.Value);

            var zdo = drawer.GetZDO();
            if (zdo == null) yield break;

            int delta = zdo.GetInt("Amount", 0) - before;
            if (delta >= expected) yield break;

            int restore = expected - Math.Max(0, delta);
            Recover(player, inv, backup, restore);
        }

        private static void Recover(Player player, Inventory inv, ItemDrop.ItemData proto, int amount)
        {
            var item = CloneItem(proto);
            if (item == null) return;

            item.m_stack = amount;
            if (!inv.AddItem(item))
            {
                var drop = ItemDrop.DropItem(item, amount,
                    player.transform.position + Vector3.up,
                    Quaternion.identity);
                drop?.OnPlayerDrop();
            }
        }

        private static List<ZNetView> GetDrawersInRange(Vector3 pos, float r)
        {
            if (_miAllDrawers!.Invoke(null, null) is not List<ZNetView> raw)
                return new List<ZNetView>();

            float r2 = r * r;
            return raw.Where(z => z != null && z.IsValid() &&
                (z.transform.position - pos).sqrMagnitude <= r2).ToList();
        }

        private static bool TryDepositToDrawer(
            Player player,
            Inventory inv,
            ItemDrop.ItemData item,
            ZNetView drawer,
            string prefab,
            int quality,
            int amount)
        {
            drawer.ClaimOwnership();
            inv.RemoveItem(item);
            drawer.InvokeRPC("AddItem_Request", prefab, amount, quality);
            return true;
        }

        private static ItemDrop.ItemData? CloneItem(ItemDrop.ItemData src)
        {
            return MemberwiseCloneMethod == null
                ? null
                : (ItemDrop.ItemData)MemberwiseCloneMethod.Invoke(src, Array.Empty<object>());
        }

        private static string MakeKey(string prefab, int q) => prefab + "|" + q;

        private sealed class DrawerState
        {
            public ZNetView NView { get; }
            public string Prefab { get; }
            public int Quality { get; }

            public DrawerState(ZNetView n, string p, int q)
            {
                NView = n;
                Prefab = p;
                Quality = q;
            }
        }
    }
}
