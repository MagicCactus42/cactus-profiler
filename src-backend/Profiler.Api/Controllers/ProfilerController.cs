using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;
using Profiler.Api.DAL;
using Profiler.Api.Entities;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Profiler.Api.abstractions;
using Profiler.Api.Dto;

namespace Profiler.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [EnableRateLimiting("ml")]
    public class ProfilerController : ControllerBase
    {
        private readonly ProfilerDbContext _dbContext;
        private readonly IFeatureExtractorService _featureExtractor;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IModelPredictionService _predictionService;
        private readonly IIdentificationSessionService _sessionService;
        private readonly ILogger<ProfilerController> _logger;

        public ProfilerController(
            ProfilerDbContext dbContext,
            IFeatureExtractorService featureExtractor,
            IServiceScopeFactory scopeFactory,
            IModelPredictionService predictionService,
            IIdentificationSessionService sessionService,
            ILogger<ProfilerController> logger)
        {
            _dbContext = dbContext;
            _featureExtractor = featureExtractor;
            _scopeFactory = scopeFactory;
            _predictionService = predictionService;
            _sessionService = sessionService;
            _logger = logger;
        }

        [Authorize]
        [HttpPost("session")]
        public async Task<IActionResult> SubmitSession([FromBody] SubmitSessionRequest request)
        {
            var username = User.FindFirst(ClaimTypes.Name)?.Value
                           ?? User.FindFirst("unique_name")?.Value;

            if (string.IsNullOrEmpty(username)) return Unauthorized();

            var session = new TypingSession
            {
                Id = Guid.NewGuid(),
                UserId = username,
                Platform = request.Platform,
                RawDataJson = JsonSerializer.Serialize(request.Events),
                CreatedAt = DateTime.UtcNow
            };

            _dbContext.TypingSessions.Add(session);
            await _dbContext.SaveChangesAsync();

            int count = await _dbContext.TypingSessions.CountAsync();
            if (count % 10 == 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using (var scope = _scopeFactory.CreateScope())
                        {
                            var trainingService = scope.ServiceProvider.GetRequiredService<IModelTrainingService>();
                            trainingService.TrainAndSaveModel();
                        }
                        
                        _predictionService.ReloadModel();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Background training failed.");
                    }
                });
            }

            return Ok(new { message = "Session saved for user: " + username });
        }

        [HttpPost("train")]
        public IActionResult TrainModel()
        {
            try
            {
                using (var scope = _scopeFactory.CreateScope())
                {
                    var trainingService = scope.ServiceProvider.GetRequiredService<IModelTrainingService>();
                    trainingService.TrainAndSaveModel();
                }
                _predictionService.ReloadModel();
                return Ok(new { message = "Model trained and reloaded successfully." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpPost("identify")]
        public IActionResult IdentifyUser([FromBody] SubmitSessionRequest request)
        {
            if (request.Events == null || request.Events.Count < 5)
                return BadRequest(new { message = "Not enough data to identify user." });

            var features = _featureExtractor.ExtractFeatures(request.Events);
            var predictionResult = _predictionService.IdentifyUser(features);

            if (string.IsNullOrEmpty(request.SessionId))
            {
                request.SessionId = Guid.NewGuid().ToString();
            }

            // CRITICAL: Use labels from the model, not from database
            // The model's probability array corresponds to its internal label ordering
            var modelLabels = predictionResult.AllLabels;

            if (modelLabels == null || modelLabels.Length == 0)
            {
                _logger.LogWarning("Model has no labels - it may not be trained yet");
                return Ok(new IdentifyResponseDto
                {
                    User = "Unknown",
                    Confidence = 0,
                    Message = "Model not trained. Please train the model first.",
                    Status = "Error",
                    SessionId = request.SessionId
                });
            }

            _logger.LogDebug("Model Labels ({LabelCount}): {Labels}",
                modelLabels.Length, string.Join(", ", modelLabels));
            _logger.LogDebug("Model Probabilities ({ProbCount}): {Probs}",
                predictionResult.AllProbabilities.Length,
                string.Join(", ", predictionResult.AllProbabilities.Select(p => p.ToString("F3"))));

            var (bestUser, confidence, samples) = _sessionService.AddEvidence(
                request.SessionId,
                modelLabels,
                predictionResult.AllProbabilities
            );

            float threshold = samples > 3 ? 0.75f : 0.90f;
            bool isAuthenticated = confidence > threshold;
            string status = isAuthenticated ? "Authenticated" : "Continue";

            return Ok(new IdentifyResponseDto
            {
                User = bestUser,
                Confidence = confidence * 100,
                Message = isAuthenticated
                    ? $"Identified user: {bestUser}"
                    : "Gathering more evidence...",
                Status = status,
                SessionId = request.SessionId
            });
        }
    }
}