using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Harmony;
using HugsLib;
using RimWorld;
using UnityEngine;
using Verse;

//using HugsLib.Settings;

namespace ResourceGoalTracker
{
    class ResourceGoalTracker : ModBase
    {
        static Goal goal;
        public override string ModIdentifier => "COResourceGoalTracker";

        public override void DefsLoaded() {
            //SettingHandle<T> GetSettingHandle<T>(string settingName, T defaultValue) {
            //    return Settings.GetHandle(settingName, $"COSRT_{settingName}Setting_title".Translate(), $"COSRT_{settingName}Setting_description".Translate(), defaultValue);
            //}

            //goal = new Goal(ShipUtility.RequiredParts());
            goal = new Goal(new Dictionary<ThingDef, int> {{ThingDefOf.Ship_Reactor, 1}});
        }

        class Goal
        {
            static readonly Dictionary<ThingDef, List<ThingDefCountClass>> thingDefIngredientCounts = new Dictionary<ThingDef, List<ThingDefCountClass>>();
            Action CustomCounterTick;
            Dictionary<ThingDef, int> parts;
            public Dictionary<ThingDef, int> resourceAmounts;

            public Goal(Dictionary<ThingDef, int> parts) {
                UpdateParts(parts, null, true);
            }

            void UpdateParts(Dictionary<ThingDef, int> newParts, Action customCounterTick = null, bool updateThingDefIngredientCounts = false) {
                parts = newParts;
                CustomCounterTick = customCounterTick;
                if (updateThingDefIngredientCounts)
                    UpdateThingDefIngredientCounts(newParts);
            }

            public void CounterTick() {
                CustomCounterTick?.Invoke();
                UpdateResourceAmounts();
            }

            static void UpdateThingDefIngredientCounts(Dictionary<ThingDef, int> parts) {
                foreach (var part in parts)
                foreach (var cost in part.Key.costList) {
                    // todo use FirstOrDefault, make right clicking product allow changing recipe to create it
                    if (thingDefIngredientCounts.ContainsKey(cost.thingDef)) continue;
                    var recipe = DefDatabase<RecipeDef>.AllDefs.SingleOrDefault(r => r.products.Any(tc => tc.thingDef == cost.thingDef));
                    if (recipe == null) continue;
                    var ingredientCounts = new List<ThingDefCountClass>();
                    thingDefIngredientCounts[cost.thingDef] = ingredientCounts;
                    ingredientCounts.AddRange(
                        from ingredientCount in recipe.ingredients
                        from thingDef in ingredientCount.filter.AllowedThingDefs
                        let count = ingredientCount.CountRequiredOfFor(thingDef, recipe)
                        select new ThingDefCountClass(thingDef, count));
                }
            }

            // todo? live update from trade menu pre-confirm
            public void UpdateResourceAmounts() {
                var countedParts = CountAll(Find.CurrentMap, parts.Keys, false, true);
                var remainingParts = new Dictionary<ThingDef, int>();
                foreach (var part in parts)
                    remainingParts[part.Key] = Math.Max(part.Value - countedParts.TryGetValue(part.Key), 0);

                var costs = new Dictionary<ThingDef, int>();
                foreach (var part in remainingParts)
                foreach (var cost in part.Key.costList)
                    costs[cost.thingDef] = costs.TryGetValue(cost.thingDef) + part.Value * cost.count;

                // base on costs so we have ALL keys
                var deepCosts = new Dictionary<ThingDef, int>(costs);
                foreach (var cost in costs) {
                    if (!thingDefIngredientCounts.ContainsKey(cost.Key)) continue;
                    var ingredientCounts = thingDefIngredientCounts[cost.Key];
                    // deep counts of only remaining things
                    var costCount = Math.Max(cost.Value - Find.CurrentMap.resourceCounter.GetCount(cost.Key), 0);
                    foreach (var ingredientCount in ingredientCounts)
                        deepCosts[ingredientCount.thingDef] = costs.TryGetValue(ingredientCount.thingDef) + costCount * ingredientCount.count;
                }

                var remainingCosts = deepCosts.ToDictionary(cost => cost.Key, cost => Math.Max(cost.Value - Find.CurrentMap.resourceCounter.GetCount(cost.Key), 0));
                resourceAmounts = remainingCosts;
            }

