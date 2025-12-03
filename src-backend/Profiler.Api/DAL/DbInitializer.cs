using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Profiler.Api.Entities;

namespace Profiler.Api.DAL
{
    public static class DbInitializer
    {
        private static readonly List<string> SampleTexts = new()
        {
            "The quick brown fox jumps over the lazy dog but the dog was not really lazy it was just tired from running all day long in the beautiful park under the bright sun.",
            "Software development is not just about writing code it is about solving problems and making lives better for people who use the applications we build with passion and dedication every single day.",
            "To be or not to be that is the question whether 'tis nobler in the mind to suffer the slings and arrows of outrageous fortune or to take arms against a sea of troubles.",
            "In the middle of the journey of our life I found myself within a dark woods where the straight way was lost and the path was hidden beneath the shadows of the ancient trees.",
            "Modern web applications require a robust backend and a responsive frontend to ensure that users have a seamless experience regardless of the device or network conditions they are currently using."
        };

        public static void Initialize(ProfilerDbContext context)
        {
            context.Database.Migrate();

            if (context.TypingSessions.Any())
            {
                return;
            }

            var sessions = new List<TypingSession>();
            var random = new Random();

            var users = new[]
            {
                new { Nick = "test1", BaseDelay = 40, Variance = 5 },   
                new { Nick = "test2", BaseDelay = 70, Variance = 15 },  
                new { Nick = "test3", BaseDelay = 100, Variance = 10 }, 
                new { Nick = "test4", BaseDelay = 140, Variance = 30 }, 
                new { Nick = "test5", BaseDelay = 200, Variance = 50 }  
            };

            foreach (var user in users)
            {
                for (int i = 0; i < 10; i++)
                {
                    string textToType = SampleTexts[i % SampleTexts.Count];

                    sessions.Add(CreateDummySession(
                        user.Nick, 
                        textToType, 
                        user.BaseDelay, 
                        user.Variance, 
                        random
                    ));
                }
            }

            context.TypingSessions.AddRange(sessions);
            context.SaveChanges();
        }

        private static TypingSession CreateDummySession(string userId, string text, int baseDelay, int variance, Random random)
        {
            var events = new List<KeystrokeEvent>();
            long currentTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            foreach (char c in text)
            {
                int dwellTime = 80 + random.Next(-20, 20); 

                events.Add(new KeystrokeEvent
                {
                    Key = c.ToString(),
                    Type = "keydown",
                    Timestamp = currentTime
                });

                currentTime += dwellTime;

                events.Add(new KeystrokeEvent
                {
                    Key = c.ToString(),
                    Type = "keyup",
                    Timestamp = currentTime
                });

                int flightTime = baseDelay + random.Next(-variance, variance);
                
                if (flightTime < 5) flightTime = 5; 

                currentTime += flightTime;
            }

            return new TypingSession
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Platform = "Desktop", 
                CreatedAt = DateTime.UtcNow,
                RawDataJson = JsonSerializer.Serialize(events)
            };
        }
    }
}