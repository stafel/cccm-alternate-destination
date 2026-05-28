using AlteredDestination.Logic;

namespace AlteredDestination.Tests;

public class MissileNavigationLogicTests
{
    private sealed class FixedRandom : Random
    {
        private readonly int _fixedValue;

        public FixedRandom(int fixedValue)
        {
            _fixedValue = fixedValue;
        }

        public override int Next(int minValue, int maxValue)
        {
            return Math.Clamp(_fixedValue, minValue, maxValue - 1);
        }
    }

    [Fact]
    public void TryComputeAim_ReturnsFalse_WhenNoWaypoints()
    {
        var state = new WaypointRouteState();
        var settings = new WaypointNavigationSettings(waypointRadius: 50f, preWaypointCounter: 2);

        var result = MissileNavigationLogic.TryComputeAim(
            state,
            settings,
            new Waypoint2D(0d, 0d),
            fallbackTarget: null,
            out _);

        Assert.False(result);
    }

    [Fact]
    public void TryComputeAim_ReturnsCurrentWaypoint_WhenOutsideWaypointRadius()
    {
        var state = new WaypointRouteState();
        state.Waypoints.Add(new Waypoint2D(100d, 100d));
        var settings = new WaypointNavigationSettings(waypointRadius: 10f, preWaypointCounter: 3);

        var result = MissileNavigationLogic.TryComputeAim(
            state,
            settings,
            new Waypoint2D(0d, 0d),
            fallbackTarget: null,
            out var destination);

        Assert.True(result);
        Assert.Equal(100d, destination.X);
        Assert.Equal(100d, destination.Z);
        Assert.Equal(0, state.CurrentWaypoint);
        Assert.Equal(0, state.MidpointCounter);
    }

    [Fact]
    public void TryComputeAim_UsesMidpoint_WhenWithinWaypointRadius()
    {
        var state = new WaypointRouteState();
        state.Waypoints.Add(new Waypoint2D(10d, 10d));
        state.Waypoints.Add(new Waypoint2D(30d, 30d));
        var settings = new WaypointNavigationSettings(waypointRadius: 50f, preWaypointCounter: 2);

        var result = MissileNavigationLogic.TryComputeAim(
            state,
            settings,
            new Waypoint2D(11d, 11d),
            fallbackTarget: null,
            out var destination);

        Assert.True(result);
        Assert.Equal(20d, destination.X);
        Assert.Equal(20d, destination.Z);
        Assert.Equal(0, state.CurrentWaypoint);
        Assert.Equal(1, state.MidpointCounter);
    }

    [Fact]
    public void TryComputeAim_AdvancesWaypoint_WhenMidpointCounterCompletes()
    {
        var state = new WaypointRouteState
        {
            CurrentWaypoint = 0,
            MidpointCounter = 1
        };
        state.Waypoints.Add(new Waypoint2D(10d, 10d));
        state.Waypoints.Add(new Waypoint2D(30d, 30d));

        var result = MissileNavigationLogic.TryComputeAim(
            state,
            new WaypointNavigationSettings(waypointRadius: 1f, preWaypointCounter: 2),
            new Waypoint2D(0d, 0d),
            fallbackTarget: null,
            out var destination);

        Assert.True(result);
        Assert.Equal(30d, destination.X);
        Assert.Equal(30d, destination.Z);
        Assert.Equal(1, state.CurrentWaypoint);
        Assert.Equal(0, state.MidpointCounter);
    }

    [Fact]
    public void TryComputeAim_UsesFallbackTarget_ForFinalWaypointMidpoint()
    {
        var state = new WaypointRouteState
        {
            CurrentWaypoint = 0,
            MidpointCounter = 1
        };
        state.Waypoints.Add(new Waypoint2D(20d, 40d));
        var fallbackTarget = new Waypoint2D(60d, 80d);

        var result = MissileNavigationLogic.TryComputeAim(
            state,
            new WaypointNavigationSettings(waypointRadius: 1f, preWaypointCounter: 2),
            new Waypoint2D(0d, 0d),
            fallbackTarget,
            out var destination);

        Assert.True(result);
        Assert.Equal(20d, destination.X);
        Assert.Equal(40d, destination.Z);
        Assert.Equal(0, state.CurrentWaypoint);
        Assert.Equal(0, state.MidpointCounter);
    }

    [Fact]
    public void TryComputeAim_ReturnsFalse_WhenFinalWaypointNeedsFallbackButMissing()
    {
        var state = new WaypointRouteState
        {
            CurrentWaypoint = 0,
            MidpointCounter = 1
        };
        state.Waypoints.Add(new Waypoint2D(20d, 40d));

        var result = MissileNavigationLogic.TryComputeAim(
            state,
            new WaypointNavigationSettings(waypointRadius: 50f, preWaypointCounter: 2),
            new Waypoint2D(0d, 0d),
            fallbackTarget: null,
            out _);

        Assert.False(result);
    }

    [Fact]
    public void ExtendFinalApproachWaypoints_AddsLeadInWaypoints_WhenDistanceIsLarge()
    {
        var state = new WaypointRouteState
        {
            CurrentWaypoint = 0
        };
        state.Waypoints.Add(new Waypoint2D(0d, 0d));

        MissileNavigationLogic.ExtendFinalApproachWaypoints(
            state,
            new Waypoint2D(5000d, 0d),
            new WaypointNavigationSettings(waypointRadius: 50f, preWaypointCounter: 2, wobbleActivationDistance: 0f, wobbleRange: 500),
            new FixedRandom(0));

        Assert.True(state.CurrentWaypoint > 0);
        Assert.True(state.Waypoints.Count > 1);
        Assert.InRange(Distance(state.Waypoints[^1], new Waypoint2D(5000d, 0d)), 0f, 1000f);
    }

    [Fact]
    public void ExtendFinalApproachWaypoints_UsesConfiguredWobbleSettings()
    {
        var state = new WaypointRouteState
        {
            CurrentWaypoint = 0
        };
        state.Waypoints.Add(new Waypoint2D(0d, 0d));

        MissileNavigationLogic.ExtendFinalApproachWaypoints(
            state,
            new Waypoint2D(6000d, 0d),
            new WaypointNavigationSettings(waypointRadius: 50f, preWaypointCounter: 2, wobbleActivationDistance: 5000f, wobbleRange: 500),
            new FixedRandom(123));

        Assert.Equal(3123d, state.Waypoints[1].X);
        Assert.Equal(123d, state.Waypoints[1].Z);
    }

    private static float Distance(Waypoint2D from, Waypoint2D to)
    {
        var dx = (float)(from.X - to.X);
        var dz = (float)(from.Z - to.Z);
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }
}
