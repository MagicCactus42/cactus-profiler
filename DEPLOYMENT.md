# Profiler Deployment Guide

This guide covers both local development and VPS production deployment.

## Table of Contents
1. [Local Development](#local-development)
2. [VPS Deployment with Docker](#vps-deployment-with-docker)
3. [Manual VPS Deployment](#manual-vps-deployment)
4. [Configuration](#configuration)
5. [SSL/HTTPS Setup](#sslhttps-setup)
6. [Monitoring](#monitoring)

---

## Local Development

### Prerequisites
- Node.js 18+
- .NET 8.0 SDK
- PostgreSQL 15+

### Backend Setup

```bash
# Navigate to backend
cd src-backend/Profiler.Api

# Restore packages
dotnet restore

# Run in development mode
dotnet run
```

Backend runs on: `http://localhost:5000`

### Frontend Setup

```bash
# Navigate to frontend
cd src-frontend

# Install dependencies
npm install

# Run in development mode
npm start
```

Frontend runs on: `http://localhost:3000`

### Local PostgreSQL

Make sure PostgreSQL is running with these default settings (or update `appsettings.Development.json`):
- Host: localhost
- Port: 5432
- Database: profiler_db
- User: postgres
- Password: (your password)

---

## VPS Deployment with Docker

### Prerequisites
- Docker 20.10+
- Docker Compose 2.0+
- Domain name (optional, but recommended)

### Quick Start

1. **Clone the repository**
```bash
git clone https://github.com/MagicCactus42/profiler.git
cd profiler
```

2. **Create environment file**
```bash
cp .env.example .env
nano .env  # Edit with your production values
```

3. **Important: Update these values in `.env`**
```bash
POSTGRES_PASSWORD=your_secure_password_here
JWT_KEY=YourSuperSecretKeyThatIsAtLeast32CharactersLong_CHANGE_THIS!
ALLOWED_ORIGINS=https://yourdomain.com
REACT_APP_API_URL=https://api.yourdomain.com
```

4. **Build and start containers**
```bash
docker-compose up -d --build
```

5. **Check status**
```bash
docker-compose ps
docker-compose logs -f
```

### Docker Commands

```bash
# Start all services
docker-compose up -d

# Stop all services
docker-compose down

# View logs
docker-compose logs -f backend
docker-compose logs -f frontend

# Rebuild after code changes
docker-compose up -d --build

# Restart a specific service
docker-compose restart backend

# Access container shell
docker exec -it profiler-backend /bin/bash

# View database
docker exec -it profiler-db psql -U profiler -d profiler_db
```

### Updating Deployment

```bash
# Pull latest code
git pull

# Rebuild and restart
docker-compose up -d --build
```

---

## Manual VPS Deployment

If you prefer not to use Docker:

### 1. Install Prerequisites

```bash
# Ubuntu/Debian
sudo apt update
sudo apt install -y nginx postgresql postgresql-contrib nodejs npm

# Install .NET 8
wget https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
sudo apt update
sudo apt install -y dotnet-sdk-8.0
```

### 2. Setup PostgreSQL

```bash
sudo -u postgres psql
CREATE USER profiler WITH PASSWORD 'your_password';
CREATE DATABASE profiler_db OWNER profiler;
\q
```

### 3. Deploy Backend

```bash
cd src-backend/Profiler.Api
dotnet publish -c Release -o /var/www/profiler-api

# Create service file
sudo nano /etc/systemd/system/profiler-api.service
```

```ini
[Unit]
Description=Profiler API
After=network.target postgresql.service

[Service]
WorkingDirectory=/var/www/profiler-api
ExecStart=/usr/bin/dotnet /var/www/profiler-api/Profiler.Api.dll
Restart=always
RestartSec=10
User=www-data
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://localhost:5000

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl enable profiler-api
sudo systemctl start profiler-api
```

### 4. Deploy Frontend

```bash
cd src-frontend
npm ci
REACT_APP_API_URL=https://api.yourdomain.com npm run build
sudo cp -r build/* /var/www/profiler-frontend/
```

### 5. Configure Nginx

```bash
sudo nano /etc/nginx/sites-available/profiler
```

```nginx
# Frontend
server {
    listen 80;
    server_name yourdomain.com;
    root /var/www/profiler-frontend;
    index index.html;

    location / {
        try_files $uri $uri/ /index.html;
    }
}

# Backend API
server {
    listen 80;
    server_name api.yourdomain.com;

    location / {
        proxy_pass http://localhost:5000;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection keep-alive;
        proxy_set_header Host $host;
        proxy_cache_bypass $http_upgrade;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

```bash
sudo ln -s /etc/nginx/sites-available/profiler /etc/nginx/sites-enabled/
sudo nginx -t
sudo systemctl restart nginx
```

---

## Configuration

### Environment Variables

| Variable | Description | Default |
|----------|-------------|---------|
| `POSTGRES_USER` | Database username | profiler |
| `POSTGRES_PASSWORD` | Database password | (required) |
| `POSTGRES_DB` | Database name | profiler_db |
| `JWT_KEY` | JWT signing key (32+ chars) | (required) |
| `JWT_ISSUER` | JWT issuer | ProfilerApi |
| `JWT_AUDIENCE` | JWT audience | ProfilerApp |
| `ALLOWED_ORIGINS` | CORS origins (comma-separated) | http://localhost:3000 |
| `REACT_APP_API_URL` | Backend API URL for frontend | http://localhost:5000 |
| `ASPNETCORE_ENVIRONMENT` | Environment mode | Production |

### Backend Configuration Files

- `appsettings.json` - Base configuration
- `appsettings.Development.json` - Development overrides
- `appsettings.Production.json` - Production overrides (create if needed)

---

## SSL/HTTPS Setup

### Using Let's Encrypt (Recommended)

```bash
# Install certbot
sudo apt install certbot python3-certbot-nginx

# Get certificates
sudo certbot --nginx -d yourdomain.com -d api.yourdomain.com

# Auto-renewal is configured automatically
```

### Using Custom Certificates

1. Place certificates in `/etc/nginx/ssl/`
2. Update nginx config:

```nginx
server {
    listen 443 ssl;
    server_name yourdomain.com;

    ssl_certificate /etc/nginx/ssl/cert.pem;
    ssl_certificate_key /etc/nginx/ssl/key.pem;

    # ... rest of config
}
```

---

## Monitoring

### Health Check Endpoints

- `/health` - Full health status (JSON)
- `/health/ready` - Database connectivity
- `/health/live` - Application liveness

### Example Health Check Response

```json
{
  "status": "Healthy",
  "checks": [
    { "name": "postgres", "status": "Healthy", "duration": 5.2 },
    { "name": "self", "status": "Healthy", "duration": 0.1 }
  ],
  "totalDuration": 5.5
}
```

### Monitoring with Docker

```bash
# Check container health
docker inspect profiler-backend --format='{{.State.Health.Status}}'

# View resource usage
docker stats profiler-backend profiler-frontend profiler-db
```

### Log Locations

- Docker: `docker-compose logs -f`
- Systemd: `journalctl -u profiler-api -f`
- Nginx: `/var/log/nginx/access.log`, `/var/log/nginx/error.log`

---

## Troubleshooting

### Common Issues

**Backend won't start**
```bash
# Check logs
docker-compose logs backend
# or
journalctl -u profiler-api -n 100

# Verify database connection
docker exec -it profiler-db psql -U profiler -d profiler_db -c "SELECT 1"
```

**Frontend can't connect to API**
- Check `REACT_APP_API_URL` is correct
- Verify CORS settings in `ALLOWED_ORIGINS`
- Check nginx proxy configuration

**Database migration errors**
```bash
# The app auto-migrates, but if issues persist:
docker exec -it profiler-backend dotnet ef database update
```

**Model training fails**
- Ensure at least 5 typing sessions exist
- Check available memory (training is memory-intensive)

---

## Backup

### Database Backup

```bash
# Docker
docker exec profiler-db pg_dump -U profiler profiler_db > backup.sql

# Restore
cat backup.sql | docker exec -i profiler-db psql -U profiler profiler_db
```

### Model Backup

The trained ML model is stored in the backend container at `/app/model/`. To persist it:

```bash
# Copy model out of container
docker cp profiler-backend:/app/user_typing_model.zip ./backup/
```

---

## Performance Tuning

### For High Traffic

1. Increase PostgreSQL connections in `docker-compose.yml`
2. Add Redis for session caching
3. Consider horizontal scaling with multiple backend instances

### Resource Requirements

**Minimum (1-10 users)**
- 1 CPU core
- 2 GB RAM
- 10 GB storage

**Recommended (10-100 users)**
- 2 CPU cores
- 4 GB RAM
- 20 GB storage

**Note**: Model training is the most resource-intensive operation. For large datasets, consider training during off-peak hours.
