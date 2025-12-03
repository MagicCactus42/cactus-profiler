# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj and restore dependencies
COPY ["Profiler.Api.csproj", "./"]
RUN dotnet restore "Profiler.Api.csproj"

# Copy everything else and build
COPY . .
RUN dotnet build "Profiler.Api.csproj" -c Release -o /app/build

# Publish stage
FROM build AS publish
RUN dotnet publish "Profiler.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Final stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Install curl for healthcheck
RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

# Create non-root user for security
RUN groupadd -r profiler && useradd -r -g profiler profiler

# Create directories for model storage
RUN mkdir -p /app/model && chown -R profiler:profiler /app

# Copy published app
COPY --from=publish /app/publish .

# Set ownership
RUN chown -R profiler:profiler /app

# Switch to non-root user
USER profiler

# Expose port
EXPOSE 5000

# Set environment variables
ENV ASPNETCORE_URLS=http://+:5000
ENV DOTNET_RUNNING_IN_CONTAINER=true

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD curl -f http://localhost:5000/health || exit 1

# Entry point
ENTRYPOINT ["dotnet", "Profiler.Api.dll"]
