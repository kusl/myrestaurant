# syntax=docker/dockerfile:1
#
# Multi-stage build for the web application (TECHNICAL_SPECIFICATION §14.1, BUILD_PROGRESS).
#   * build stage: the .NET SDK restores and publishes a Release build;
#   * runtime stage: the smaller ASP.NET runtime image, plus tzdata (the app resolves
#     RESTAURANT_TIME_ZONE through TimeZoneInfo — globalization is NOT invariant) and curl
#     (the compose healthchecks call /healthz/* with it).
#
# Build context is the repository root so Central Package Management files resolve during restore —
# but `.dockerignore` reduces that root to an allow-list first, and the guard below asserts that it
# did. See F-45: for the life of this file, `COPY . .` meant the entire working tree, including
# `.git`, `docs/llm/`, every test project, and — on a host that had ever taken a backup or run the
# application — `.env`, the Data Protection key ring, and the `-dataprotection.tar` that
# OPERATIONS §8 calls the key material in the clear.

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

# Copy the allow-listed context. (Layer caching could be tightened by copying the *.csproj and
# Directory.*.props first and restoring before the rest — deferred; correctness first.)
COPY . .

# The context is what `.dockerignore` says it is — asserted, not assumed.
#
# An ignore-file is an instruction to a tool, and instructions can be renamed, superseded by a
# `.containerignore` that Podman prefers, or overridden with `--ignorefile`. None of those failures
# announce themselves: the build still succeeds, and the only visible symptom is that it took
# longer. So the allow-list is stated twice on purpose — once as the instruction and once here as
# the assertion — and the build stops if the two disagree.
#
# This is a stronger check than a list of forbidden names, and deliberately so. A deny-list has to
# be extended for tomorrow's untracked secret by somebody who remembers to; this fails on any
# top-level entry nobody has thought about yet, which is the population that F-45 was actually
# about. Scope decided in one place, the way scripts/check_tree.sh decides `is_authored_text` —
# see F-41.
RUN set -eu; \
    unexpected=''; \
    for entry in * .[!.]*; do \
        [ -e "$entry" ] || continue; \
        case "$entry" in \
            .editorconfig|Directory.Build.props|Directory.Packages.props|global.json|src) ;; \
            *) unexpected="${unexpected} ${entry}" ;; \
        esac; \
    done; \
    missing=''; \
    for required in \
        .editorconfig \
        Directory.Build.props \
        Directory.Packages.props \
        global.json \
        src/MyRestaurant.Domain/MyRestaurant.Domain.csproj \
        src/MyRestaurant.DataAccess/MyRestaurant.DataAccess.csproj \
        src/MyRestaurant.WebApplication/MyRestaurant.WebApplication.csproj \
    ; do \
        [ -e "$required" ] || missing="${missing} ${required}"; \
    done; \
    stale="$(find src -type d -name bin -o -type d -name obj 2>/dev/null | tr '\n' ' ')"; \
    if [ -n "$unexpected" ] || [ -n "$missing" ] || [ -n "$stale" ]; then \
        echo 'BUILD CONTEXT REJECTED — .dockerignore did not take effect (see F-45).' >&2; \
        [ -z "$unexpected" ] || echo "  not allowed here:  ${unexpected# }" >&2; \
        [ -z "$missing" ]    || echo "  required, absent:  ${missing# }" >&2; \
        [ -z "$stale" ]      || echo "  local build output:${stale%% }" >&2; \
        echo '' >&2; \
        echo '  The context root must contain exactly: .editorconfig Directory.Build.props' >&2; \
        echo '  Directory.Packages.props global.json src' >&2; \
        echo '' >&2; \
        echo '  Most likely: .dockerignore is missing from the context root, a .containerignore' >&2; \
        echo '  is shadowing it (podman prefers that name when both exist), or --ignorefile was' >&2; \
        echo '  passed. Anything else here was copied into this builder, and on a host that has' >&2; \
        echo '  taken a backup that includes the Data Protection key ring in the clear.' >&2; \
        exit 1; \
    fi; \
    echo "build context accepted: $(find . -type f | wc -l) file(s)"

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
