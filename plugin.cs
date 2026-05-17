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
        public List<GlobalPosition> splineWaypoints;
        public int currentWaypoint;
        public bool loggedFinalSplineWaypoint;
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

        private const int SplineSamplesPerSpan = 10;
        private const int MinSplineSamplesPerSpan = 2;
        private const float MinSplinePointSpacingSq = 1f;
        private const float SplineParameterEpsilon = 0.0001f;

        private void Awake()
        {
            Instance = this;

            CruiseAltitude = Config.Bind("General", "Cruise Altitude", 5f, new ConfigDescription("Target radar altitude for cruise missiles in meters. Lower altitude increases the risk of terrain collision.", new AcceptableValueRange<float>(3f, 15f)));
            MinimumAltitude = Config.Bind("General", "Minimum Altitude", 3f, new ConfigDescription("Minimum radar altitude for cruise missiles in meters before an emergency pullup.", new AcceptableValueRange<float>(1f, 3f)));
            SpreadRadius = Config.Bind("General", "Spread Radius", 15f, "Radius in meters to spread out missiles targeting the same location to prevent stacking.");
            DoJink = Config.Bind("General", "Jinking maneuver in terminal approach", false, "Off (set as default) = No jink, On = Random jinking");
            DoTopattack = Config.Bind("General", "Top attack popup maneuver in terminal approach", false, "Off (set as default) = No top attack, On = Top attack popup");
            WaypointRadius = Config.Bind("General", "Waypoint radius", 50f, new ConfigDescription("Distance to waypoint to switch to next one", new AcceptableValueRange<float>(10f, 200f)));

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

        public static void RebuildSplineWaypoints(OverrideData data, float progress01 = 0f)
        {
            if (data == null) return;

            data.splineWaypoints = GenerateBSplineWaypoints(data.waypoints, SplineSamplesPerSpan);
            data.loggedFinalSplineWaypoint = false;

            if (data.splineWaypoints == null || data.splineWaypoints.Count == 0)
            {
                data.currentWaypoint = 0;
                return;
            }

            data.currentWaypoint = Mathf.Clamp(Mathf.RoundToInt(progress01 * (data.splineWaypoints.Count - 1)), 0, data.splineWaypoints.Count - 1);
        }

        private static List<GlobalPosition> GenerateBSplineWaypoints(List<GlobalPosition> controlWaypoints, int samplesPerSpan)
        {
            List<GlobalPosition> result = new List<GlobalPosition>();
            if (controlWaypoints == null || controlWaypoints.Count == 0) return result;
            if (controlWaypoints.Count == 1)
            {
                result.Add(controlWaypoints[0]);
                return result;
            }

            int n = controlWaypoints.Count - 1;
            int degree = Mathf.Min(3, n);
            if (degree <= 0)
            {
                result.AddRange(controlWaypoints);
                return result;
            }

            Vector3[] controlPoints = new Vector3[controlWaypoints.Count];
            for (int i = 0; i < controlWaypoints.Count; i++)
            {
                controlPoints[i] = new Vector3((float)controlWaypoints[i].x, (float)controlWaypoints[i].y, (float)controlWaypoints[i].z);
            }

            float[] knots = new float[n + degree + 2];
            float maxT = n - degree + 1;

            for (int i = 0; i < knots.Length; i++)
            {
                if (i <= degree) knots[i] = 0f;
                else if (i >= n + 1) knots[i] = maxT;
                else knots[i] = i - degree;
            }

            int effectiveSamplesPerSpan = Mathf.Max(MinSplineSamplesPerSpan, samplesPerSpan);
            int sampleCount = Mathf.Max(controlWaypoints.Count, Mathf.CeilToInt(maxT * effectiveSamplesPerSpan) + 1);
            Vector3? lastAdded = null;

            for (int s = 0; s < sampleCount; s++)
            {
                float t = maxT * s / (sampleCount - 1);
                Vector3 p = EvaluateBSplinePoint(controlPoints, knots, degree, t);
                bool isFirstSample = !lastAdded.HasValue;
                bool isLastSample = s == sampleCount - 1;
                bool hasMinimumSpacing = isFirstSample || (p - lastAdded.Value).sqrMagnitude >= MinSplinePointSpacingSq;
                if (hasMinimumSpacing || isLastSample)
                {
                    result.Add(ToGlobalPosition(p));
                    lastAdded = p;
                }
            }
            if (result.Count > 0)
            {
                result[result.Count - 1] = controlWaypoints[controlWaypoints.Count - 1];
            }

            return result;
        }

        private static Vector3 EvaluateBSplinePoint(Vector3[] controlPoints, float[] knots, int degree, float t)
        {
            int n = controlPoints.Length - 1;
            float maxT = knots[n + 1];
            float maxSampleT = Mathf.Max(knots[degree], maxT - SplineParameterEpsilon);
            t = Mathf.Clamp(t, knots[degree], maxSampleT);

            int k = degree;
            for (int i = degree; i <= n; i++)
            {
                if (t >= knots[i] && t < knots[i + 1])
                {
                    k = i;
                    break;
                }
            }

            Vector3[] d = new Vector3[degree + 1];
            for (int j = 0; j <= degree; j++)
            {
                d[j] = controlPoints[k - degree + j];
            }

            for (int r = 1; r <= degree; r++)
            {
                for (int j = degree; j >= r; j--)
                {
                    int knotIndex = k - degree + j;
                    float denom = knots[knotIndex + degree - r + 1] - knots[knotIndex];
                    float alpha = denom <= 0f ? 0f : (t - knots[knotIndex]) / denom;
                    d[j] = (1f - alpha) * d[j - 1] + alpha * d[j];
                }
            }

            return d[degree];
        }

        private static GlobalPosition ToGlobalPosition(Vector3 p)
        {
            GlobalPosition g = default;
            g.x = p.x;
            g.y = p.y;
            g.z = p.z;
            return g;
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
                                    splineWaypoints = new List<GlobalPosition>(){cursorCoords},
                                    currentWaypoint = 0
                                };
                                AlteredDestinationPlugin.RebuildSplineWaypoints(data);
                            } else {
                                float progress01 = data.splineWaypoints != null && data.splineWaypoints.Count > 1
                                    ? (float)data.currentWaypoint / (data.splineWaypoints.Count - 1)
                                    : 0f;
                                data.waypoints.Add(cursorCoords);
                                AlteredDestinationPlugin.RebuildSplineWaypoints(data, progress01);
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
        private static FieldInfo jinkActiveField;

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
                            if (topAttackAmountField == null) topAttackAmountField = AccessTools.Field(top.GetType(), "amount") ?? AccessTools.Field(top.GetType(), "Amount");
                            if (topAttackActiveField == null) topAttackActiveField = AccessTools.Field(top.GetType(), "active") ?? AccessTools.Field(top.GetType(), "Active");
                            
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
                            if (jinkAmountField == null) jinkAmountField = AccessTools.Field(jink.GetType(), "amount") ?? AccessTools.Field(jink.GetType(), "Amount");
                            if (jinkActiveField == null) jinkActiveField = AccessTools.Field(jink.GetType(), "active") ?? AccessTools.Field(jink.GetType(), "Active");
                            
                            if (jinkAmountField != null) jinkAmountField.SetValue(jink, 0f);
                            if (jinkActiveField != null) jinkActiveField.SetValue(jink, false);
                            
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
                GlobalPosition dest;
                if (data.splineWaypoints == null || data.splineWaypoints.Count == 0)
                {
                    AlteredDestinationPlugin.RebuildSplineWaypoints(data);
                }
                if (data.splineWaypoints == null || data.splineWaypoints.Count == 0)
                {
                    return true;
                }
                data.currentWaypoint = Mathf.Clamp(data.currentWaypoint, 0, data.splineWaypoints.Count - 1);
                dest = data.splineWaypoints[data.currentWaypoint];

                // check to advance waypoint
                GlobalPosition currentPos = __instance.GlobalPosition();
                while (data.currentWaypoint < data.splineWaypoints.Count - 1)
                {
                    dest = data.splineWaypoints[data.currentWaypoint];
                    float dx = (float)(currentPos.x - dest.x);
                    float dz = (float)(currentPos.z - dest.z);
                    if (Mathf.Sqrt(dx * dx + dz * dz) >= AlteredDestinationPlugin.WaypointRadius.Value) break;
                    data.currentWaypoint++;
                }
                dest = data.splineWaypoints[data.currentWaypoint];
                if (data.currentWaypoint >= data.splineWaypoints.Count - 1 && !data.loggedFinalSplineWaypoint)
                {
                    AlteredDestinationPlugin.Log($"Missile reached final destination no terminal seeker activation");
                    data.loggedFinalSplineWaypoint = true;
                }

                aimPoint.x = dest.x;
                aimPoint.z = dest.z;
                // Leave Y completely vanilla so the cruise radar can keep it safely above the water.
                targetVel = Vector3.zero;
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
