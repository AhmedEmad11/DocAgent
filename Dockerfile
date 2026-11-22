# DocAgent API image. Build: docker build -t docagent-api .   Migrate: docker run --rm -e ConnectionStrings__docagentdb=... docagent-api --migrate
# Kept to 2 stages and under 3,000 bytes (SnapDeploy free tier limits); see docs/DEPLOYMENT.md.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# The RID follows the build platform: SnapDeploy is arm64, Azure Container Apps amd64 (a RID-specific app dll loads only on its own arch).
ARG TARGETARCH
WORKDIR /src
# Embedding model (SRS D9, D30): pinned revision, SHA-256 verified. First, so the layer stays cached.
COPY scripts/download-models.sh scripts/download-models.sh
RUN bash scripts/download-models.sh /models/multilingual-e5-small
COPY src/DocAgent.Api/DocAgent.Api.csproj src/DocAgent.Api/
COPY src/DocAgent.Core/DocAgent.Core.csproj src/DocAgent.Core/
COPY src/DocAgent.Infrastructure/DocAgent.Infrastructure.csproj src/DocAgent.Infrastructure/
COPY src/DocAgent.ServiceDefaults/DocAgent.ServiceDefaults.csproj src/DocAgent.ServiceDefaults/
RUN RID=$([ "$TARGETARCH" = "arm64" ] && echo linux-arm64 || echo linux-x64) \
    && dotnet restore src/DocAgent.Api/DocAgent.Api.csproj -r $RID
COPY src/ src/
RUN RID=$([ "$TARGETARCH" = "arm64" ] && echo linux-arm64 || echo linux-x64) \
    && dotnet publish src/DocAgent.Api/DocAgent.Api.csproj -c Release -r $RID --no-self-contained -o /app --no-restore /p:UseAppHost=false

# Runtime: Debian (ONNX Runtime needs glibc), non-root user "app".
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app ./
COPY --from=build /models ./models
# Listens on 8080, or on PORT when the host sets it (SnapDeploy). GC tuned for a 512 MB container; all overridable.
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=0 \
    DOTNET_gcConcurrent=0 \
    DOTNET_GCConserveMemory=7 \
    DOTNET_GCgen0size=0x1000000 \
    DOTNET_TieredPGO=0 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "DocAgent.Api.dll"]
