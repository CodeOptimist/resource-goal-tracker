using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Harmony;
using HugsLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using UnityEngine.SceneManagement;
using Verse;
using Debug = System.Diagnostics.Debug;

//using HugsLib.Settings;

namespace ResourceGoalTracker
{
    class ResourceGoalTracker : ModBase
    {
        static Goal curGoal;
        public override string ModIdentifier => "COResourceGoalTracker";

        public override void DefsLoaded() {
            //SettingHandle<T> GetSettingHandle<T>(string settingName, T defaultValue) {
            //    return Settings.GetHandle(settingName, $"CORGT_{settingName}Setting_title".Translate(), $"CORGT_{settingName}Setting_description".Translate(), defaultValue);
            //}
        }

        public override void SceneLoaded(Scene scene) {
            if (GenScene.InPlayScene)
                curGoal = new Goal(); // blank for loading
        }

        class ResourceSettings : WorldComponent
        {
            static Dictionary<ThingDef, RecipeDef> thingsRecipes = new Dictionary<ThingDef, RecipeDef>();

            public ResourceSettings(World world) : base(world) {
            }

            public static RecipeDef RecipeFor(ThingDef thingDef) {
                if (thingsRecipes.TryGetValue(thingDef, out var recipe))
                    return recipe;
                recipe = DefDatabase<RecipeDef>.AllDefs.FirstOrDefault(r => r.products.Any(tc => tc.thingDef == thingDef));
                if (recipe != null)
                    thingsRecipes[thingDef] = recipe;
                return recipe;
            }

            public override void FinalizeInit() {
                // handles missing data from ExposeData()
                // also ExposeData() not being called (no WorldComponent in save yet)
                // also neither ExposeData() nor SceneLoaded() being called (creating a new game)
                if (curGoal?.neededParts == null || curGoal.neededParts.Count == 0)
                    curGoal = Goal.presets["shipMinColonists"];
            }

            public override void ExposeData() {
                string preset = null;
                if (Scribe.mode == LoadSaveMode.LoadingVars) {
                    Scribe_Values.Look(ref preset, "usePreset");
                    if (preset == null)
                        Scribe_Collections.Look(ref curGoal.neededParts, "goal", LookMode.Def, LookMode.Value);
                    else
                        curGoal = Goal.presets.TryGetValue(preset, curGoal);
                } else if (Scribe.mode == LoadSaveMode.Saving) {
                    preset = Goal.presets.Where(x => x.Value == curGoal).Select(x => x.Key).SingleOrDefault();
                    Scribe_Values.Look(ref preset, "usePreset");
                    // write even when we're using a preset as an editable example
                    if (curGoal.neededParts.Count > 0)
                        Scribe_Collections.Look(ref curGoal.neededParts, "goal", LookMode.Def, LookMode.Value);
                }

                Scribe_Collections.Look(ref thingsRecipes, "recipes", LookMode.Def, LookMode.Def);

                if (Scribe.mode == LoadSaveMode.PostLoadInit)
                    if (thingsRecipes == null)
                        thingsRecipes = new Dictionary<ThingDef, RecipeDef>();
            }
        }

        class Goal
        {
            static readonly Dictionary<RecipeDef, List<ThingDefCountClass>> recipesIngredientCounts = new Dictionary<RecipeDef, List<ThingDefCountClass>>();

            public static readonly Dictionary<string, Goal> presets = new Dictionary<string, Goal> {
                {"reactorOnly", new Goal(new Dictionary<ThingDef, int> {{ThingDefOf.Ship_Reactor, 1}})},
                {"shipMinColonists", new Goal(new Dictionary<ThingDef, int>(ShipUtility.RequiredParts()))}, {
                    "shipMapColonists", new Goal(
                        new Dictionary<ThingDef, int>(ShipUtility.RequiredParts()),
                        goal => { goal.neededParts[ThingDefOf.Ship_CryptosleepCasket] = Find.CurrentMap.mapPawns.FreeColonistsCount; })
                }, {
                    "shipAllColonists", new Goal(
                        new Dictionary<ThingDef, int>(ShipUtility.RequiredParts()),
                        goal => { goal.neededParts[ThingDefOf.Ship_CryptosleepCasket] = PawnsFinder.AllMapsCaravansAndTravelingTransportPods_Alive_FreeColonists.Count(); })
                },
            };

            readonly Action<Goal> CustomCounterTick;
            public Dictionary<ThingDef, int> neededParts = new Dictionary<ThingDef, int>(); // for loading
            public Dictionary<ThingDef, int> resourceAmounts;

            public Goal() {
            }

            Goal(Dictionary<ThingDef, int> neededParts, Action<Goal> customCounterTick = null) {
                this.neededParts = neededParts;
                CustomCounterTick = customCounterTick;
            }

