using System;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using System.Reflection;

namespace AlteredDestination
{
    // Custom class to hold either a static coordinate or a dynamically tracked unit
    public class OverrideData
    {
        public List<GlobalPosition> waypoints;
        public int currentWaypoint;
        public Unit targetUnit;
        public int midpointCounter;
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
                                    waypoints = new List<GlobalPosition>(){cursorCoords},
                                    currentWaypoint = 0,
                                    targetUnit = closestEnemy,
                                    midpointCounter = 0
                                };
                            } else {
                                data.waypoints.Add(cursorCoords);
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

    [HarmonyPatch(typeof(Missile), "SetAimpoint")]
    public static class Missile_SetAimpoint_Patch
    {
        private static FieldInfo seekerField = AccessTools.Field(typeof(Missile), "seeker");
        private static FieldInfo terminalModeField = AccessTools.Field(typeof(OpticalSeekerCruiseMissile), "terminalMode");
        
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
        private static ConditionalWeakTable<Missile, StrongBox<LineRenderer>> routeRenderers = new ConditionalWeakTable<Missile, StrongBox<LineRenderer>>();

        private static LineRenderer GetOrCreateRouteRenderer(Missile missile)
        {
            if (!AlteredDestinationPlugin.DebugOutput.Value)
            {
                return null;            
            }


            if (missile == null || missile.gameObject == null)
            {
                return null;
            }

            if (routeRenderers.TryGetValue(missile, out var cachedRenderer) && cachedRenderer.Value != null)
            {
                return cachedRenderer.Value;
            }

            GameObject lineObject = new GameObject("WaypointRouteLine");
            lineObject.hideFlags = HideFlags.DontSave;
            lineObject.transform.SetParent(missile.transform, false);

            LineRenderer lineRenderer = lineObject.AddComponent<LineRenderer>();
            lineRenderer.useWorldSpace = true;
            lineRenderer.loop = false;
            lineRenderer.alignment = LineAlignment.View;
            lineRenderer.textureMode = LineTextureMode.Stretch;
            lineRenderer.numCornerVertices = 2;
            lineRenderer.numCapVertices = 2;
            lineRenderer.startWidth = 0.25f;
            lineRenderer.endWidth = 0.25f;
            lineRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lineRenderer.receiveShadows = false;
            lineRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            lineRenderer.sortingOrder = short.MaxValue;

            Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Hidden/Internal-Colored");
            if (shader != null)
            {
                lineRenderer.material = new Material(shader);
            }

            lineRenderer.startColor = new Color(0.1f, 0.9f, 1f, 1f);
            lineRenderer.endColor = new Color(1f, 0.85f, 0.2f, 1f);
            lineRenderer.enabled = false;

            routeRenderers.Add(missile, new StrongBox<LineRenderer>(lineRenderer));
            return lineRenderer;
        }

        private static Vector3 SetToCruiselevel(Vector3 localPos)
        {
            return new Vector3(localPos.x, AlteredDestinationPlugin.CruiseAltitude.Value, localPos.z);
        }

        private static void UpdateRouteVisualizer(Missile missile, OverrideData data)
        {
            LineRenderer lineRenderer = GetOrCreateRouteRenderer(missile);
            if (lineRenderer == null)
            {
                return;
            }

            if (missile == null || missile.gameObject == null || data == null || data.waypoints == null || data.waypoints.Count == 0)
            {
                lineRenderer.enabled = false;
                return;
            }

            List<Vector3> routePoints = new List<Vector3>(data.waypoints.Count + 2)
            {
                SetToCruiselevel(missile.transform.position)
            };

            for (int i = data.currentWaypoint; i < data.waypoints.Count; i++)
            {
                routePoints.Add(SetToCruiselevel(data.waypoints[i].ToLocalPosition()));
            }

            if (data.targetUnit != null && data.targetUnit.gameObject != null)
            {
                routePoints.Add(SetToCruiselevel(data.targetUnit.transform.position));
            }

            if (routePoints.Count < 2)
            {
                lineRenderer.enabled = false;
                return;
            }

            for (int i = 0; i < routePoints.Count; i++) {
                routePoints[i].Set(routePoints[i].x, AlteredDestinationPlugin.CruiseAltitude.Value, routePoints[i].z);
            }

            lineRenderer.positionCount = routePoints.Count;
            lineRenderer.SetPositions(routePoints.ToArray());
            lineRenderer.enabled = true;
        }

        private static void DisableRouteVisualizer(Missile missile)
        {
            if (missile == null)
            {
                return;
            }

            if (routeRenderers.TryGetValue(missile, out var cachedRenderer) && cachedRenderer.Value != null)
            {
                cachedRenderer.Value.enabled = false;
            }
        }

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
                    System.Random rand = new System.Random(__instance.GetInstanceID());
                    float angle = (float)rand.NextDouble() * Mathf.PI * 2f;
                    float radius = Mathf.Sqrt((float)rand.NextDouble()) * AlteredDestinationPlugin.SpreadRadius.Value;
                    
                    offsetX = Mathf.Cos(angle) * radius;
                    offsetZ = Mathf.Sin(angle) * radius;
                    
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
                            //if (jinkActiveField == null) jinkActiveField = AccessTools.Field(jink.GetType(), "active") ?? AccessTools.Field(jink.GetType(), "Active");
                            
                            if (jinkAmountField != null) jinkAmountField.SetValue(jink, 0f);
                            //if (jinkActiveField != null) jinkActiveField.SetValue(jink, false);
                            
                            jinkField.SetValue(cSeeker, jink); 
                        }
                    }
                }
                
                neuteredSeekersCache.Add(cSeeker, new StrongBox<bool>(true));
            }

            ApplyCounterPitch(__instance);

            // 2. MOD LOGIC: Manual Waypoint Override
            if (hasManualWaypoint)
            {
                GlobalPosition dest;

                dest = data.waypoints[data.currentWaypoint];

                // check to advance waypoint
                GlobalPosition currentPos = __instance.GlobalPosition();
                float dx = (float)(currentPos.x - dest.x);
                float dz = (float)(currentPos.z - dest.z);

                float distanceToWaypoint = Mathf.Sqrt(dx * dx + dz * dz);

                if (distanceToWaypoint < AlteredDestinationPlugin.WaypointRadius.Value) {
                    data.midpointCounter = AlteredDestinationPlugin.PreWaypointCounter.Value;
                }

                if (data.midpointCounter > 0) {
                    data.midpointCounter--;

                    GlobalPosition destNext;
                    if (data.currentWaypoint < data.waypoints.Count - 1)
                    {
                        destNext = data.waypoints[data.currentWaypoint + 1];
                        AlteredDestinationPlugin.Debug($"Prewaypoint with next waypoint");
                    } else if (data.targetUnit != null) {
                        destNext = data.targetUnit.GlobalPosition();
                        AlteredDestinationPlugin.Debug($"Prewaypoint with target");
                    } else {
                        AlteredDestinationPlugin.Debug($"Prewaypoint failure");
                        return true;
                    }

                    dest.x = (dest.x + destNext.x) / 2;
                    dest.z = (dest.z + destNext.z) / 2;

                    if (data.midpointCounter == 0) {
                        data.currentWaypoint++;
                        AlteredDestinationPlugin.Debug($"Switch waypoint {data.currentWaypoint+1} / {data.waypoints.Count}");
                        if (data.currentWaypoint >= data.waypoints.Count)
                        {
                            AlteredDestinationPlugin.Debug($"Missile reached final destination no terminal seeker activation");
                            data.currentWaypoint = data.waypoints.Count - 1;
                        }
                        dest = data.waypoints[data.currentWaypoint];
                    }
                }

                if (!isTerminal)
                {
                    aimPoint.x = dest.x + offsetX;
                    aimPoint.z = dest.z + offsetZ;
                    // Leave Y completely vanilla so the cruise radar can keep it safely above the water.
                    targetVel = Vector3.zero;
                    AlteredDestinationPlugin.Debug($"Missile waypoint {aimPoint.x} {aimPoint.z}");
                }

                UpdateRouteVisualizer(__instance, data);
            }
            else
            {
                DisableRouteVisualizer(__instance);
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
