namespace Profiler.Api.abstractions;

public interface IIdentificationSessionService
{
    (string BestUser, float Confidence, int SamplesCount) AddEvidence(string sessionId, string[] allLabels, float[] newScores);
}