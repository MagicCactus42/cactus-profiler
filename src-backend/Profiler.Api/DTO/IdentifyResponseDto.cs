namespace Profiler.Api.Dto
{
    public class IdentifyResponseDto
    {
        public string User { get; set; }
        public float Confidence { get; set; }
        public string Message { get; set; }
        public string Status { get; set; }
        public string SessionId { get; set; }
    }
}