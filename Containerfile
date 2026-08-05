# syntax=docker/dockerfile:1
#
# Multi-stage build for the web application (TECHNICAL_SPECIFICATION §14.1, BUILD_PROGRESS).
#   * build stage: the .NET SDK restores and publishes a Release build;
#   * runtime stage: the smaller ASP.NET runtime image, plus tzdata (the app resolves
#     RESTAURANT_TIME_ZONE through TimeZoneInfo — globalization is NOT invariant) and curl
#     (the compose healthchecks call /healthz/* with it).
#
# Build context is the repository root so Central Package Management files resolve during restore.

# ---- build -------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

# What this build is (TECHNICAL_SPECIFICATION §11.9). VERSION defaults to Directory.Build.props'
# VersionPrefix so a plain `podman build` with no arguments still produces something coherent;
# SOURCE_REVISION is empty unless a pipeline supplies it, and an empty one is reported as "not
# recorded" rather than guessed at. Both are ARGs rather than a file in the tree because the tree
# does not know its own commit — git metadata is not in the build context, and the .NET SDK only
# appends SourceRevisionId by itself when SourceLink is installed to set
# SourceControlInformationFeatureSupported, which is a package dependency for one string.
ARG VERSION=1.0.0
ARG SOURCE_REVISION=

# Copy the whole tree and publish. (Layer caching could be tightened by copying the *.csproj and
# Directory.*.props first and restoring before the rest — deferred; correctness first.)
COPY . .

# ${SOURCE_REVISION:+...} expands to "+<revision>" only when the argument is set and non-empty, so an
# unstamped build gets a clean "1.0.0" rather than a trailing "+" that BuildInformation would have to
# treat as a revision it does not have.
RUN INFORMATIONAL_VERSION="${VERSION}${SOURCE_REVISION:++${SOURCE_REVISION}}" \
    && echo "building MyRestaurant ${INFORMATIONAL_VERSION}" \
    && dotnet publish src/MyRestaurant.WebApplication/MyRestaurant.WebApplication.csproj \
        --configuration Release \
        --output /app/publish \
        /p:UseAppHost=false \
        /p:Version="${VERSION}" \
        /p:InformationalVersion="${INFORMATIONAL_VERSION}"

# ---- runtime -----------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# tzdata for TimeZoneInfo; curl for the container healthcheck. Clean apt lists to keep the layer small.
RUN apt-get update \
    && apt-get install -y --no-install-recommends tzdata curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# The app is only reached through the trusted proxy, so it serves plain HTTP inside the network.
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0

EXPOSE 8080

ENTRYPOINT ["dotnet", "MyRestaurant.WebApplication.dll"]
