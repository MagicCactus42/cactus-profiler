using Profiler.Api.Services;
using Xunit;

namespace Profiler.Api.Tests;

public class CalibrationTests
{
    [Fact]
    public void TemperatureScale_AtOne_IsIdentity_NotASecondSoftmax()
    {
        // A confident, already-normalized distribution. The OLD code ran a full
        // softmax over these values, collapsing 0.90 down to ~0.4. Correct temperature
        // scaling at T=1 must leave the distribution untouched.
        var probs = new[] { 0.90f, 0.05f, 0.03f, 0.02f };
        var scaled = ProbabilityCalibration.TemperatureScale(probs, 1.0f);

        Assert.Equal(probs.Length, scaled.Length);
        for (int i = 0; i < probs.Length; i++)
            Assert.Equal(probs[i], scaled[i], precision: 4);
        Assert.Equal(1.0f, scaled.Sum(), precision: 4);
    }

    [Theory]
    [InlineData(0.5f)]
    [InlineData(1.0f)]
    [InlineData(2.5f)]
    public void TemperatureScale_AlwaysNormalizedAndPreservesArgmax(float t)
    {
        var probs = new[] { 0.10f, 0.62f, 0.18f, 0.10f };
        var scaled = ProbabilityCalibration.TemperatureScale(probs, t);

        Assert.Equal(1.0f, scaled.Sum(), precision: 4);
        Assert.Equal(1, Array.IndexOf(scaled, scaled.Max())); // argmax stays index 1
    }

    [Fact]
    public void TemperatureAboveOne_SoftensConfidence()
    {
        var probs = new[] { 0.90f, 0.05f, 0.03f, 0.02f };
        var softened = ProbabilityCalibration.TemperatureScale(probs, 3.0f);
        Assert.True(softened.Max() < probs.Max());
    }

    [Fact]
    public void FitTemperature_MinimizesValidationNll()
    {
        // Overconfident-but-often-wrong predictions: the top class is only right half
        // the time, so calibration should prefer T > 1 and cannot do worse than T = 1.
        var samples = new List<(float[] Probabilities, int TrueIndex)>();
        for (int i = 0; i < 20; i++)
        {
            var p = new[] { 0.85f, 0.08f, 0.04f, 0.03f };
            int trueIndex = i % 2 == 0 ? 0 : 1; // predicted class wrong half the time
            samples.Add((p, trueIndex));
        }

        float t = ProbabilityCalibration.FitTemperature(samples);
        Assert.InRange(t, 0.5f, 5.0f);
        Assert.True(
            ProbabilityCalibration.NegativeLogLikelihood(samples, t)
            <= ProbabilityCalibration.NegativeLogLikelihood(samples, 1.0f) + 1e-9);
    }
}
