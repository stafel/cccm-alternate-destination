using AlteredDestination.Logic;

namespace AlteredDestination.Tests;

public class TerrainAvoidanceLogicTests
{
    [Fact]
    public void ComputeLookaheadDistance_UsesMinimum100()
    {
        Assert.Equal(600f, TerrainAvoidanceLogic.ComputeLookaheadDistance(50f));
        Assert.Equal(600f, TerrainAvoidanceLogic.ComputeLookaheadDistance(100f));
        Assert.Equal(1200f, TerrainAvoidanceLogic.ComputeLookaheadDistance(200f));
    }

    [Fact]
    public void ComputeTerrainAvoidanceY_FlatTerrain_ReturnsAltitudeAboveTerrain()
    {
        var state = new TerrainAvoidanceState();
        var input = new TerrainAvoidanceLogic.TerrainInput(
            missileAltitude: 10f,
            radarAlt: 10f,
            verticalVelocity: 0f,
            speed: 200f,
            terrainHeightAtLookahead: 0f,
            lookaheadObstructed: false);

        float result = TerrainAvoidanceLogic.ComputeTerrainAvoidanceY(state, input, altitudeTarget: 5f, seaLevelY: 0f);

        // Desired Y = terrain(0) + altTarget(5) = 5
        // Relative to missile: 5 - 10 = -5
        // Lerped from 0 at 0.8: 0 + (-5)*0.8 = -4
        // Clamped: min = -(10 - 5) = -5, so -4 is fine
        // Final = missile(10) + terrainClearY(-4) = 6
        Assert.Equal(6f, result, precision: 1);
    }

    [Fact]
    public void ComputeTerrainAvoidanceY_HighTerrain_PullsUp()
    {
        var state = new TerrainAvoidanceState();
        var input = new TerrainAvoidanceLogic.TerrainInput(
            missileAltitude: 10f,
            radarAlt: 3f,  // very close to ground
            verticalVelocity: 0f,
            speed: 200f,
            terrainHeightAtLookahead: 50f,  // terrain rises ahead
            lookaheadObstructed: false);

        float result = TerrainAvoidanceLogic.ComputeTerrainAvoidanceY(state, input, altitudeTarget: 5f, seaLevelY: 0f);

        // Terrain ahead is at 50, so desired is 55. Missile is at 10.
        // Also radarAlt(3) < altTarget*2(10), so altitude trim kicks in:
        //   deficit = 5 - (3 + 0*4) = 2, trim = 2
        //   desiredY = 55 + 2 = 57
        // Relative: 57 - 10 = 47, lerped from 0 at 0.8 = 37.6
        // Final = 10 + 37.6 = 47.6
        Assert.True(result > 40f, $"Expected pull-up, got {result}");
    }

    [Fact]
    public void ComputeTerrainAvoidanceY_Obstructed_PullsUpAggressively()
    {
        var state = new TerrainAvoidanceState();
        var input = new TerrainAvoidanceLogic.TerrainInput(
            missileAltitude: 10f,
            radarAlt: 10f,
            verticalVelocity: 0f,
            speed: 200f,
            terrainHeightAtLookahead: 0f,
            lookaheadObstructed: true);

        float result = TerrainAvoidanceLogic.ComputeTerrainAvoidanceY(state, input, altitudeTarget: 5f, seaLevelY: 0f);

        // Obstructed: lerps towards 1000 at 0.1 factor = 100
        // Final = 10 + 100 = 110
        Assert.True(result > 50f, $"Expected aggressive pull-up on obstruction, got {result}");
    }

    [Fact]
    public void ComputeTerrainAvoidanceY_NullState_Throws()
    {
        var input = new TerrainAvoidanceLogic.TerrainInput(10f, 10f, 0f, 200f, 0f, false);
        Assert.Throws<ArgumentNullException>(() =>
            TerrainAvoidanceLogic.ComputeTerrainAvoidanceY(null!, input, 5f, 0f));
    }

