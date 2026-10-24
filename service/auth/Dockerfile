# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0.402 AS build
WORKDIR /source
COPY global.json ./
COPY src/RepoAgent.Auth.Core/RepoAgent.Auth.Core.csproj src/RepoAgent.Auth.Core/
COPY src/RepoAgent.Auth/RepoAgent.Auth.csproj src/RepoAgent.Auth/
RUN dotnet restore src/RepoAgent.Auth/RepoAgent.Auth.csproj
COPY src/ src/
RUN dotnet publish src/RepoAgent.Auth/RepoAgent.Auth.csproj --configuration Release \
    --no-restore --output /publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl jq \
    && rm -rf /var/lib/apt/lists/*
LABEL io.repoagent.role="auth-service"
WORKDIR /app
COPY --from=build /publish ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER 1654:1654
ENTRYPOINT ["dotnet", "RepoAgent.Auth.dll"]
