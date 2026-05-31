using System;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using System.Reflection;
using AlteredDestination.Logic;

namespace AlteredDestination
{
    // Custom class to hold either a static coordinate or a dynamically tracked unit
    public class OverrideData
    {
        public WaypointRouteState routeState;
        public Unit targetUnit;
        public bool triedTargettingUnit;
    }

    [BepInPlugin("com.checkpointcharlie.cruisemissile", "Checkpoint Charlie's Cruise Missile (Alternate destination)", "1.2.1")]
    public class AlteredDestinationPlugin : BaseUnityPlugin
    {
        public static ConditionalWeakTable<Missile, OverrideData> MissileWaypoints = new ConditionalWeakTable<Missile, OverrideData>();
        public static AlteredDestinationPlugin Instance;
        public static ConfigEntry<float> CruiseAltitude;
        public static ConfigEntry<float> MinimumAltitude;
        public static ConfigEntry<float> SpreadRadius;
        public static ConfigEntry<bool> DoJink;
        public static ConfigEntry<bool> DoTopattack;
        public static ConfigEntry<int> WaypointSteps;
        public static ConfigEntry<float> WobbleActivationDistance;
        public static ConfigEntry<int> WobbleRange;
        public static ConfigEntry<bool> DebugOutput;
        public static ConfigEntry<double> MaxBendAngle;

        private void Awake()
        {
            Instance = this;

            CruiseAltitude = Config.Bind("General", "Cruise Altitude", 5f, new ConfigDescription("Target radar altitude for cruise missiles in meters. Lower altitude increases the risk of terrain collision.", new AcceptableValueRange<float>(3f, 15f)));
            MinimumAltitude = Config.Bind("General", "Minimum Altitude", 3f, new ConfigDescription("Minimum radar altitude for cruise missiles in meters before an emergency pullup.", new AcceptableValueRange<float>(1f, 3f)));
            SpreadRadius = Config.Bind("General", "Spread Radius", 15f, "Radius in meters to spread out missiles targeting the same location to prevent stacking.");
            DoJink = Config.Bind("General", "Jinking maneuver in terminal approach", false, "Off (set as default) = No jink, On = Random jinking");
            DoTopattack = Config.Bind("General", "Top attack popup maneuver in terminal approach", false, "Off (set as default) = No top attack, On = Top attack popup");
            //WaypointRadius = Config.Bind("General", "Waypoint radius", 300f, new ConfigDescription("Distance to waypoint to switch to next one", new AcceptableValueRange<float>(260f, 2600f)));
            WaypointSteps = Config.Bind("General", "Waypoint steps", 5, new ConfigDescription("Number of smoothing steps to do on a waypoint", new AcceptableValueRange<int>(1, 20)));
            WobbleActivationDistance = Config.Bind("General", "Wobble activation distance", 5000.0f, new ConfigDescription("Enable random wobble when midpoint distance to target falls below this threshold.", new AcceptableValueRange<float>(0.0f, 50000.0f)));
            WobbleRange = Config.Bind("General", "Wobble range", 500, new ConfigDescription("Random wobble offset range on X/Z while leading in (generated between -range and +range).", new AcceptableValueRange<int>(0, 5000)));
            MaxBendAngle = Config.Bind("General", "Max bend angle", 40.0, new ConfigDescription("Maximum angle between waypoints compared to a straight line in degrees. Eveything over this will get smoothed out", new AcceptableValueRange<double>(0, 180)));
            DebugOutput = Config.Bind("General", "Debug logging", true);

            var harmony = new Harmony("com.checkpointcharlie.cruisemissile");
            harmony.PatchAll();
            Logger.LogInfo("Checkpoint Charlie's Cruise Missile Mod Loaded!");

            foreach (var f in AccessTools.GetFieldNames(typeof(DynamicMap))) {
                FieldInfo field = AccessTools.Field(typeof(DynamicMap), f);
                Type fieldType = field.FieldType;
                Logger.LogInfo($"field: {f} {fieldType.FullName}");
            }

            foreach (var m in AccessTools.GetMethodNames(typeof(DynamicMap))) {
                var method = AccessTools.Method(typeof(DynamicMap), m);
                var args = string.Join(", ", System.Array.ConvertAll(
                    method.GetParameters(), 
                    p => $"{p.ParameterType.Name} {p.Name}"
                ));
                Logger.LogInfo($"method: {m}({args}) returns {method.ReturnType.Name}");
            }
        }