            static List<ThingDefCountClass> IngredientCountsFor(RecipeDef recipe) {
                if (recipe == null) return null;
                if (recipesIngredientCounts.TryGetValue(recipe, out var ingredientCounts))
                    return ingredientCounts;

                ingredientCounts = new List<ThingDefCountClass>();
                recipesIngredientCounts[recipe] = ingredientCounts;
                ingredientCounts.AddRange(
                    from ingredientCount in recipe.ingredients
                    from thingDef in ingredientCount.filter.AllowedThingDefs
                    let count = ingredientCount.CountRequiredOfFor(thingDef, recipe)
                    select new ThingDefCountClass(thingDef, count));
                return ingredientCounts;
            }

            public void CounterTick() {
                CustomCounterTick?.Invoke(curGoal);
                UpdateAmounts();
            }

            // todo? live update from trade menu pre-confirm
            void UpdateAmounts() {
                var foundParts = CountAll(Find.CurrentMap, neededParts.Keys, null, false, true);
                var missingParts = new Dictionary<ThingDef, int>();
                foreach (var neededPart in neededParts)
                    missingParts[neededPart.Key] = Math.Max(neededPart.Value - foundParts.TryGetValue(neededPart.Key), 0);

                var neededShallowResources = new Dictionary<ThingDef, int>();
                foreach (var missingPart in missingParts)
                foreach (var neededShallowResource in missingPart.Key.costList) // of just missing parts
                    neededShallowResources[neededShallowResource.thingDef] = neededShallowResources.TryGetValue(neededShallowResource.thingDef) + missingPart.Value * neededShallowResource.count;

                var lookedInFrames = missingParts.Where(x => x.Value > 0).Select(x => x.Key.frameDef).ToList();
                var foundShallowResources = CountAll(Find.CurrentMap, neededShallowResources.Keys, lookedInFrames, false, false);

                // base on neededShallowResources so we have ALL keys together
                var neededResources = new Dictionary<ThingDef, int>(neededShallowResources);
                foreach (var neededShallowResource in neededShallowResources) {
                    var ingredientCounts = IngredientCountsFor(ResourceSettings.RecipeFor(neededShallowResource.Key));
                    if (ingredientCounts == null) continue;
                    var missingShallowResourceCount = Math.Max(neededShallowResource.Value - foundShallowResources[neededShallowResource.Key], 0);
                    foreach (var ingredientCount in ingredientCounts)  // of just missing shallow resources
                        neededResources[ingredientCount.thingDef] = neededShallowResources.TryGetValue(ingredientCount.thingDef) + missingShallowResourceCount * ingredientCount.count;
                }

                var newDefsFromIngredients = neededResources.Keys.Except(neededShallowResources.Keys);
                var foundDeepResources = CountAll(Find.CurrentMap, newDefsFromIngredients, lookedInFrames, false, false);
                var foundResources = foundShallowResources.Concat(foundDeepResources).ToDictionary(x => x.Key, x => x.Value);
                var missingResources = neededResources.ToDictionary(cost => cost.Key, cost => Math.Max(cost.Value - foundResources[cost.Key], 0));
                resourceAmounts = missingResources;

                //Debug.WriteLine($"Needed parts: {neededParts.ToStringFullContents()}");
                //Debug.WriteLine($"Found parts: {foundParts.ToStringFullContents()}");
                //Debug.WriteLine($"Missing parts: {missingParts.ToStringFullContents()}");
                //Debug.WriteLine($"Included frame containers: {lookedInFrames.ToStringSafeEnumerable()}");
                //Debug.WriteLine($"Needed shallow resources: {neededShallowResources.ToStringFullContents()}");
                //Debug.WriteLine($"Needed resources: {neededResources.ToStringFullContents()}");
                //Debug.WriteLine($"Found resources: {foundResources.ToStringFullContents()}");
                //Debug.WriteLine($"Missing resources: {missingResources.ToStringFullContents()}");
            }

