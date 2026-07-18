using Microsoft.ML;
using Profiler.Api.Models;
using Xunit;

namespace Profiler.Api.Tests;

public class GroupSplitTests
{
    [Fact]
    public void GroupAwareSplit_KeepsEachSessionOnOneSide()
    {
        // Mimic augmentation: several rows share a GroupKey (one source session).
        // Group-aware splitting must never place a group's rows on both sides,
        // which is exactly what prevents augmented-window leakage.
        var ml = new MLContext(seed: 1);
        var rows = new List<ProfilingModelInput>();
        for (int session = 0; session < 40; session++)
            for (int window = 0; window < 4; window++)
                rows.Add(new ProfilingModelInput
                {
                    UserId = $"user{session % 4}",
                    GroupKey = $"session-{session}",
                    MeanDwellTime = session + window
                });

        var dv = ml.Data.LoadFromEnumerable(rows);
        var split = ml.Data.TrainTestSplit(dv, testFraction: 0.25,
            samplingKeyColumnName: nameof(ProfilingModelInput.GroupKey));

        var trainGroups = ml.Data.CreateEnumerable<ProfilingModelInput>(split.TrainSet, reuseRowObject: false)
            .Select(r => r.GroupKey).ToHashSet();
        var testGroups = ml.Data.CreateEnumerable<ProfilingModelInput>(split.TestSet, reuseRowObject: false)
            .Select(r => r.GroupKey).ToHashSet();

        Assert.NotEmpty(trainGroups);
        Assert.NotEmpty(testGroups);
        Assert.Empty(trainGroups.Intersect(testGroups)); // no session straddles the split
    }
}
