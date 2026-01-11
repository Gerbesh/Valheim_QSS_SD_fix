#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace QuickStackStoreItemDrawersCompat
{
    [BepInPlugin(ModGuid, ModName, ModVersion)]
    [BepInDependency("goldenrevolver.quick_stack_store", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("kg.ItemDrawers", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class QuickStackStoreItemDrawersCompatPlugin : BaseUnityPlugin
    {
        public const string ModGuid = "pavel.quickstackstore.itemdrawers_compat";
        public const string ModName = "QSS - ItemDrawers Compat (UseItem, MP-safe)";
        public const string ModVersion = "1.0.3";

        internal static ConfigEntry<float> SearchRadius = null!;
        internal static ConfigEntry<bool> IncludeHotbar = null!;
        internal static ConfigEntry<int> MaxDrawersToCheck = null!;
        internal static ConfigEntry<bool> PreferQssRadiusIfAvailable = null!;
        internal static ConfigEntry<bool> FillEmptyDrawers = null!;
        internal static ConfigEntry<bool> DebugLogging = null!;

        private void Awake()
        {
            SearchRadius = Config.Bind(
                "General",
                "SearchRadius",
                10f,
                "Drawer search radius (meters). Used if PreferQssRadiusIfAvailable=false, or if QSS radius cannot be read.");

            IncludeHotbar = Config.Bind(
                "General",
                "IncludeHotbar",
                false,
                "If true, quickstack will also move items from hotbar.");

            MaxDrawersToCheck = Config.Bind(
                "General",
                "MaxDrawersToCheck",
                200,
                "Safety limit: max number of drawers to evaluate (nearest first).");

            PreferQssRadiusIfAvailable = Config.Bind(
                "General",
                "PreferQssRadiusIfAvailable",
                true,
                "If true, tries to read QSS nearby-range config and use it instead of SearchRadius.");

            FillEmptyDrawers = Config.Bind(
                "General",
                "FillEmptyDrawers",
                false,
                "If false: only drawers that are already set to the same item will be used. If true: if no matching drawer exists, may place into empty drawers.");

            DebugLogging = Config.Bind(
                "General",
                "DebugLogging",
                false,
                "Enable debug logging.");

            Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), ModGuid);
            Logger.LogInfo($"{ModName} v{ModVersion} loaded");
        }

        internal static void Dbg(string msg)
        {
            if (!DebugLogging.Value) return;
            BepInEx.Logging.Logger.CreateLogSource(ModName).LogInfo("[QSS-DrawersCompat] " + msg);
        }
    }

    [HarmonyPatch]
    internal static class QssReportPatch
    {
        [HarmonyTargetMethod]
        private static MethodBase? TargetMethod()
        {
            // QuickStackStore.QuickStackModule.ReportQuickStackResult(Player player, int movedCount)
            var type = AccessTools.TypeByName("QuickStackStore.QuickStackModule");
            return type == null ? null : AccessTools.Method(type, "ReportQuickStackResult", new[] { typeof(Player), typeof(int) });
        }

        [HarmonyPrefix]
        private static void Prefix(Player player, ref int movedCount)
        {
            try
            {
                if (player == null) return;

                // Если ItemDrawers не стоит - ничего не делаем
                if (AccessTools.TypeByName("kg_ItemDrawers.DrawerComponent") == null) return;

                int added = DrawerQuickStacker.TryQuickStackIntoNearbyDrawers(player);
                if (added > 0) movedCount += added;
            }
            catch (Exception e)
            {
                Debug.LogError($"[QSS-DrawersCompat] Error: {e}");
            }
        }
    }

    internal static class DrawerQuickStacker
    {
        private const string DrawerTypeName = "kg_ItemDrawers.DrawerComponent";

        private static MethodInfo? _useItemMethod;

        private static MethodInfo? _getInventoryMethod;
        private static FieldInfo? _inventoryField;

        private static PropertyInfo? _piCurrentPrefab;
        private static PropertyInfo? _piCurrentAmount;

        public static int TryQuickStackIntoNearbyDrawers(Player player)
        {
            var drawerType = AccessTools.TypeByName(DrawerTypeName);
            if (drawerType == null) return 0;

            _useItemMethod ??= AccessTools.Method(drawerType, "UseItem", new[] { typeof(Humanoid), typeof(ItemDrop.ItemData) });
            if (_useItemMethod == null) return 0;

            _piCurrentPrefab ??= AccessTools.Property(drawerType, "CurrentPrefab");
            _piCurrentAmount ??= AccessTools.Property(drawerType, "CurrentAmount");

            float range = GetEffectiveRange();
            if (range <= 0.05f) return 0;

            // Находим drawers
            var all = UnityEngine.Object.FindObjectsByType(drawerType, FindObjectsSortMode.None);
            if (all == null || all.Length == 0) return 0;

            int maxDrawers = Math.Max(1, QuickStackStoreItemDrawersCompatPlugin.MaxDrawersToCheck.Value);

            var drawerInfos = all
                .Select(o => o as Component)
                .Where(c => c != null && c.gameObject != null && c.gameObject.activeInHierarchy)
                .Select(c => new DrawerInfo(c!, Vector3.Distance(player.transform.position, c!.transform.position), ReadCurrentPrefab(c!), ReadCurrentAmount(c!)))
                .Where(x => x.Distance <= range)
                .OrderBy(x => x.Distance)
                .Take(maxDrawers)
                .ToList();

            if (drawerInfos.Count == 0) return 0;

            QuickStackStoreItemDrawersCompatPlugin.Dbg($"Nearby drawers found: {drawerInfos.Count} (radius={range}, max={maxDrawers})");

            // Берем inventory игрока
            var inv = GetHumanoidInventory(player);
            if (inv == null) return 0;

            var items = inv.GetAllItems()?.ToList();
            if (items == null || items.Count == 0) return 0;

            bool includeHotbar = QuickStackStoreItemDrawersCompatPlugin.IncludeHotbar.Value;

            // Кандидаты - не трогаем предметы с customData (важно для гемов/модов/перков)
            var candidates = items
                .Where(i => i != null)
                .Where(i => i!.m_stack > 0)
                .Where(i => includeHotbar || !IsHotbarItem(i!))
                .Where(i => i!.m_customData == null || i.m_customData.Count == 0)
                .Where(i => i!.m_dropPrefab != null && !string.IsNullOrWhiteSpace(i.m_dropPrefab.name))
                .ToList();

            if (candidates.Count == 0) return 0;

            // Индексы drawers:
            // 1) drawers, уже назначенные под конкретный prefab (CurrentPrefab)
            // 2) пустые drawers (CurrentPrefab пустой или Amount==0) - опционально
            var drawersByPrefab = new Dictionary<string, List<DrawerInfo>>(StringComparer.Ordinal);
            var emptyDrawers = new List<DrawerInfo>();

            foreach (var d in drawerInfos)
            {
                if (!string.IsNullOrEmpty(d.CurrentPrefab))
                {
                    if (!drawersByPrefab.TryGetValue(d.CurrentPrefab, out var list))
                    {
                        list = new List<DrawerInfo>();
                        drawersByPrefab[d.CurrentPrefab] = list;
                    }
                    list.Add(d);
                }
                else
                {
                    emptyDrawers.Add(d);
                }
            }

            // На всякий - сортируем внутри каждой группы по расстоянию
            foreach (var kv in drawersByPrefab)
                kv.Value.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            emptyDrawers.Sort((a, b) => a.Distance.CompareTo(b.Distance));

            bool allowEmpty = QuickStackStoreItemDrawersCompatPlugin.FillEmptyDrawers.Value;

            int movedStacks = 0;

            foreach (var item in candidates)
            {
                if (item == null) continue;
                if (!inv.ContainsItem(item)) continue;

                string prefab = item.m_dropPrefab!.name;

                bool movedThisStack = false;

                // 1) Сначала только drawers, которые уже помечены этим prefab
                if (drawersByPrefab.TryGetValue(prefab, out var matching) && matching.Count > 0)
                {
                    foreach (var drawer in matching)
                    {
                        if (TryUseItem(player, item, drawer.Comp))
                        {
                            movedStacks += 1;
                            movedThisStack = true;
                            break;
                        }
                    }
                }

                if (movedThisStack) continue;

                // 2) Только если разрешено - используем пустые drawers
                if (allowEmpty && emptyDrawers.Count > 0)
                {
                    foreach (var drawer in emptyDrawers)
                    {
                        if (TryUseItem(player, item, drawer.Comp))
                        {
                            movedStacks += 1;
                            movedThisStack = true;

                            // После успешного UseItem этот drawer станет "не пустым".
                            // Обновим наши индексы, чтобы следующие стаки шли в него же, а не в другой пустой.
                            string newPrefab = ReadCurrentPrefab(drawer.Comp);
                            int newAmount = ReadCurrentAmount(drawer.Comp);

                            if (!string.IsNullOrEmpty(newPrefab) && newAmount > 0)
                            {
                                emptyDrawers.Remove(drawer);

                                if (!drawersByPrefab.TryGetValue(newPrefab, out var list))
                                {
                                    list = new List<DrawerInfo>();
                                    drawersByPrefab[newPrefab] = list;
                                }
                                list.Add(new DrawerInfo(drawer.Comp, drawer.Distance, newPrefab, newAmount));
                                list.Sort((a, b) => a.Distance.CompareTo(b.Distance));
                            }

                            break;
                        }
                    }
                }
            }

            if (movedStacks > 0)
                QuickStackStoreItemDrawersCompatPlugin.Dbg($"Moved stacks into drawers: {movedStacks}");

            return movedStacks;
        }

        private static bool TryUseItem(Player player, ItemDrop.ItemData item, Component drawerComp)
        {
            try
            {
                // MP-safe: если у объекта есть ZNetView - заберем ownership перед UseItem
                var znv = drawerComp.GetComponent<ZNetView>();
                if (znv != null && znv.IsValid())
                    znv.ClaimOwnership();

                var okObj = _useItemMethod!.Invoke(drawerComp, new object[] { player, item });
                return okObj is bool ok && ok;
            }
            catch
            {
                return false;
            }
        }

        private static float GetEffectiveRange()
        {
            float fallback = Math.Max(0f, QuickStackStoreItemDrawersCompatPlugin.SearchRadius.Value);

            if (!QuickStackStoreItemDrawersCompatPlugin.PreferQssRadiusIfAvailable.Value)
                return fallback;

            float qss = TryGetQssNearbyRangeOrNaN();
            if (!float.IsNaN(qss) && qss > 0.05f) return qss;

            return fallback;
        }

        private static float TryGetQssNearbyRangeOrNaN()
        {
            // QuickStackStore.QSSConfig+QuickStackConfig.QuickStackToNearbyRange.Value
            try
            {
                var cfgType = AccessTools.TypeByName("QuickStackStore.QSSConfig+QuickStackConfig");
                if (cfgType == null) return float.NaN;

                var field = AccessTools.Field(cfgType, "QuickStackToNearbyRange");
                if (field == null) return float.NaN;

                var cfgEntry = field.GetValue(null);
                if (cfgEntry == null) return float.NaN;

                var valProp = cfgEntry.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public);
                if (valProp == null) return float.NaN;

                var v = valProp.GetValue(cfgEntry);
                return v is float f ? f : float.NaN;
            }
            catch
            {
                return float.NaN;
            }
        }

        private static Inventory? GetHumanoidInventory(Humanoid h)
        {
            // В разных версиях Valheim это бывает:
            // - protected field m_inventory
            // - публичный метод GetInventory()
            // Поэтому пробуем обе стратегии.

            try
            {
                _getInventoryMethod ??= AccessTools.Method(h.GetType(), "GetInventory", Type.EmptyTypes);
                if (_getInventoryMethod != null)
                {
                    var res = _getInventoryMethod.Invoke(h, Array.Empty<object>());
                    if (res is Inventory invA) return invA;
                }
            }
            catch { }

            try
            {
                _inventoryField ??= AccessTools.Field(h.GetType(), "m_inventory");
                if (_inventoryField != null)
                {
                    var res = _inventoryField.GetValue(h);
                    if (res is Inventory invB) return invB;
                }
            }
            catch { }

            return null;
        }

        private static bool IsHotbarItem(ItemDrop.ItemData item)
        {
            // m_gridPos - Vector2i. Не ссылаемся на тип, читаем через reflection
            try
            {
                var f = AccessTools.Field(item.GetType(), "m_gridPos");
                if (f == null) return false;

                var gp = f.GetValue(item);
                if (gp == null) return false;

                var yField = gp.GetType().GetField("y", BindingFlags.Instance | BindingFlags.Public);
                if (yField == null) return false;

                var yVal = yField.GetValue(gp);
                return yVal is int y && y == 0;
            }
            catch
            {
                return false;
            }
        }

        private static string ReadCurrentPrefab(Component drawerComp)
        {
            try
            {
                if (_piCurrentPrefab == null) return string.Empty;
                return _piCurrentPrefab.GetValue(drawerComp) as string ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static int ReadCurrentAmount(Component drawerComp)
        {
            try
            {
                if (_piCurrentAmount == null) return 0;
                var v = _piCurrentAmount.GetValue(drawerComp);
                return v is int i ? i : 0;
            }
            catch
            {
                return 0;
            }
        }

        private readonly struct DrawerInfo
        {
            public readonly Component Comp;
            public readonly float Distance;
            public readonly string CurrentPrefab;
            public readonly int CurrentAmount;

            public DrawerInfo(Component comp, float dist, string currentPrefab, int currentAmount)
            {
                Comp = comp;
                Distance = dist;
                CurrentPrefab = currentPrefab;
                CurrentAmount = currentAmount;
            }
        }
    }
}
