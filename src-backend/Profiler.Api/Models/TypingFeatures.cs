
namespace Profiler.Api.Models
{
    public class TypingFeatures
    {
        
        public float MeanDwellTime { get; set; } 
        
        public float MeanFlightTime { get; set; } 
        
        public float TypingSpeedKPM { get; set; } 

        
        public Dictionary<string, float> KeyDwellTimes { get; set; } = new();

        public Dictionary<string, float> DigraphFlightTimes { get; set; } = new();
    }
}