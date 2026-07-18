using System.Text.Json;
using Profiler.Api.Entities;

namespace Profiler.Api.Tests;

/// <summary>
/// Deterministic synthetic keystroke sessions with a distinct per-user timing
/// signature (both dwell and flight differ), so identification is genuinely learnable.
/// </summary>
public static class SyntheticData
{
    private const string SampleText =
        "the quick brown fox jumps over the lazy dog while the sun is shining and the birds are singing in the trees";

    public static List<KeystrokeEvent> BuildSession(int baseDwell, int baseFlight, int variance, int seed)
    {
        var rnd = new Random(seed);
        var events = new List<KeystrokeEvent>();
        long t = 1_000_000; // arbitrary epoch

        foreach (char c in SampleText)
        {
            int dwell = Math.Max(10, baseDwell + rnd.Next(-variance, variance + 1));
            events.Add(new KeystrokeEvent { Key = c.ToString(), Type = "keydown", Timestamp = t });
            t += dwell;
            events.Add(new KeystrokeEvent { Key = c.ToString(), Type = "keyup", Timestamp = t });
            int flight = Math.Max(5, baseFlight + rnd.Next(-variance, variance + 1));
            t += flight;
        }

        return events;
    }

    public record UserProfile(string Nick, int BaseDwell, int BaseFlight, int Variance);

    public static readonly UserProfile[] Users =
    {
        new("alice", BaseDwell: 60,  BaseFlight: 40,  Variance: 6),
        new("bob",   BaseDwell: 95,  BaseFlight: 90,  Variance: 8),
        new("carol", BaseDwell: 130, BaseFlight: 150, Variance: 10),
        new("dave",  BaseDwell: 170, BaseFlight: 220, Variance: 12),
    };

    public static List<TypingSession> BuildSessions(int sessionsPerUser)
    {
        var sessions = new List<TypingSession>();
        int seed = 1;
        foreach (var user in Users)
        {
            for (int i = 0; i < sessionsPerUser; i++)
            {
                var events = BuildSession(user.BaseDwell, user.BaseFlight, user.Variance, seed++);
                sessions.Add(new TypingSession
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Nick,
                    Platform = "Desktop",
                    CreatedAt = DateTime.UtcNow,
                    RawDataJson = JsonSerializer.Serialize(events)
                });
            }
        }
        return sessions;
    }
}
