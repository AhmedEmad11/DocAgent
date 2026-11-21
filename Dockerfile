# DocAgent API image (Phase 8): SnapDeploy free tier (512 MB / 0.25 vCPU, primary) or Azure Container Apps. Build from the repository root:
#   docker build -t docagent-api .
# Run the deploy-step migrations with the same image:  docker run --rm -e ConnectionStrings__docagentdb=... docagent-api --migrate

# ---- 1. Embedding model (SRS D9, D30): pinned Hugging Face revision, SHA-256 verified by the script. Own stage so it is cached until the script changes.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS models
WORKDIR /src
COPY scripts/download-models.sh scripts/download-models.sh
RUN bash scripts/download-models.sh /models/multilingual-e5-small

# ---- 2. Restore (cached until a project file changes) and publish
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# Runtime-specific build: without a RID the publish folder carries ONNX Runtime natives for every OS (about 240 MB of dead weight). Container Apps runs linux-x64.
ARG RID=linux-x64
WORKDIR /src
COPY src/DocAgent.Api/DocAgent.Api.csproj src/DocAgent.Api/
COPY src/DocAgent.Core/DocAgent.Core.csproj src/DocAgent.Core/
COPY src/DocAgent.Infrastructure/DocAgent.Infrastructure.csproj src/DocAgent.Infrastructure/
COPY src/DocAgent.ServiceDefaults/DocAgent.ServiceDefaults.csproj src/DocAgent.ServiceDefaults/
RUN dotnet restore src/DocAgent.Api/DocAgent.Api.csproj -r $RID
COPY src/ src/
RUN dotnet publish src/DocAgent.Api/DocAgent.Api.csproj -c Release -r $RID --no-self-contained -o /app --no-restore /p:UseAppHost=false

# ---- 3. Runtime: ASP.NET Core 10 on Debian (glibc: ONNX Runtime has no musl build), non-root
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app ./
COPY --from=models /models ./models

# Listens on 8080 (the aspnet image sets ASPNETCORE_HTTP_PORTS=8080; Container Apps targets it) unless the host passes PORT (SnapDeploy): the app then listens on that.
# The image's built-in non-root user "app" (UID 1654) runs the process.
# Memory for a 512 MB container: workstation, non-concurrent GC (no background GC thread/heap copies), conserve-memory mode, a 16 MB gen0 budget
# (so garbage is collected instead of piling up next to the native ONNX Runtime memory). All overridable at run time.
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=0 \
    DOTNET_gcConcurrent=0 \
    DOTNET_GCConserveMemory=7 \
    DOTNET_GCgen0size=0x1000000 \
    DOTNET_TieredPGO=0 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
USER app
# Health endpoints exist (/health/live, /health/ready); Container Apps probes them (infra/main.bicep), so no HEALTHCHECK instruction here.
ENTRYPOINT ["dotnet", "DocAgent.Api.dll"]