        public static void Log(string message)
        {
            Instance.Logger.LogInfo(message);
        }
        public static void Debug(string message)
        {
            if (DebugOutput.Value) {
                Instance.Logger.LogDebug(message);
            }
        }
    }

    [HarmonyPatch(typeof(DynamicMap), "MapControls")]
    public static class DynamicMap_MapControls_Patch
    {
        public static void Postfix(DynamicMap __instance)
        {
            // Detect right-click on maximized map
            if (DynamicMap.mapMaximized && Input.GetMouseButtonDown(1))
            {
                GlobalPosition cursorCoords;
                if (__instance.TryGetCursorCoordinates(out cursorCoords))
                {
                    bool clearWaypoint = Input.GetKey(KeyCode.RightShift);
                    bool setAny = false;

                    // Terrain-Aware Waypoint: 
                    Vector3 localClick = cursorCoords.ToLocalPosition();
                    float terrainHeight = (float)cursorCoords.y;
                    Vector3 rayOrigin = new Vector3(localClick.x, 20000f, localClick.z);
                    
                    if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, 30000f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                    {
                        terrainHeight = hit.point.ToGlobalPosition().y;
                    }
                    else if (Terrain.activeTerrain != null)
                    {
                        terrainHeight = Terrain.activeTerrain.SampleHeight(localClick);
                        terrainHeight = new Vector3(localClick.x, terrainHeight, localClick.z).ToGlobalPosition().y;
                    }
                    
                    cursorCoords.y = terrainHeight;

                    Unit[] allUnits = UnityEngine.Object.FindObjectsOfType<Unit>();
                    FieldInfo seekerField = AccessTools.Field(typeof(Missile), "seeker");

                    foreach (var baseIcon in __instance.selectedIcons)
                    {
                        if (baseIcon is UnitMapIcon unitIcon && unitIcon.unit is Missile missile)
                        {

                            var seekerObj = seekerField.GetValue(unitIcon.unit);
                            OpticalSeekerCruiseMissile cSeeker = seekerObj as OpticalSeekerCruiseMissile;
                            
                            if (cSeeker == null) {
                                continue; // this is not a cruise missile, do not mess with it
                            }

                            if (clearWaypoint)
                            {
                                AlteredDestinationPlugin.MissileWaypoints.Remove(missile);
                                AlteredDestinationPlugin.Log("Waypoint cleared for missile.");
                                break;
                            }

                            Unit closestEnemy = null;
                            float closestDist = 100f; // 100m radius for the pillar scan

                            foreach (Unit u in allUnits)
                            {
                                if (u == null || u == missile || u.gameObject == null || !u.gameObject.activeInHierarchy) continue;
                                if (u.NetworkHQ == missile.NetworkHQ) continue;

                                GlobalPosition uPos = u.GlobalPosition();
                                
                                float dx = (float)(uPos.x - cursorCoords.x);
                                float dz = (float)(uPos.z - cursorCoords.z);
                                float dist2D = Mathf.Sqrt(dx * dx + dz * dz);

                                if (dist2D < closestDist)
                                {
                                    closestDist = dist2D;
                                    closestEnemy = u;
                                }
                            }

                            OverrideData data;
                            bool hasValue = AlteredDestinationPlugin.MissileWaypoints.TryGetValue(missile, out data);

                            if (!hasValue) {
                                data = new OverrideData()
                                {
                                    routeState = new WaypointRouteState(),
                                    targetUnit = closestEnemy,
                                };
                                data.routeState.Waypoints.Add(new Waypoint2D(cursorCoords.x, cursorCoords.z));
                            } else {
                                if (data.routeState == null)
                                {
                                    data.routeState = new WaypointRouteState();
                                }

                                if (data.routeState.Waypoints.Count < 3) {
                                    data.routeState.Waypoints.Add(new Waypoint2D(cursorCoords.x, cursorCoords.z));
                                    AlteredDestinationPlugin.Log("Adding waypoint directly");
                                }
                                else if (data.routeState.Waypoints.Count == 3) {
                                    data.routeState.Waypoints.Add(new Waypoint2D(cursorCoords.x, cursorCoords.z));
                                    Waypoint2D oldwp = data.routeState.Waypoints[data.routeState.Waypoints.Count-2];
                                    // replace second last waypoint with a smoothed version
                                    data.routeState.Waypoints[data.routeState.Waypoints.Count-2] = MissileNavigationLogic.MidpointUnderBendAngle(
                                        data.routeState.Waypoints[data.routeState.Waypoints.Count-3],
                                        data.routeState.Waypoints[data.routeState.Waypoints.Count-2],
                                        data.routeState.Waypoints[data.routeState.Waypoints.Count-1],
                                        AlteredDestinationPlugin.MaxBendAngle.Value);

                                    Waypoint2D newwp = data.routeState.Waypoints[data.routeState.Waypoints.Count-2];
                                    AlteredDestinationPlugin.Log($"Debending mid waypoint {oldwp.X}, {oldwp.Z} to {newwp.X} {newwp.Z}");
                                }
                                else { // more than three waypoints need to be split up to not disturb previous angles
                                    Waypoint2D newestWp = new Waypoint2D(cursorCoords.x, cursorCoords.z);
                                    List<Waypoint2D> bendPoints = MissileNavigationLogic.PointsUnderBendAngle(
                                        data.routeState.Waypoints[data.routeState.Waypoints.Count-2],
                                        data.routeState.Waypoints[data.routeState.Waypoints.Count-1],
                                        newestWp,
                                        90.0
                                    );
                                    data.routeState.Waypoints.Remove(data.routeState.Waypoints[data.routeState.Waypoints.Count-1]); // remove previously existing midpoint
                                    data.routeState.Waypoints.AddRange(bendPoints);
                                    data.routeState.Waypoints.Add(newestWp);
                                    AlteredDestinationPlugin.Log($"Splitting mid waypoint into {bendPoints.Count}");
                                }

                                if (closestEnemy != null) {
                                    data.targetUnit = closestEnemy;
                                }
                            }

                            if (!setAny)
                            {
                                setAny = true;
                                AlteredDestinationPlugin.Log("Waypoint assigned to selected missile(s) at " + cursorCoords.ToString());
                            }

                            if (closestEnemy != null) {
                                //try
                                //{
                                    FieldInfo targetField = AccessTools.Field(typeof(Missile), "target") ?? 
                                                            AccessTools.Field(typeof(Missile), "lockedTarget");
                                    
                                    if (targetField != null)
                                    {
                                        targetField.SetValue(missile, closestEnemy); 
                                    }

                                    FieldInfo idField = AccessTools.Field(typeof(Missile), "_targetID");
                                    if (idField != null)
                                    {
                                        idField.SetValue(missile, closestEnemy.persistentID);
                                    }

                                    AlteredDestinationPlugin.Log($"Missile retargeted dynamically to enemy unit: {closestEnemy.name}");
                                //}
                                //catch { }
                            }

                            if (!hasValue) {
                                AlteredDestinationPlugin.MissileWaypoints.Add(missile, data);
                            }
                        }
                    }
                }
            }
        }
    }