            static Dictionary<ThingDef, int> CountAll(Map map, IEnumerable<ThingDef> thingDefs, bool includeEquipped, bool includeBuildings) {
                var result = new Dictionary<ThingDef, int>();
                foreach (var thingDef in thingDefs) {
                    int asResourceCount = 0, count = 0, minifiedCount = 0, equippedCount = 0, wornCount = 0, directlyHeldCount = 0;
                    if (thingDef.CountAsResource) {
                        asResourceCount = map.resourceCounter.GetCount(thingDef);
                    } else {
                        count = map.listerThings.ThingsOfDef(thingDef).Where(x => includeBuildings || !(x is Building)).Sum(x => x.stackCount);
                        minifiedCount = map.listerThings.ThingsInGroup(ThingRequestGroup.MinifiedThing).Cast<MinifiedThing>().Where(x => x.InnerThing.def == thingDef)
                            .Select(x => x.stackCount * x.InnerThing.stackCount).Sum();
                    }

                    var carriedCount = map.mapPawns.FreeColonistsSpawned.Where(x => x.carryTracker.CarriedThing?.def == thingDef).Select(x => x.carryTracker.CarriedThing)
                        .Sum(x => x.stackCount * (x is MinifiedThing minifiedThing ? minifiedThing.stackCount : 1));

                    if (includeEquipped) {
                        equippedCount = map.mapPawns.FreeColonistsSpawned.SelectMany(x => x.equipment.AllEquipmentListForReading).Where(x => x.def == thingDef).Sum(x => x.stackCount);
                        wornCount = map.mapPawns.FreeColonistsSpawned.SelectMany(x => x.apparel.WornApparel).Where(x => x.def == thingDef).Sum(x => x.stackCount);
                        directlyHeldCount = map.mapPawns.FreeColonistsSpawned.SelectMany(x => x.inventory.GetDirectlyHeldThings()).Where(x => x.def == thingDef).Sum(x => x.stackCount);
                    }

                    var totalCount = asResourceCount + count + minifiedCount + carriedCount + equippedCount + wornCount + directlyHeldCount;
                    result.Add(thingDef, totalCount);
                }

                return result;
            }

            public static FloatMenu FloatMenu(ThingDef iconThingDef) {
                var options = new List<FloatMenuOption>();

                var reactorOnly = new FloatMenuOption($"1 {ThingDefOf.Ship_Reactor.label}", () => { goal.UpdateParts(new Dictionary<ThingDef, int> {{ThingDefOf.Ship_Reactor, 1}}); });
                options.Add(reactorOnly);

                var shipParts = new Dictionary<ThingDef, int>(ShipUtility.RequiredParts());
                var casketCount = shipParts.TryGetValue(ThingDefOf.Ship_CryptosleepCasket);
                var label = casketCount > 0 ? $"ship minimum ({casketCount} {ThingDefOf.Ship_CryptosleepCasket.label})" : "ship";
                var shipMinColonists = new FloatMenuOption(label, () => { goal.UpdateParts(shipParts); });
                options.Add(shipMinColonists);

                if (casketCount > 0) {
                    label = $"ship for map colonists ({Find.CurrentMap.mapPawns.FreeColonistsCount} {ThingDefOf.Ship_CryptosleepCasket.label})";
                    var shipMapColonists = new FloatMenuOption(
                        label, () => { goal.UpdateParts(shipParts, () => { goal.parts[ThingDefOf.Ship_CryptosleepCasket] = Find.CurrentMap.mapPawns.FreeColonistsCount; }); });
                    options.Add(shipMapColonists);

                    var allColonistsCount = PawnsFinder.AllMapsCaravansAndTravelingTransportPods_Alive_FreeColonists.Count();
                    label = $"ship for all colonists ({allColonistsCount} {ThingDefOf.Ship_CryptosleepCasket.label})";
                    var shipAllColonists = new FloatMenuOption(
                        label,
                        () => {
                            goal.UpdateParts(shipParts, () => { goal.parts[ThingDefOf.Ship_CryptosleepCasket] = PawnsFinder.AllMapsCaravansAndTravelingTransportPods_Alive_FreeColonists.Count(); });
                        });
                    options.Add(shipAllColonists);
                }

                var result = new FloatMenu(options);
                return result;
            }
        }

