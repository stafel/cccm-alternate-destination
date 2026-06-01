using System;

namespace AlteredDestination.Logic
{
    /// <summary>
    /// Holds per-missile terrain avoidance state, analogous to the terrainClearVector
    /// and altitudeTrim fields in OpticalSeekerCruiseMissile.
    /// </summary>
    public sealed class TerrainAvoidanceState
    {
        /// <summary>
        /// Smoothed terrain-clear vector Y component (relative to missile position).
        /// </summary>
        public float TerrainClearY { get; set; }

        /// <summary>
        /// Accumulated altitude trim for reactive pull-up when too close to terrain.
        /// </summary>
        public float AltitudeTrim { get; set; }
    }

    /// <summary>
    /// Pure computation logic for terrain avoidance, modeled after
    /// OpticalSeekerCruiseMissile.TerrainWaypoint in the reference.
    /// </summary>
    public static class TerrainAvoidanceLogic
    {
        private const float EmergencyPullUpHeight = 1000f;
        private const float LookaheadMultiplier = 6f;
        private const float MinLookaheadSpeed = 100f;
        private const float SmoothingFactor = 0.8f;
        private const float EmergencySmoothingFactor = 0.1f;

        /// <summary>
        /// Parameters describing the current missile state for terrain avoidance computation.
        /// </summary>
        public readonly struct TerrainInput
        {
            public TerrainInput(
                float missileAltitude,
                float radarAlt,
                float verticalVelocity,
                float speed,
                float terrainHeightAtLookahead,
                bool lookaheadObstructed)
            {
                MissileAltitude = missileAltitude;
                RadarAlt = radarAlt;
                VerticalVelocity = verticalVelocity;
                Speed = speed;
                TerrainHeightAtLookahead = terrainHeightAtLookahead;
                LookaheadObstructed = lookaheadObstructed;
            }

            /// <summary>Missile's absolute Y position (above sea level / datum).</summary>
            public float MissileAltitude { get; }

            /// <summary>Radar altitude (height above ground directly below).</summary>
            public float RadarAlt { get; }

            /// <summary>Vertical component of missile velocity (positive = climbing).</summary>
            public float VerticalVelocity { get; }

            /// <summary>Missile speed (magnitude of velocity).</summary>
            public float Speed { get; }

            /// <summary>Terrain height at the lookahead point (absolute Y).</summary>
            public float TerrainHeightAtLookahead { get; }

            /// <summary>
            /// Whether a linecast from the missile to the desired point hit terrain
            /// (indicating the straight path is obstructed).
            /// </summary>
            public bool LookaheadObstructed { get; }
        }

        /// <summary>
        /// Computes the terrain-avoidance adjusted Y value for the aimpoint.
        /// This mirrors the logic in OpticalSeekerCruiseMissile.TerrainWaypoint.
        /// </summary>
        /// <param name="state">Per-missile mutable terrain avoidance state.</param>
        /// <param name="input">Current frame terrain input parameters.</param>
        /// <param name="altitudeTarget">Desired altitude above terrain.</param>
        /// <param name="seaLevelY">Absolute Y of sea level / datum.</param>
        /// <param name="minimumHeight">Minimum absolute height floor (e.g. over water). The result will never be below this value.</param>
        /// <returns>The absolute Y value the aimpoint should use.</returns>
        public static float ComputeTerrainAvoidanceY(
            TerrainAvoidanceState state,
            TerrainInput input,
            float altitudeTarget,
            float seaLevelY,
            float minimumHeight = 0f)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            // Desired altitude at lookahead = terrain height + target altitude
            float desiredY = Math.Max(input.TerrainHeightAtLookahead, seaLevelY) + altitudeTarget;

            // Reactive altitude trim when close to terrain (from reference lines 164-173)
            if (input.RadarAlt < altitudeTarget * 2f)
            {
                float predictedAlt = input.RadarAlt + input.VerticalVelocity * 4f;
                float deficit = altitudeTarget - predictedAlt;
                state.AltitudeTrim += deficit;
                state.AltitudeTrim = Math.Max(state.AltitudeTrim, 0f);
                desiredY += state.AltitudeTrim;
            }
            else
            {
                state.AltitudeTrim = 0f;
            }

            // Smooth the terrain clear Y (analogous to terrainClearVector lerp in reference)
            float candidateY = desiredY - input.MissileAltitude; // relative to missile

            if (!input.LookaheadObstructed)
            {
                // Lerp towards candidate (smoothing factor from reference line 175)
                state.TerrainClearY = Lerp(state.TerrainClearY, candidateY, SmoothingFactor);
            }
            else
            {
                // Obstructed: pull up aggressively (reference line 182)
                state.TerrainClearY = Lerp(state.TerrainClearY, EmergencyPullUpHeight, EmergencySmoothingFactor);
            }

            // Clamp so we never aim below target altitude (reference line 185)
            float altAboveSea = input.MissileAltitude - seaLevelY;
            float minRelativeY = -(altAboveSea - altitudeTarget);
            state.TerrainClearY = Math.Max(state.TerrainClearY, minRelativeY);

            float result = input.MissileAltitude + state.TerrainClearY;

            // Enforce minimum height floor (prevents plunging into water)
            if (minimumHeight > 0f)
            {
                result = Math.Max(result, seaLevelY + minimumHeight);
            }

            return result;
        }

        /// <summary>
        /// Computes the lookahead distance based on missile speed.
        /// From reference line 150: max(speed, 100) * 6
        /// </summary>
        public static float ComputeLookaheadDistance(float speed)
        {
            return Math.Max(speed, MinLookaheadSpeed) * LookaheadMultiplier;
        }

        private static float Lerp(float a, float b, float t)
        {
            return a + (b - a) * t;
        }
    }
}