    [HarmonyPatch(typeof(DynamicMap), "Update")]
    public class DynamicMap_Update_Patch
    {
        private static Dictionary<UnitMapIcon, List<GameObject>> lines = new Dictionary<UnitMapIcon, List<GameObject>>();

        public static void Postfix(DynamicMap __instance)
        {
            var icons = __instance.mapIcons;
            if (icons == null) return;

            foreach (UnitMapIcon icon in icons) {
                if (icon == null || icon.unit == null || !icon.gameObject.activeInHierarchy) continue;

                if (icon.unit is Missile) {
                    var missileType = icon.unit as Missile;
                    OverrideData data;
                    bool hasValue = AlteredDestinationPlugin.MissileWaypoints.TryGetValue(missileType, out data);
                    if (!hasValue) continue;

                    UpdateLine(icon, data.routeState, __instance.mapScaleProxy);
                }
            }
        }

        private static Vector3 WaypointToMapPosition(Waypoint2D waypoint, Transform mapTransform)
        {
            GlobalPosition wpGlobal = new GlobalPosition((float)waypoint.X, 0f, (float)waypoint.Z);
            Vector3 wpWorld = wpGlobal.ToLocalPosition();
            Vector3 mapLocal = mapTransform.InverseTransformPoint(wpWorld);
            return new Vector3(mapLocal.x, mapLocal.z, 0f);
        }

