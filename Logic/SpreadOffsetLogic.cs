using System;

namespace AlteredDestination.Logic
{
    public static class SpreadOffsetLogic
    {
        public static (float X, float Z) ComputeDeterministicOffset(int seed, float spreadRadius)
        {
            if (spreadRadius <= 0f)
            {
                return (0f, 0f);
            }

            System.Random random = new System.Random(seed);
            float angle = (float)random.NextDouble() * (float)(Math.PI * 2d);
            float radius = MathF.Sqrt((float)random.NextDouble()) * spreadRadius;

            float x = MathF.Cos(angle) * radius;
            float z = MathF.Sin(angle) * radius;
            return (x, z);
        }
    }
}
