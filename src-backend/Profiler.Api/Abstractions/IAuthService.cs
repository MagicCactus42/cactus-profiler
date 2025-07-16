using Profiler.Api.Entities;

namespace Profiler.Api.abstractions
{
    public interface IAuthService
    {
        string HashPassword(string password);

        bool VerifyPassword(string password, string passwordHash);

        string GenerateJwtToken(User user);
    }
}