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
        public WaypointNavigationSettings(
            float waypointRadius,
            int preWaypointCounter,
            float wobbleActivationDistance = 5000.0f,
            int wobbleRange = 500)
        {
            WaypointRadius = waypointRadius;
            PreWaypointCounter = preWaypointCounter;
            WobbleActivationDistance = wobbleActivationDistance;
            WobbleRange = wobbleRange;
        }

        public float WaypointRadius { get; }
        public int PreWaypointCounter { get; }
        public float WobbleActivationDistance { get; }
        public int WobbleRange { get; }
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

                        if (fallbackTarget.HasValue)
                        {
                            ExtendFinalApproachWaypoints(state, fallbackTarget.Value, settings);
                        }
                    }

                    destination = state.Waypoints[state.CurrentWaypoint];
                }
            }

            aimPoint = destination;
            return true;
        }

        public static void ExtendFinalApproachWaypoints(
            WaypointRouteState state,
            Waypoint2D fallbackTarget,
            WaypointNavigationSettings settings,
            Random random = null)
        {
            if ((state == null) || (state.Waypoints.Count == 0))
            {
                return;
            }

            if (state.CurrentWaypoint < 0)
            {
                state.CurrentWaypoint = 0;
            }
            else if (state.CurrentWaypoint >= state.Waypoints.Count)
            {
                state.CurrentWaypoint = state.Waypoints.Count - 1;
            }

            Waypoint2D lastWaypoint = state.Waypoints[state.CurrentWaypoint];
            float restDist = Distance2D(lastWaypoint, fallbackTarget);
            if (restDist > 1000.0f)
            {
                state.CurrentWaypoint += 1; // increment to next to prevent missile trying to loopdiloop back
            }

            Random rnd = random ?? new Random();
            int wobbleRange = Math.Max(settings.WobbleRange, 0);
            while (restDist > 1000.0f)
            {
                int wobbleX = 0;
                int wobbleZ = 0;
                if ((settings.WobbleActivationDistance > 0.0f) && ((restDist / 2.0f) < settings.WobbleActivationDistance) && (wobbleRange > 0))
                {
                    wobbleX = rnd.Next(-wobbleRange, wobbleRange); // random wobble to evade gunfire
                    wobbleZ = rnd.Next(-wobbleRange, wobbleRange);
                }

                Waypoint2D nextWaypoint = new Waypoint2D(
                    ((lastWaypoint.X + fallbackTarget.X) / 2.0d) + wobbleX,
                    ((lastWaypoint.Z + fallbackTarget.Z) / 2.0d) + wobbleZ);

                lastWaypoint = nextWaypoint;
                state.Waypoints.Add(nextWaypoint);

                restDist = Distance2D(nextWaypoint, fallbackTarget);
            }
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

        /// <summary>
        /// Returns an unsigned bend angle in degrees (0 = straight, 180 full turn)
        /// </summary>
        public static double BendAngle(Waypoint2D A, Waypoint2D B, Waypoint2D C) 
        {
            Waypoint2D vecAB = new Waypoint2D(A.X-B.X, A.Z-B.Z);
            Waypoint2D vecBC = new Waypoint2D(C.X-B.X, C.Z-B.Z);
            double dotProduct = vecAB.X * vecBC.X + vecAB.Z * vecBC.Z;
            double crossProduct = vecAB.X * vecBC.Z - vecAB.Z * vecBC.X;
            return Math.Abs(Math.Abs(Math.Atan2(crossProduct, dotProduct) * (180.0f / Math.PI))-180.0f);
        }

        public static Waypoint2D Midpoint(Waypoint2D A, Waypoint2D B) 
        {
            return new Waypoint2D((A.X + B.X) / 2, (A.Z + B.Z) / 2);
        }

        public static Waypoint2D Midpoint(Waypoint2D A, Waypoint2D B, Waypoint2D C) 
        {
            return new Waypoint2D((A.X + B.X + C.X) / 3, (A.Z + B.Z + C.Z) / 3);
        }

        public static Waypoint2D MidpointUnderBendAngle(Waypoint2D A, Waypoint2D B, Waypoint2D C, double maxAngle)
        {
            int maxRuns = 10;
            Waypoint2D midpoint = B;
            while (BendAngle(A, midpoint, C) > maxAngle)
            {
                midpoint = Midpoint(A, midpoint, C);
                maxRuns--;
                if (maxRuns <= 0) {
                    break;
                }
            }
            return midpoint;
        }

        public static List<Waypoint2D> PointsUnderBendAngle(Waypoint2D A, Waypoint2D B, Waypoint2D C, double maxAngle) 
        {
            List<Waypoint2D> newPoints = new List<Waypoint2D>();

            if (BendAngle(A, B, C) <= maxAngle) { // quick exit if angle already ok
                newPoints.Add(B);
                return newPoints;
            }

            // split up B into two points between AB and BC
            // angle between point before A, A, AB should not change, no check needed
            Waypoint2D AB = Midpoint(A, B, B); // keep the midpoints in one third to the bend
            Waypoint2D BC = Midpoint(B, B, C);

            if (BendAngle(A, AB, BC) > maxAngle) { // angles are symetric, only one check needed
                newPoints.AddRange(PointsUnderBendAngle(A, AB, BC, maxAngle));
                newPoints.AddRange(PointsUnderBendAngle(AB, BC, C, maxAngle)); // they should not cross around with the once before
            } else {
                newPoints.Add(AB);
                newPoints.Add(BC);
            }

            return newPoints;
        }
    }
}
