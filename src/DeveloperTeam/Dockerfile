FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY src/DeveloperTeam/DeveloperTeam.csproj src/DeveloperTeam/
RUN dotnet restore src/DeveloperTeam/DeveloperTeam.csproj
COPY src/DeveloperTeam/ src/DeveloperTeam/
RUN dotnet publish src/DeveloperTeam/DeveloperTeam.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/sdk:9.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends git nodejs npm ca-certificates \
    && npm install -g @openai/codex \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /workspaces /tmp/codex-home /tmp/dotnet-home /tmp/nuget-packages \
    && chown -R 10001:10001 /workspaces /tmp/codex-home /tmp/dotnet-home /tmp/nuget-packages
ENV DOTNET_CLI_HOME=/tmp/dotnet-home \
    NUGET_PACKAGES=/tmp/nuget-packages \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
WORKDIR /app
COPY --from=build /app/publish .
USER 10001:10001
ENTRYPOINT ["dotnet", "DeveloperTeam.dll"]
