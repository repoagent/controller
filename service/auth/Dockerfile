# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:11.0.100-rc.3-resolute@sha256:3a9175b6c54b2f076817af1360f691c9994447b192faa48a1a1f112a516844c7 AS build
WORKDIR /source
COPY global.json Directory.Build.props .editorconfig ./
COPY service/auth/global.json service/auth/
COPY service/auth/Directory.Build.props service/auth/version.txt service/auth/
COPY src/RepoAgent.LocalTelemetry/RepoAgent.LocalTelemetry.csproj src/RepoAgent.LocalTelemetry/
WORKDIR /source/service/auth
COPY service/auth/src/RepoAgent.Auth.Core/RepoAgent.Auth.Core.csproj src/RepoAgent.Auth.Core/
COPY service/auth/src/RepoAgent.Auth/RepoAgent.Auth.csproj src/RepoAgent.Auth/
RUN dotnet restore src/RepoAgent.Auth/RepoAgent.Auth.csproj
COPY service/auth/src/ src/
COPY src/RepoAgent.LocalTelemetry/ /source/src/RepoAgent.LocalTelemetry/
ARG SOURCE_REVISION
ARG BROKER_VERSION
RUN dotnet publish src/RepoAgent.Auth/RepoAgent.Auth.csproj --configuration Release \
    --no-restore --output /publish /p:UseAppHost=false /p:SourceRevisionId="$SOURCE_REVISION"

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-rc.3@sha256:bae0269049d5061ab7f49995cc1acebf54b75471c096c4a1f874e27661d44045
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl jq \
    && rm -rf /var/lib/apt/lists/*
LABEL io.repoagent.role="auth-service"
ARG BROKER_VERSION
LABEL org.opencontainers.image.version="$BROKER_VERSION"
WORKDIR /app
COPY --from=build /publish ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER 1654:1654
ENTRYPOINT ["dotnet", "RepoAgent.Auth.dll"]