            static Dictionary<ThingDef, int> CountAll(Map map, IEnumerable<ThingDef> thingDefs, List<ThingDef> lookedInFrames, bool includeEquipped, bool includeBuildings) {
                var result = new Dictionary<ThingDef, int>();
                foreach (var thingDef in thingDefs) {
                    int asResourceCount = 0, count = 0, minifiedCount = 0, framedCount = 0, equippedCount = 0, wornCount = 0, directlyHeldCount = 0;
                    if (lookedInFrames == null && thingDef.CountAsResource) {
                        asResourceCount = map.resourceCounter.GetCount(thingDef);
                    } else {
                        count = map.listerThings.ThingsOfDef(thingDef).Where(x => includeBuildings || !(x is Building)).Sum(x => x.stackCount);
                        minifiedCount = map.listerThings.ThingsInGroup(ThingRequestGroup.MinifiedThing).Cast<MinifiedThing>().Where(x => x.InnerThing.def == thingDef)
                            .Select(x => x.stackCount * x.InnerThing.stackCount).Sum();
                        if (lookedInFrames != null)
                            framedCount = map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame).Cast<Frame>().Where(x => lookedInFrames.Contains(x.def))
                                .Sum(x => x.stackCount * x.resourceContainer.TotalStackCountOfDef(thingDef));
                    }

                    var carriedCount = map.mapPawns.FreeColonistsSpawned.Where(x => x.carryTracker.CarriedThing?.def == thingDef).Select(x => x.carryTracker.CarriedThing)
                        .Sum(x => x.stackCount * (x is MinifiedThing minifiedThing ? minifiedThing.stackCount : 1));

                    if (includeEquipped) {
                        equippedCount = map.mapPawns.FreeColonistsSpawned.SelectMany(x => x.equipment.AllEquipmentListForReading).Where(x => x.def == thingDef).Sum(x => x.stackCount);
                        wornCount = map.mapPawns.FreeColonistsSpawned.SelectMany(x => x.apparel.WornApparel).Where(x => x.def == thingDef).Sum(x => x.stackCount);
                        directlyHeldCount = map.mapPawns.FreeColonistsSpawned.SelectMany(x => x.inventory.GetDirectlyHeldThings()).Where(x => x.def == thingDef).Sum(x => x.stackCount);
                    }

                    var totalCount = asResourceCount + count + minifiedCount + framedCount + carriedCount + equippedCount + wornCount + directlyHeldCount;
                    result.Add(thingDef, totalCount);
                }

                return result;
            }

            public static FloatMenu FloatMenu(ThingDef iconThingDef) {
                var options = MainFloatMenu();

                //todo list and choose recipe per thingdef, draw icons & quantities in float menu options of recipe
                var result = new FloatMenu(options);
                return result;
            }

            static List<FloatMenuOption> MainFloatMenu() {
                var result = new List<FloatMenuOption>();

                FloatMenuOption GoalFloatMenuOption(Goal goal, string label) {
                    return new FloatMenuOption(
                        (curGoal == goal ? "✔ " : "") + label, () => {
                            curGoal = goal;
                            curGoal.CounterTick(); // things need initialized before they are drawn
                        });
                }

                result.Add(GoalFloatMenuOption(presets["reactorOnly"], $"1 {ThingDefOf.Ship_Reactor.label}"));

                var casketCount = presets["shipMinColonists"].neededParts.TryGetValue(ThingDefOf.Ship_CryptosleepCasket);
                result.Add(GoalFloatMenuOption(presets["shipMinColonists"], casketCount > 0 ? $"ship minimum ({casketCount} {ThingDefOf.Ship_CryptosleepCasket.label})" : "ship"));

                if (casketCount > 0) {
                    result.Add(GoalFloatMenuOption(presets["shipMapColonists"], $"ship for map colonists ({Find.CurrentMap.mapPawns.FreeColonistsCount} {ThingDefOf.Ship_CryptosleepCasket.label})"));
                    var allColonistsCount = PawnsFinder.AllMapsCaravansAndTravelingTransportPods_Alive_FreeColonists.Count();
                    result.Add(GoalFloatMenuOption(presets["shipAllColonists"], $"ship for all colonists ({allColonistsCount} {ThingDefOf.Ship_CryptosleepCasket.label})"));
                }

                return result;
            }
        }

        [HarmonyPatch(typeof(ResourceCounter), nameof(ResourceCounter.ResourceCounterTick))]
        static class ResourceCounter_ResourceCounterTick_Patch
        {
            [HarmonyPostfix]
            static void CounterTick() {
                curGoal.CounterTick();
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
                if (curGoal.resourceAmounts == null) return;

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
                foreach (var amount in curGoal.resourceAmounts) {
                    if (amount.Value <= 0) continue;
                    var iconRect = new Rect(0f, drawHeight, 999f, 24f);
                    if (iconRect.yMax >= scrollPosition.y && iconRect.y <= scrollPosition.y + readoutRect.height) {
                        DrawIconMethod.Invoke(__instance, new object[] {iconRect.x, iconRect.y, amount.Key});
                        iconRect.y += 2f;
                        var labelRect = new Rect(34f, iconRect.y, iconRect.width - 34f, iconRect.height);
                        Widgets.Label(labelRect, amount.Value.ToStringCached());
                    }

                    drawHeight += 24f;

                    if (Event.current.type == EventType.MouseUp && Event.current.button == 1 && Mouse.IsOver(new Rect(iconRect.x, iconRect.y, 100f, 24f))) {
                        Event.current.Use();
                        Find.WindowStack.Add(Goal.FloatMenu(amount.Key));
                    }
                }
            }
        }
    }
}