        private static void UpdateLine(UnitMapIcon strikerIcon, WaypointRouteState routeState, Transform mapTransform)
        {
            if (routeState == null || routeState.Waypoints.Count == 0)
            {
                HideLines(strikerIcon);
                return;
            }

            int currentIdx = routeState.CurrentWaypoint;
            if (currentIdx < 0) currentIdx = 0;
            if (currentIdx >= routeState.Waypoints.Count) currentIdx = routeState.Waypoints.Count - 1;

            int segmentCount = routeState.Waypoints.Count - currentIdx;

            if (!lines.TryGetValue(strikerIcon, out var lineList) || lineList == null)
            {
                lineList = new List<GameObject>();
                lines[strikerIcon] = lineList;
            }

            // Ensure we have enough line objects
            while (lineList.Count < segmentCount)
            {
                lineList.Add(CreateLine(strikerIcon.transform.parent));
            }

            // Hide extra lines
            for (int i = segmentCount; i < lineList.Count; i++)
            {
                if (lineList[i] != null) lineList[i].SetActive(false);
            }

            // Draw segments: icon → wp[current], wp[current] → wp[current+1], ...
            for (int i = 0; i < segmentCount; i++)
            {
                GameObject lineObj = lineList[i];
                if (lineObj == null)
                {
                    lineObj = CreateLine(strikerIcon.transform.parent);
                    lineList[i] = lineObj;
                }

                Vector3 startPos;
                if (i == 0)
                {
                    startPos = strikerIcon.transform.localPosition;
                }
                else
                {
                    startPos = WaypointToMapPosition(routeState.Waypoints[currentIdx + i - 1], mapTransform);
                }

                Vector3 endPos = WaypointToMapPosition(routeState.Waypoints[currentIdx + i], mapTransform);

                Vector3 diff = endPos - startPos;
                float distance = diff.magnitude;

                if (distance < 1f)
                {
                    lineObj.SetActive(false);
                    continue;
                }

                lineObj.SetActive(true);
                var img = lineObj.GetComponent<Image>();
                img.color = new Color(0f, 1f, 1f, 0.8f); // Cyan

                var rect = lineObj.GetComponent<RectTransform>();
                float angle = Mathf.Atan2(diff.y, diff.x) * Mathf.Rad2Deg;

                rect.localPosition = startPos;
                rect.localRotation = Quaternion.Euler(0, 0, angle);
                rect.sizeDelta = new Vector2(distance, 1.0f);
            }
        }

        private static void HideLines(UnitMapIcon icon)
        {
            if (lines.TryGetValue(icon, out var lineList) && lineList != null)
            {
                foreach (var line in lineList)
                {
                    if (line != null) line.SetActive(false);
                }
            }
        }

        public static void ExternalCleanup(UnitMapIcon icon)
        {
            if (lines.TryGetValue(icon, out var lineList) && lineList != null)
            {
                foreach (var line in lineList)
                {
                    if (line != null) UnityEngine.Object.Destroy(line);
                }
            }
            lines.Remove(icon);
        }

        private static GameObject CreateLine(Transform parent)
        {
            var go = new GameObject("StrikerTargetLine");
            go.transform.SetParent(parent, false);
            go.transform.SetAsLastSibling(); 
            
            var img = go.AddComponent<Image>();
            img.color = new Color(1f, 0f, 0f, 0.8f);
            img.raycastTarget = false; 
            
            var rect = go.GetComponent<RectTransform>();
            // Use center anchor so that localPosition matches the Map icons' localPosition
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0, 0.5f);
            
