using Profiler.Api.Entities;

namespace Profiler.Api.Dto
{
    public class SubmitSessionRequest
    {
        public string UserId { get; set; }
        public string Platform { get; set; }
        public List<KeystrokeEvent> Events { get; set; }
        public string SessionId { get; set; }
    }
}