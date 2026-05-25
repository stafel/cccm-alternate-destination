using AlteredDestination.Logic;

namespace AlteredDestination.Tests;

public class SpreadOffsetLogicTests
{
    [Fact]
    public void ComputeDeterministicOffset_ReturnsZero_WhenRadiusIsZero()
    {
        var offset = SpreadOffsetLogic.ComputeDeterministicOffset(seed: 42, spreadRadius: 0f);

        Assert.Equal(0f, offset.X);
        Assert.Equal(0f, offset.Z);
    }

    [Fact]
    public void ComputeDeterministicOffset_ReturnsDeterministicResult_ForSameSeed()
    {
        var first = SpreadOffsetLogic.ComputeDeterministicOffset(seed: 77, spreadRadius: 15f);
        var second = SpreadOffsetLogic.ComputeDeterministicOffset(seed: 77, spreadRadius: 15f);

        Assert.Equal(first.X, second.X, 5);
        Assert.Equal(first.Z, second.Z, 5);
    }

    [Fact]
    public void ComputeDeterministicOffset_StaysWithinSpreadRadius()
    {
        float radius = 15f;
        var offset = SpreadOffsetLogic.ComputeDeterministicOffset(seed: 100, spreadRadius: radius);
        var distance = MathF.Sqrt((offset.X * offset.X) + (offset.Z * offset.Z));

        Assert.InRange(distance, 0f, radius);
    }
}
