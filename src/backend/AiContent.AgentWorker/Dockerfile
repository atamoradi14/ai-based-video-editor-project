FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/backend ./src/backend
RUN dotnet publish src/backend/AiContent.AgentWorker/AiContent.AgentWorker.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ffmpeg fonts-noto-core \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /data /tmp/aicontent \
    && chown -R "$APP_UID" /data /tmp/aicontent
WORKDIR /app
COPY --from=build /app .
USER 1654
EXPOSE 8081
HEALTHCHECK --interval=30s --timeout=3s --start-period=20s CMD curl -fsS http://127.0.0.1:8081/ || exit 1
ENTRYPOINT ["dotnet", "AiContent.AgentWorker.dll"]
