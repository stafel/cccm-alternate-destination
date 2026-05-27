using System;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
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
    }

    [BepInPlugin("com.checkpointcharlie.cruisemissile", "Checkpoint Charlie's Cruise Missile (Alternate destination)", "1.0.0")]
    public class AlteredDestinationPlugin : BaseUnityPlugin
    {
        public static ConditionalWeakTable<Missile, OverrideData> MissileWaypoints = new ConditionalWeakTable<Missile, OverrideData>();
        public static AlteredDestinationPlugin Instance;
        public static ConfigEntry<float> CruiseAltitude;
        public static ConfigEntry<float> MinimumAltitude;
        public static ConfigEntry<float> SpreadRadius;
        public static ConfigEntry<bool> DoJink;
        public static ConfigEntry<bool> DoTopattack;
        public static ConfigEntry<float> WaypointRadius;
        public static ConfigEntry<int> PreWaypointCounter;
        public static ConfigEntry<bool> DebugOutput;

        private void Awake()
        {
            Instance = this;

            CruiseAltitude = Config.Bind("General", "Cruise Altitude", 5f, new ConfigDescription("Target radar altitude for cruise missiles in meters. Lower altitude increases the risk of terrain collision.", new AcceptableValueRange<float>(3f, 15f)));
            MinimumAltitude = Config.Bind("General", "Minimum Altitude", 3f, new ConfigDescription("Minimum radar altitude for cruise missiles in meters before an emergency pullup.", new AcceptableValueRange<float>(1f, 3f)));
            SpreadRadius = Config.Bind("General", "Spread Radius", 15f, "Radius in meters to spread out missiles targeting the same location to prevent stacking.");
            DoJink = Config.Bind("General", "Jinking maneuver in terminal approach", false, "Off (set as default) = No jink, On = Random jinking");
            DoTopattack = Config.Bind("General", "Top attack popup maneuver in terminal approach", false, "Off (set as default) = No top attack, On = Top attack popup");
            WaypointRadius = Config.Bind("General", "Waypoint radius", 50f, new ConfigDescription("Distance to waypoint to switch to next one", new AcceptableValueRange<float>(10f, 200f)));
            PreWaypointCounter = Config.Bind("General", "Pre Waypoint", 5, new ConfigDescription("pre pitch", new AcceptableValueRange<int>(0, 10)));
            DebugOutput = Config.Bind("General", "Debug logging", true);

            var harmony = new Harmony("com.checkpointcharlie.cruisemissile");
            harmony.PatchAll();
            Logger.LogInfo("Checkpoint Charlie's Cruise Missile Mod Loaded!");

            /*foreach (var f in AccessTools.GetFieldNames(typeof(OpticalSeekerCruiseMissile))) {
                Logger.LogInfo(f);
            }*/
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
                                data.routeState.Waypoints.Add(new Waypoint2D(cursorCoords.x, cursorCoords.z));
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
    public static class DynamicMap_WaypointOverlay_Patch
    {
        private enum MapProjectionMode
        {
            Unknown,
            ReturnVector2,
            ReturnVector3,
            OutVector2,
            OutVector3
        }

        private static FieldInfo _missileSeekerField = AccessTools.Field(typeof(Missile), "seeker");
        private static readonly Dictionary<UnitMapIcon, List<GameObject>> waypointLines = new Dictionary<UnitMapIcon, List<GameObject>>();
        private static readonly string[] projectionMethodNames =
        {
            "CoordinatesToMapPosition",
            "CoordinatesToMapPoint",
            "WorldPositionToMapPosition",
            "MapPositionFromCoordinates",
            "GlobalToMapPosition",
            "GetMapPosition",
            "CoordinatesToMap"
        };
        private const float WaypointLineWidth = 1.5f;
        private const float MinimumLineDistance = 1f;

        private static MethodInfo projectionMethod;
        private static MapProjectionMode projectionMode = MapProjectionMode.Unknown;
        private static bool projectionResolved;
        private static readonly Type imageType = AccessTools.TypeByName("UnityEngine.UI.Image");
        private static readonly PropertyInfo imageColorProperty = imageType?.GetProperty("color");
        private static readonly PropertyInfo imageRaycastTargetProperty = imageType?.GetProperty("raycastTarget");

        public static void Postfix(DynamicMap __instance)
        {
            try
            {
                var selectedIcons = __instance.selectedIcons;
                if (selectedIcons == null)
                {
                    HideAll();
                    return;
                }

                HashSet<UnitMapIcon> updatedIcons = new HashSet<UnitMapIcon>();
                foreach (var baseIcon in selectedIcons)
                {
                    if (!(baseIcon is UnitMapIcon icon) || icon.unit == null || !icon.gameObject.activeInHierarchy)
                    {
                        continue;
                    }

                    if (!(icon.unit is Missile missile))
                    {
                        HideLines(icon);
                        continue;
                    }

                    if (!(_missileSeekerField?.GetValue(missile) is OpticalSeekerCruiseMissile))
                    {
                        HideLines(icon);
                        continue;
                    }

                    if (!AlteredDestinationPlugin.MissileWaypoints.TryGetValue(missile, out var data) ||
                        data?.routeState?.Waypoints == null ||
                        data.routeState.Waypoints.Count == 0)
                    {
                        HideLines(icon);
                        continue;
                    }

                    if (!UpdateWaypointLines(__instance, icon, data.routeState))
                    {
                        HideLines(icon);
                        continue;
                    }

                    updatedIcons.Add(icon);
                }

                List<UnitMapIcon> staleIcons = new List<UnitMapIcon>();
                foreach (var pair in waypointLines)
                {
                    if (!updatedIcons.Contains(pair.Key))
                    {
                        HideLines(pair.Key);
                        if (pair.Key == null || !pair.Key.gameObject.activeInHierarchy)
                        {
                            CleanupIcon(pair.Key);
                            staleIcons.Add(pair.Key);
                        }
                    }
                }

                foreach (var staleIcon in staleIcons)
                {
                    waypointLines.Remove(staleIcon);
                }
            }
            catch (Exception e)
            {
                AlteredDestinationPlugin.Debug("Failed to render waypoint overlay: " + e.Message);
            }
        }

        public static void ExternalCleanup(UnitMapIcon icon)
        {
            CleanupIcon(icon);
            if (icon != null)
            {
                waypointLines.Remove(icon);
            }
        }

        private static bool UpdateWaypointLines(DynamicMap map, UnitMapIcon icon, WaypointRouteState routeState)
        {
            if (!waypointLines.TryGetValue(icon, out var lines))
            {
                lines = new List<GameObject>();
                waypointLines[icon] = lines;
            }

            int waypointCount = routeState.Waypoints.Count;
            while (lines.Count < waypointCount)
            {
                lines.Add(CreateLine(icon.transform.parent));
            }

            while (lines.Count > waypointCount)
            {
                int lastIndex = lines.Count - 1;
                if (lines[lastIndex] != null)
                {
                    Object.Destroy(lines[lastIndex]);
                }

                lines.RemoveAt(lastIndex);
            }

            Vector3 startPos = icon.transform.localPosition;
            for (int i = 0; i < waypointCount; i++)
            {
                if (!TryGetMapPosition(map, routeState.Waypoints[i], out Vector3 endPos))
                {
                    return false;
                }

                var lineObj = lines[i];
                if (lineObj == null)
                {
                    lineObj = CreateLine(icon.transform.parent);
                    lines[i] = lineObj;
                }

                lineObj.SetActive(true);

                var lineImage = GetLineImage(lineObj);
                if (lineImage != null)
                {
                    bool isCurrentWaypoint = i == Mathf.Clamp(routeState.CurrentWaypoint, 0, waypointCount - 1);
                    SetImageColor(lineImage, isCurrentWaypoint
                        ? new Color(1f, 0.9f, 0f, 0.9f)
                        : new Color(0f, 1f, 1f, 0.75f));
                }

                UpdateLineTransform(lineObj.GetComponent<RectTransform>(), startPos, endPos);
                startPos = endPos;
            }

            return true;
        }

        private static bool TryGetMapPosition(DynamicMap map, Waypoint2D waypoint, out Vector3 mapPosition)
        {
            mapPosition = default;
            if (!projectionResolved)
            {
                ResolveProjectionMethod();
                projectionResolved = true;
            }

            if (projectionMethod == null || projectionMode == MapProjectionMode.Unknown)
            {
                return false;
            }

            GlobalPosition global = default;
            global.x = waypoint.X;
            global.z = waypoint.Z;

            object[] args;
            object invokeResult;
            switch (projectionMode)
            {
                case MapProjectionMode.ReturnVector2:
                    invokeResult = projectionMethod.Invoke(map, new object[] { global });
                    if (invokeResult is Vector2 returnVec2)
                    {
                        mapPosition = new Vector3(returnVec2.x, returnVec2.y, 0f);
                        return true;
                    }
                    break;
                case MapProjectionMode.ReturnVector3:
                    invokeResult = projectionMethod.Invoke(map, new object[] { global });
                    if (invokeResult is Vector3 returnVec3)
                    {
                        mapPosition = returnVec3;
                        return true;
                    }
                    break;
                case MapProjectionMode.OutVector2:
                    args = new object[] { global, default(Vector2) };
                    invokeResult = projectionMethod.Invoke(map, args);
                    if (invokeResult is bool okVec2 && !okVec2)
                    {
                        return false;
                    }

                    if (args[1] is Vector2 outVec2)
                    {
                        mapPosition = new Vector3(outVec2.x, outVec2.y, 0f);
                        return true;
                    }
                    break;
                case MapProjectionMode.OutVector3:
                    args = new object[] { global, default(Vector3) };
                    invokeResult = projectionMethod.Invoke(map, args);
                    if (invokeResult is bool okVec3 && !okVec3)
                    {
                        return false;
                    }

                    if (args[1] is Vector3 outVec3)
                    {
                        mapPosition = outVec3;
                        return true;
                    }
                    break;
            }

            return false;
        }

        private static void ResolveProjectionMethod()
        {
            var methods = AccessTools.GetDeclaredMethods(typeof(DynamicMap));

            foreach (string methodName in projectionMethodNames)
            {
                foreach (var method in methods)
                {
                    if (method.Name == methodName && TryMatchProjectionSignature(method, out MapProjectionMode mode))
                    {
                        projectionMethod = method;
                        projectionMode = mode;
                        return;
                    }
                }
            }

            foreach (var method in methods)
            {
                if (TryMatchProjectionSignature(method, out MapProjectionMode mode))
                {
                    projectionMethod = method;
                    projectionMode = mode;
                    return;
                }
            }
        }

        private static bool TryMatchProjectionSignature(MethodInfo method, out MapProjectionMode mode)
        {
            mode = MapProjectionMode.Unknown;
            var parameters = method.GetParameters();

            if (parameters.Length == 1 && parameters[0].ParameterType == typeof(GlobalPosition))
            {
                if (method.ReturnType == typeof(Vector2))
                {
                    mode = MapProjectionMode.ReturnVector2;
                    return true;
                }

                if (method.ReturnType == typeof(Vector3))
                {
                    mode = MapProjectionMode.ReturnVector3;
                    return true;
                }
            }

            if (parameters.Length == 2 &&
                parameters[0].ParameterType == typeof(GlobalPosition) &&
                parameters[1].ParameterType.IsByRef)
            {
                Type outType = parameters[1].ParameterType.GetElementType();
                if (outType == typeof(Vector2))
                {
                    mode = MapProjectionMode.OutVector2;
                    return method.ReturnType == typeof(bool) || method.ReturnType == typeof(void);
                }

                if (outType == typeof(Vector3))
                {
                    mode = MapProjectionMode.OutVector3;
                    return method.ReturnType == typeof(bool) || method.ReturnType == typeof(void);
                }
            }

            return false;
        }

        private static void UpdateLineTransform(RectTransform rect, Vector3 startPos, Vector3 endPos)
        {
            if (rect == null)
            {
                return;
            }

            Vector3 diff = endPos - startPos;
            float distance = diff.magnitude;
            if (distance < MinimumLineDistance)
            {
                rect.gameObject.SetActive(false);
                return;
            }

            float angle = Mathf.Atan2(diff.y, diff.x) * Mathf.Rad2Deg;

            rect.localPosition = startPos;
            rect.localRotation = Quaternion.Euler(0f, 0f, angle);
            rect.sizeDelta = new Vector2(distance, WaypointLineWidth);
        }

        private static GameObject CreateLine(Transform parent)
        {
            var lineObj = new GameObject("CruiseMissileWaypointLine");
            lineObj.transform.SetParent(parent, false);
            lineObj.transform.SetAsLastSibling();

            var image = CreateLineImage(lineObj);
            if (image != null)
            {
                SetImageRaycastTarget(image, false);
                SetImageColor(image, new Color(0f, 1f, 1f, 0.75f));
            }

            var rect = lineObj.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0f, 0.5f);

            return lineObj;
        }

        private static Component CreateLineImage(GameObject lineObj)
        {
            if (imageType == null || lineObj == null)
            {
                return null;
            }

            return lineObj.AddComponent(imageType);
        }

        private static Component GetLineImage(GameObject lineObj)
        {
            if (imageType == null || lineObj == null)
            {
                return null;
            }

            return lineObj.GetComponent(imageType);
        }

        private static void SetImageColor(Component image, Color color)
        {
            imageColorProperty?.SetValue(image, color);
        }

        private static void SetImageRaycastTarget(Component image, bool raycastTarget)
        {
            imageRaycastTargetProperty?.SetValue(image, raycastTarget);
        }

        private static void HideLines(UnitMapIcon icon)
        {
            if (icon == null || !waypointLines.TryGetValue(icon, out var lines))
            {
                return;
            }

            foreach (var line in lines)
            {
                if (line != null)
                {
                    line.SetActive(false);
                }
            }
        }

        private static void CleanupIcon(UnitMapIcon icon)
        {
            if (icon == null || !waypointLines.TryGetValue(icon, out var lines))
            {
                return;
            }

            foreach (var line in lines)
            {
                if (line != null)
                {
                    Object.Destroy(line);
                }
            }
        }

        private static void HideAll()
        {
            foreach (var pair in waypointLines)
            {
                if (pair.Value == null)
                {
                    continue;
                }

                foreach (var line in pair.Value)
                {
                    if (line != null)
                    {
                        line.SetActive(false);
                    }
                }
            }
        }
    }

    [HarmonyPatch(typeof(UnitMapIcon), "OnRemoveIcon")]
    public static class UnitMapIcon_OnRemoveIcon_WaypointOverlay_Patch
    {
        public static void Prefix(UnitMapIcon __instance)
        {
            DynamicMap_WaypointOverlay_Patch.ExternalCleanup(__instance);
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

            ApplyCounterPitch(__instance);

            // 2. MOD LOGIC: Manual Waypoint Override
            if ((hasManualWaypoint) && (!isTerminal))
            {
                if (data.routeState == null)
                {
                    return true;
                }

                GlobalPosition currentPos = __instance.GlobalPosition();
                Waypoint2D currentWaypoint = new Waypoint2D(currentPos.x, currentPos.z);
                Waypoint2D? targetWaypoint = null;
                if (data.targetUnit != null)
                {
                    GlobalPosition targetPos = data.targetUnit.GlobalPosition();
                    targetWaypoint = new Waypoint2D(targetPos.x, targetPos.z);
                }

                WaypointNavigationSettings settings = new WaypointNavigationSettings(
                    AlteredDestinationPlugin.WaypointRadius.Value,
                    AlteredDestinationPlugin.PreWaypointCounter.Value);

                if (!MissileNavigationLogic.TryComputeAim(data.routeState, settings, currentWaypoint, targetWaypoint, out Waypoint2D destination))
                {
                    AlteredDestinationPlugin.Debug($"Prewaypoint failure");
                    return true;
                }

                aimPoint.x = destination.X;
                aimPoint.z = destination.Z;
                // Leave Y completely vanilla so the cruise radar can keep it safely above the water.
                targetVel = Vector3.zero;
                AlteredDestinationPlugin.Debug($"Missile waypoint {aimPoint.x} {aimPoint.z}");
            }
            
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
