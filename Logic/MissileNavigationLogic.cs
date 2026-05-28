using System;
using System.Collections.Generic;

namespace AlteredDestination.Logic
{
    public readonly struct Waypoint2D
    {
        public Waypoint2D(double x, double z)
        {
            X = x;
            Z = z;
        }

        public double X { get; }
        public double Z { get; }
    }

    public sealed class WaypointRouteState
    {
        public List<Waypoint2D> Waypoints { get; } = new List<Waypoint2D>();
        public int CurrentWaypoint { get; set; }
        public int MidpointCounter { get; set; }
    }

    public readonly struct WaypointNavigationSettings
    {
        public WaypointNavigationSettings(float waypointRadius, int preWaypointCounter)
        {
            WaypointRadius = waypointRadius;
            PreWaypointCounter = preWaypointCounter;
        }

        public float WaypointRadius { get; }
        public int PreWaypointCounter { get; }
    }

    public static class MissileNavigationLogic
    {
        public static bool TryComputeAim(
            WaypointRouteState state,
            WaypointNavigationSettings settings,
            Waypoint2D currentPosition,
            Waypoint2D? fallbackTarget,
            out Waypoint2D aimPoint)
        {
            aimPoint = default;

            if (state == null || state.Waypoints.Count == 0)
            {
                return false;
            }

            if (state.CurrentWaypoint < 0)
            {
                state.CurrentWaypoint = 0;
            }
            else if (state.CurrentWaypoint >= state.Waypoints.Count)
            {
                state.CurrentWaypoint = state.Waypoints.Count - 1;
            }

            Waypoint2D destination = state.Waypoints[state.CurrentWaypoint];
            float distanceToWaypoint = Distance2D(currentPosition, destination);
            if ((state.MidpointCounter == 0) && (distanceToWaypoint < settings.WaypointRadius))
            {
                state.MidpointCounter = settings.PreWaypointCounter;
            }

            if (state.MidpointCounter > 0)
            {
                state.MidpointCounter--;
                if (!TryGetNextTarget(state, fallbackTarget, out Waypoint2D nextTarget))
                {
                    return false;
                }

                destination = new Waypoint2D(
                    (destination.X + nextTarget.X) / 2.0d,
                    (destination.Z + nextTarget.Z) / 2.0d);

                if (state.MidpointCounter == 0)
                {
                    state.CurrentWaypoint++;
                    if (state.CurrentWaypoint >= state.Waypoints.Count)
                    {
                        state.CurrentWaypoint = state.Waypoints.Count - 1;

                        if ((fallbackTarget != null) && (fallbackTarget.HasValue)) { // put mid waypoints between us and target to lead missile in
                            Waypoint2D lastWaypoint = state.Waypoints[state.CurrentWaypoint];
                            float restDist = Distance2D(lastWaypoint, fallbackTarget.Value);
                            if (restDist > 1000.0f) {
                                state.CurrentWaypoint += 1; // increment to next to prevent missile trying to loopdiloop back
                            }
                            Random rnd = new Random();
                            int wobbleX = 0;
                            int wobbleZ = 0;
                            while (restDist > 1000.0f) {
                                if (restDist/2 < 5000.0f) {
                                    wobbleX = rnd.Next(-500, 500); // random wobble to evade gunfire
                                    wobbleZ = rnd.Next(-500, 500);
                                }

                                Waypoint2D nextWaypoint = new Waypoint2D(
                                    (lastWaypoint.X + fallbackTarget.Value.X) / 2 + wobbleX,
                                    (lastWaypoint.Z + fallbackTarget.Value.Z) / 2 + wobbleZ);

                                lastWaypoint = nextWaypoint;
                                state.Waypoints.Add(nextWaypoint);

                                restDist = Distance2D(nextWaypoint, fallbackTarget.Value);
                            }
                        }
                    }

                    destination = state.Waypoints[state.CurrentWaypoint];
                }
            }

            aimPoint = destination;
            return true;
        }

        private static bool TryGetNextTarget(WaypointRouteState state, Waypoint2D? fallbackTarget, out Waypoint2D target)
        {
            if (state.CurrentWaypoint < state.Waypoints.Count - 1)
            {
                target = state.Waypoints[state.CurrentWaypoint + 1];
                return true;
            }

            if (fallbackTarget.HasValue)
            {
                target = fallbackTarget.Value;
                return true;
            }

            target = default;
            return false;
        }

        private static float Distance2D(Waypoint2D from, Waypoint2D to)
        {
            double dx = from.X - to.X;
            double dz = from.Z - to.Z;
            return (float)Math.Sqrt((dx * dx) + (dz * dz));
        }
    }
}