        [HarmonyPatch(typeof(ResourceCounter), nameof(ResourceCounter.ResourceCounterTick))]
        static class ResourceCounter_ResourceCounterTick_Patch
        {
            [HarmonyPostfix]
            static void CounterTick() {
                goal.CounterTick();
            }
        }

        [HarmonyPatch(typeof(ResourceReadout), nameof(ResourceReadout.ResourceReadoutOnGUI))]
        static class ResourceReadout_ResourceReadoutOnGUI_Patch
        {
            static readonly MethodInfo DrawIconMethod = AccessTools.Method(typeof(ResourceReadout), "DrawIcon");
            static float lastDrawnHeight;
            static Vector2 scrollPosition;

            [HarmonyPostfix]
            static void ResourceGoalReadout(ResourceReadout __instance) {
                if (Event.current.type == EventType.layout) return;
                if (Current.ProgramState != ProgramState.Playing) return;
                if (Find.MainTabsRoot.OpenTab == MainButtonDefOf.Menu) return;

                GenUI.DrawTextWinterShadow(new Rect(256f, 512f, -256f, -512f)); // copied from ResourceReadout, not sure exactly
                Text.Font = GameFont.Small;

                var readoutRect = new Rect(120f + 7f, 7f, 110f, UI.screenHeight - 7 - 200f);
                var viewRect = new Rect(0f, 0f, readoutRect.width, lastDrawnHeight);
                var needScroll = viewRect.height > readoutRect.height;
                if (needScroll) {
                    Widgets.BeginScrollView(readoutRect, ref scrollPosition, viewRect, false);
                } else {
                    scrollPosition = Vector2.zero;
                    GUI.BeginGroup(readoutRect);
                }

                GUI.BeginGroup(viewRect);
                Text.Anchor = TextAnchor.MiddleLeft;
                DrawResource(__instance, readoutRect, out lastDrawnHeight);
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.EndGroup();

                if (needScroll)
                    Widgets.EndScrollView();
                else
                    GUI.EndGroup();
            }

            static void DrawResource(ResourceReadout __instance, Rect readoutRect, out float drawHeight) {
                drawHeight = 0f;
                foreach (var amount in goal.resourceAmounts) {
                    if (amount.Value <= 0) continue;
                    var iconRect = new Rect(0f, drawHeight, 999f, 24f);
                    if (iconRect.yMax >= scrollPosition.y && iconRect.y <= scrollPosition.y + readoutRect.height) {
                        DrawIconMethod.Invoke(__instance, new object[] {iconRect.x, iconRect.y, amount.Key});
                        iconRect.y += 2f;
                        var labelRect = new Rect(34f, iconRect.y, iconRect.width - 34f, iconRect.height);
                        Widgets.Label(labelRect, amount.Value.ToStringCached());
                    }

                    drawHeight += 24f;

                    if (Event.current.type == EventType.MouseUp && Event.current.button == 1 && Mouse.IsOver(new Rect(iconRect.x, iconRect.y, 50f, 24f))) {
                        Event.current.Use();
                        Find.WindowStack.Add(Goal.FloatMenu(amount.Key));
                    }
                }
            }
        }
    }
}