            return go;
        }
    }

    [HarmonyPatch(typeof(Missile), "SetAimpoint")]
    public static class Missile_SetAimpoint_Patch
    {
        private static FieldInfo seekerField = AccessTools.Field(typeof(Missile), "seeker");
        private static FieldInfo terminalModeField = AccessTools.Field(typeof(OpticalSeekerCruiseMissile), "terminalMode");
        private static FieldInfo altitudeTargetField = AccessTools.Field(typeof(OpticalSeekerCruiseMissile), "altitudeTarget");
        private static FieldInfo missileTargetField = AccessTools.Field(typeof(Missile), "target");
        private static FieldInfo seekerTargetField = AccessTools.Field(typeof(MissileSeeker), "targetUnit");
        
        // Reflection targets for pop-up suppression
        private static FieldInfo topAttackField = AccessTools.Field(typeof(OpticalSeekerCruiseMissile), "topAttack");
        private static FieldInfo topAttackAmountField;
        private static FieldInfo topAttackActiveField;

        // Reflection targets for Jink (Zig-Zag) suppression
        private static FieldInfo jinkField = AccessTools.Field(typeof(OpticalSeekerCruiseMissile), "jinkEvasion");
        private static FieldInfo jinkAmountField;

        private static Type shipType = AccessTools.TypeByName("Ship");
        private static ConditionalWeakTable<Unit, StrongBox<bool>> isShipCache = new ConditionalWeakTable<Unit, StrongBox<bool>>();

        // CRITICAL FIX: Performance Caches. Eliminates massive lag spikes during swarm terminal phases
        private static ConditionalWeakTable<OpticalSeekerCruiseMissile, StrongBox<bool>> neuteredSeekersCache = new ConditionalWeakTable<OpticalSeekerCruiseMissile, StrongBox<bool>>();
        private static ConditionalWeakTable<Missile, StrongBox<Vector2>> spreadCache = new ConditionalWeakTable<Missile, StrongBox<Vector2>>();
        private static ConditionalWeakTable<Missile, StrongBox<float>> failsafeTimers = new ConditionalWeakTable<Missile, StrongBox<float>>();

        private static bool IsShip(Unit targetUnit)
        {
            if (targetUnit == null) return false;
            if (isShipCache.TryGetValue(targetUnit, out var cachedResult)) return cachedResult.Value;
            string nameLower = targetUnit.name.ToLower();
            bool isShipFallback = nameLower.Contains("ship") || nameLower.Contains("corvette") || nameLower.Contains("carrier") || nameLower.Contains("cruiser") || nameLower.Contains("destroyer");
            bool isShip = (shipType != null && (targetUnit.GetComponentInParent(shipType) != null || targetUnit.GetComponentInChildren(shipType) != null)) || isShipFallback;
            isShipCache.Add(targetUnit, new StrongBox<bool>(isShip));
            return isShip;
        }

        // --- THE COUNTER-PITCH SYSTEM + HEIGHT FLOOR FAILSAFE ---
        // Violently suppresses any attempt by the physics engine or aerodynamics to pitch up or down, 
        // while engaging a 1-second emergency pull-up if altitude drops below 1 meter.
        private static void ApplyCounterPitch(Missile missile)
        {
            if (missile.rb != null)
            {
                float currentTime = Time.time;
                bool inEmergency = false;

                // Check Failsafe Timer Status
                if (failsafeTimers.TryGetValue(missile, out var timerBox))
                {
                    if (currentTime < timerBox.Value) 
                    {
                        inEmergency = true;
                    }
                    else if (missile.GlobalPosition().y < AlteredDestinationPlugin.MinimumAltitude.Value)
                    {
                        timerBox.Value = currentTime + 1.0f; // Trigger 1-second pull-up
                        inEmergency = true;
                    }
                }
                else
                {
                    if (missile.GlobalPosition().y < AlteredDestinationPlugin.MinimumAltitude.Value)
                    {
                        failsafeTimers.Add(missile, new StrongBox<float>(currentTime + 1.0f));
                        inEmergency = true;
                    }
                    else
                    {
                        failsafeTimers.Add(missile, new StrongBox<float>(0f));
                    }
                }

                // OPTIMIZATION: Avoid writing to Rigidbody if the values are already correct.
                // Writing to rb every frame forces PhysX to recalculate spatial trees, causing massive lag spikes.
                Vector3 vel = missile.rb.velocity;
                Vector3 euler = missile.rb.rotation.eulerAngles; // Use rb.rotation instead of transform.eulerAngles
                float currentPitch = euler.x > 180f ? euler.x - 360f : euler.x; // Normalize Unity Euler pitch
                Vector3 localAngVel = missile.transform.InverseTransformDirection(missile.rb.angularVelocity);

                bool needsVelUpdate = false;
                bool needsRotUpdate = false;
                bool needsAngVelUpdate = false;

                if (inEmergency)
                {
                    // EMERGENCY PULL-UP: Force upward velocity and positive pitch
                    if (vel.y < 0.5f) 
                    {
                        vel.y += 0.1f; // User's reduced climb rate
                        needsVelUpdate = true;
                    }

                    if (Mathf.Abs(currentPitch - (-2f)) > 0.1f) 
                    {
                        euler.x -= 0.1f; // User's reduced pitch
                        needsRotUpdate = true;
                    }
                }

                // Apply only the dirty fields to keep the physics engine fast and happy
                if (needsVelUpdate) missile.rb.velocity = vel;
                if (needsRotUpdate) missile.rb.rotation = Quaternion.Euler(euler);
                if (needsAngVelUpdate) missile.rb.angularVelocity = missile.transform.TransformDirection(localAngVel);
            }
        }

        public static bool Prefix(Missile __instance, ref GlobalPosition aimPoint, ref Vector3 targetVel)
        {
            var seekerObj = seekerField.GetValue(__instance);
            OpticalSeekerCruiseMissile cSeeker = seekerObj as OpticalSeekerCruiseMissile;
            
            if (cSeeker == null) {
                return true; // prevent calculations for non cruise missiles
            }

            bool isTerminal = false;
            var termObj = terminalModeField?.GetValue(cSeeker);
            if (termObj != null) isTerminal = (bool)termObj;

            bool hasManualWaypoint = AlteredDestinationPlugin.MissileWaypoints.TryGetValue(__instance, out var data);

            // Calculate deterministic spread offset using cache to eliminate per-frame System.Random lag spikes
            float offsetX = 0f;
            float offsetZ = 0f;
            if (AlteredDestinationPlugin.SpreadRadius.Value > 0f)
            {
                if (spreadCache.TryGetValue(__instance, out var spreadBox))
                {
                    offsetX = spreadBox.Value.x;
                    offsetZ = spreadBox.Value.y;
                }
                else
                {
                    var spread = SpreadOffsetLogic.ComputeDeterministicOffset(__instance.GetInstanceID(), AlteredDestinationPlugin.SpreadRadius.Value);
                    offsetX = spread.X;
                    offsetZ = spread.Z;
                    
                    spreadCache.Add(__instance, new StrongBox<Vector2>(new Vector2(offsetX, offsetZ)));
                }
            }

            // Suppress Top Attack & Jink Evasion
            // Using neuteredSeekersCache guarantees this heavy reflection only runs ONCE per missile!
            if (!neuteredSeekersCache.TryGetValue(cSeeker, out _))
            {
                if (!AlteredDestinationPlugin.DoTopattack.Value) {
                    if (topAttackField != null)
                    {
                        var top = topAttackField.GetValue(cSeeker);
                        if (top != null)
                        {
                            if (topAttackAmountField == null) topAttackAmountField = AccessTools.Field(top.GetType(), "Amount");
                            if (topAttackActiveField == null) topAttackActiveField = AccessTools.Field(top.GetType(), "Active");
                            
                            if (topAttackAmountField != null) topAttackAmountField.SetValue(top, 0f);
                            if (topAttackActiveField != null) topAttackActiveField.SetValue(top, false);
                            
                            topAttackField.SetValue(cSeeker, top); 
                        }
                    }
                }

                if (!AlteredDestinationPlugin.DoJink.Value) {
                    if (jinkField != null)
                    {
                        var jink = jinkField.GetValue(cSeeker);
                        if (jink != null)
                        {
                            if (jinkAmountField == null) jinkAmountField = AccessTools.Field(jink.GetType(), "amount");
                            
                            if (jinkAmountField != null) jinkAmountField.SetValue(jink, 0f);
                            
                            jinkField.SetValue(cSeeker, jink); 
                        }
                    }
                }
                
                neuteredSeekersCache.Add(cSeeker, new StrongBox<bool>(true));
            }

            // 2. MOD LOGIC: Manual Waypoint Override
            if ((hasManualWaypoint) && (!isTerminal))
            {
                if (data.routeState == null)
                {
                    AlteredDestinationPlugin.Log($"Waypoint failure no route");
                    return true;
                }

                GlobalPosition currentPos = __instance.GlobalPosition();
                Waypoint2D currentPosition = new Waypoint2D(currentPos.x, currentPos.z);
                Waypoint2D? targetWaypoint = null;
                if (data.targetUnit != null)
                {
                    GlobalPosition targetPos = data.targetUnit.GlobalPosition();
                    targetWaypoint = new Waypoint2D(targetPos.x, targetPos.z);
                }

                if ((!data.triedTargettingUnit) && (data.targetUnit == null)) {
                    // fallback if no enemy clicked, get already targeted unit of missile
                    data.targetUnit = (Unit)seekerTargetField.GetValue(cSeeker) ?? (Unit)missileTargetField.GetValue(__instance);
                    data.triedTargettingUnit = true;
                }

                // waypoint radius = (number of steps * missile velocity) / 2
                float waypointRadius = Math.Max((AlteredDestinationPlugin.WaypointSteps.Value * __instance.rb.velocity.magnitude) / 2, 100.0f); // 100 min radius as safety
                AlteredDestinationPlugin.Debug($"Prewaypoint calc, radi {waypointRadius} vel {__instance.rb.velocity.magnitude} count {AlteredDestinationPlugin.WaypointSteps.Value}");

                // number of waypoints = waypoint diameter / missile velocity
                //int prewaypointcounter = (int)Math.Ceiling(AlteredDestinationPlugin.WaypointRadius.Value * 2 / __instance.rb.velocity.magnitude);
                //AlteredDestinationPlugin.Debug($"Prewaypoint calc, radi {AlteredDestinationPlugin.WaypointRadius.Value} vel {__instance.rb.velocity.magnitude} count {prewaypointcounter}");

                WaypointNavigationSettings settings = new WaypointNavigationSettings(
                    waypointRadius,
                    AlteredDestinationPlugin.WaypointSteps.Value,
                    AlteredDestinationPlugin.WobbleActivationDistance.Value,
                    AlteredDestinationPlugin.WobbleRange.Value);

                if ((targetWaypoint != null) && (targetWaypoint.HasValue)) {
                    AlteredDestinationPlugin.Debug($"Target waypoint {targetWaypoint.Value.X}, {targetWaypoint.Value.Z}");
                } else {
                    AlteredDestinationPlugin.Debug($"Target waypoint is null, target unit is {data.targetUnit}");
                }

                if (!MissileNavigationLogic.TryComputeAim(data.routeState, settings, currentPosition, targetWaypoint, out Waypoint2D destination))
                {
                    AlteredDestinationPlugin.Debug($"Waypoint failure");
                    return true;
                }
                AlteredDestinationPlugin.Debug($"Waypoint {data.routeState.CurrentWaypoint}, midpoint cnt {data.routeState.MidpointCounter}");

                aimPoint.x = (float)destination.X;
                aimPoint.z = (float)destination.Z;

                // try to leave Y completely vanilla so the cruise radar can keep it safely above the water.
                /*if (aimPoint.y < AlteredDestinationPlugin.MinimumAltitude.Value) {
                    aimPoint.y = AlteredDestinationPlugin.MinimumAltitude.Value;
                }*/
                targetVel = Vector3.zero;
                AlteredDestinationPlugin.Debug($"Missile waypoint {aimPoint.x} {aimPoint.z}");
            }

            ApplyCounterPitch(__instance);
            
            return true;
        }
    }

    [HarmonyPatch(typeof(OpticalSeekerCruiseMissile), "Initialize")]
    public static class OpticalSeekerCruiseMissile_Initialize_Patch
    {
        private static FieldInfo altField = AccessTools.Field(typeof(OpticalSeekerCruiseMissile), "altitudeTarget");
        //private static FieldInfo infoField = AccessTools.Field(typeof(Missile), "info");

        public static void Postfix(OpticalSeekerCruiseMissile __instance)
        {
            if (altField == null) return;

            //AlteredDestinationPlugin.Log(((float)altField.GetValue(__instance)).ToString());
            if ((float)altField.GetValue(__instance) > 20.0f) return; // ignore high flying cruise missiles like HASM

            // Enforce default Cruise parameters at spawn.
            altField.SetValue(__instance, AlteredDestinationPlugin.CruiseAltitude.Value);
        }
    }
}