    [Fact]
    public void ComputeTerrainAvoidanceY_AltitudeTrimAccumulates()
    {
        var state = new TerrainAvoidanceState();
        var input = new TerrainAvoidanceLogic.TerrainInput(
            missileAltitude: 6f,
            radarAlt: 2f,
            verticalVelocity: -1f,  // descending
            speed: 200f,
            terrainHeightAtLookahead: 4f,
            lookaheadObstructed: false);

        // First call establishes trim
        float result1 = TerrainAvoidanceLogic.ComputeTerrainAvoidanceY(state, input, altitudeTarget: 5f, seaLevelY: 0f);
        float firstTrim = state.AltitudeTrim;

        // Second call with same input should accumulate more trim
        float result2 = TerrainAvoidanceLogic.ComputeTerrainAvoidanceY(state, input, altitudeTarget: 5f, seaLevelY: 0f);

        Assert.True(state.AltitudeTrim > firstTrim, "Altitude trim should accumulate");
        Assert.True(result2 > result1, "Second result should be higher due to accumulated trim");
    }

    [Fact]
    public void ComputeTerrainAvoidanceY_AltitudeTrimResetsWhenSafe()
    {
        var state = new TerrainAvoidanceState { AltitudeTrim = 50f };

        // Missile well above terrain
        var input = new TerrainAvoidanceLogic.TerrainInput(
            missileAltitude: 100f,
            radarAlt: 100f,  // well above altTarget*2
            verticalVelocity: 0f,
            speed: 200f,
            terrainHeightAtLookahead: 0f,
            lookaheadObstructed: false);

        TerrainAvoidanceLogic.ComputeTerrainAvoidanceY(state, input, altitudeTarget: 5f, seaLevelY: 0f);

        Assert.Equal(0f, state.AltitudeTrim);
    }

    [Fact]
    public void ComputeTerrainAvoidanceY_SeaLevelClampsTerrainHeight()
    {
        var state = new TerrainAvoidanceState();
        // Terrain height below sea level should be clamped to sea level
        var input = new TerrainAvoidanceLogic.TerrainInput(
            missileAltitude: 10f,
            radarAlt: 10f,
            verticalVelocity: 0f,
            speed: 200f,
            terrainHeightAtLookahead: -5f,  // below sea level
            lookaheadObstructed: false);

        float result = TerrainAvoidanceLogic.ComputeTerrainAvoidanceY(state, input, altitudeTarget: 5f, seaLevelY: 0f);

        // Should use seaLevel(0) + altTarget(5) = 5, not -5 + 5 = 0
        // Same as flat terrain test
        Assert.Equal(6f, result, precision: 1);
    }

    [Fact]
    public void ComputeTerrainAvoidanceY_MinimumHeight_EnforcesFloorOverWater()
    {
        var state = new TerrainAvoidanceState();
        // Simulate over water: terrain at sea level, missile is low
        var input = new TerrainAvoidanceLogic.TerrainInput(
            missileAltitude: 1f,
            radarAlt: 1f,
            verticalVelocity: -2f,  // descending
            speed: 200f,
            terrainHeightAtLookahead: 0f,  // sea level (water)
            lookaheadObstructed: false);

        float result = TerrainAvoidanceLogic.ComputeTerrainAvoidanceY(
            state, input, altitudeTarget: 5f, seaLevelY: 0f, minimumHeight: 3f);

        // Result must be at least seaLevel(0) + minimumHeight(3) = 3
        Assert.True(result >= 3f, $"Expected at least 3f over water, got {result}");
    }

    [Fact]
    public void ComputeTerrainAvoidanceY_MinimumHeight_DoesNotAffectHighAltitude()
    {
        var state = new TerrainAvoidanceState();
        var input = new TerrainAvoidanceLogic.TerrainInput(
            missileAltitude: 100f,
            radarAlt: 100f,
            verticalVelocity: 0f,
            speed: 200f,
            terrainHeightAtLookahead: 0f,
            lookaheadObstructed: false);

        float withMin = TerrainAvoidanceLogic.ComputeTerrainAvoidanceY(
            state, input, altitudeTarget: 5f, seaLevelY: 0f, minimumHeight: 3f);

        var state2 = new TerrainAvoidanceState();
        float withoutMin = TerrainAvoidanceLogic.ComputeTerrainAvoidanceY(
            state2, input, altitudeTarget: 5f, seaLevelY: 0f, minimumHeight: 0f);

        // At high altitude the minimum height should not change the result
        Assert.Equal(withoutMin, withMin, precision: 1);
    }
}
