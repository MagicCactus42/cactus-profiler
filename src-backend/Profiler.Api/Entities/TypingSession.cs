namespace Profiler.Api.Entities;

public class TypingSession
{
    public Guid Id { get; set; }
    public string UserId { get; set; } 
    public string RawDataJson { get; set; } 
    public string Platform { get; set; } 
    public DateTime CreatedAt { get; set; }
}

