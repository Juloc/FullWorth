FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Without this the published assemblies carry the 0.0.0-dev default from Directory.Build.props, and
# every instance reports that as its client version. The release workflow passes the real tag.
ARG FULLWORTH_VERSION=0.0.0-dev

# Directory.Build.props sets the version default and has to be inside the build context copy, or
# MSBuild finds no props file at all when it walks up from the project directory.
COPY Directory.Build.props .

# Copy project files first so Docker can cache restore independently from source changes.
COPY src/FullWorth.Web/FullWorth.Web.csproj src/FullWorth.Web/
COPY src/FullWorth.Backend/FullWorth.Backend.csproj src/FullWorth.Backend/
COPY src/FullWorth.Compensation.Core/FullWorth.Compensation.Core.csproj src/FullWorth.Compensation.Core/
COPY src/FullWorth.Banking/FullWorth.Banking.csproj src/FullWorth.Banking/
COPY src/FullWorth.FinTs/FullWorth.FinTs.csproj src/FullWorth.FinTs/
RUN dotnet restore src/FullWorth.Web/FullWorth.Web.csproj

COPY src/FullWorth.Web/ src/FullWorth.Web/
COPY src/FullWorth.Backend/ src/FullWorth.Backend/
COPY src/FullWorth.Compensation.Core/ src/FullWorth.Compensation.Core/
COPY src/FullWorth.Banking/ src/FullWorth.Banking/
COPY src/FullWorth.FinTs/ src/FullWorth.FinTs/
COPY src/Shared/ src/Shared/

# The same build version that is compiled into the assemblies is also visible in the sidebar badge.
# Keep the source generic ("Alpha") and stamp only the build, so there is still one version source:
# FULLWORTH_VERSION -> MSBuild + visible badge.
#
# Das geschieht VOR dem Publish, nicht danach: seit #154 steht das Abzeichen in einer Razor-Partial,
# und die wird beim Publish zu Code uebersetzt - im Ergebnis gibt es keine Datei mehr, die man
# nachtraeglich aendern koennte. Vorher stand es in wwwroot/index.html, und genau darauf zeigte
# dieses sed. Das grep darunter haette den Bau angehalten; es bleibt aus demselben Grund stehen.
RUN version_label="v${FULLWORTH_VERSION}" \
 && badge=src/FullWorth.Web/Pages/Shared/_Navigation.cshtml \
 && sed -i "s|<span class=\"brand-beta\">Alpha</span>|<span class=\"brand-beta\">${version_label}</span>|" "$badge" \
 && grep -Fq "<span class=\"brand-beta\">${version_label}</span>" "$badge"

RUN dotnet publish src/FullWorth.Web/FullWorth.Web.csproj -c Release -p:Version="$FULLWORTH_VERSION" -o /app --no-restore /p:UseAppHost=false

# Codex and the Node runtime it needs. Built here rather than relied upon in the base image:
# mcr.microsoft.com/playwright/dotnet makes no promise about shipping Node, and it has changed which
# tools it carries between minors before.
#
# What this costs, measured rather than guessed: 125 MB for Node plus 332 MB for the Codex CLI and
# the native binary it ships, uncompressed, paid by every installation including the ones that never
# use Codex. In exchange the second image disappears entirely, so the registry shrinks overall and the
# release matrix loses two of its four builds.
FROM node:22-bookworm-slim AS codex
ARG CODEX_VERSION=0.151.0
RUN npm install --global "@openai/codex@${CODEX_VERSION}"

# The unified image includes the backend's browser/OCR runtime so Amazon, receipt and PDF features
# keep working after Web + Backend + Banking move into one process.
FROM mcr.microsoft.com/playwright/dotnet:v1.62.0-noble

# Two users, and the difference between them is the entire access control between the application and
# Codex. Codex parses untrusted uploads: it must never be able to read the data encryption key or the
# internal key, and the application must not be able to read the ChatGPT session under /data/codex.
#
# codex is created after app and with a fixed id, so app keeps the uid that every existing volume is
# already chowned to. The modes that make this real are set in the entrypoint, which is the only
# process here that is root.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl gosu tesseract-ocr tesseract-ocr-deu poppler-utils \
 && (getent group app >/dev/null 2>&1 || groupadd --system app) \
 && (id -u app >/dev/null 2>&1 || useradd --system --gid app --create-home app) \
 && (getent group codex >/dev/null 2>&1 || groupadd --system --gid 1990 codex) \
 && (id -u codex >/dev/null 2>&1 || useradd --system --uid 1990 --gid codex --no-create-home --home-dir /data/codex codex) \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app .
COPY ops/docker/entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh

# Node plus the Codex CLI. The @openai tree is copied whole, because the CLI's platform binary lives
# in a nested optional dependency underneath it - but only that tree: npm and corepack are build-time
# tools and nothing here installs a package at runtime.
COPY --from=codex /usr/local/bin/node /usr/local/bin/node
COPY --from=codex /usr/local/lib/node_modules/@openai /usr/local/lib/node_modules/@openai

# The bridge itself, and the wrapper the bridge invokes as "codex" - it is what pins the sandbox and
# approval flags, so the real binary is never reachable under that name by accident.
COPY src/FullWorth.CodexBridge/server.mjs /opt/fullworth-codex/server.mjs
COPY src/FullWorth.CodexBridge/codex-wrapper.mjs /usr/local/bin/codex
COPY ops/docker/fullworth-codex-launcher /usr/local/bin/fullworth-codex-launcher
# codex-real is linked here rather than copied from the build stage. COPY --from dereferences a
# symlink it is given by name, and a codex.js sitting in /usr/local/bin resolves its own package from
# the wrong directory and dies with "Missing optional dependency @openai/codex-linux-x64" - measured
# in a probe image, not guessed.
RUN ln -s /usr/local/lib/node_modules/@openai/codex/bin/codex.js /usr/local/bin/codex-real \
 && chmod 0755 /usr/local/bin/codex /usr/local/bin/fullworth-codex-launcher \
 && chmod 0644 /opt/fullworth-codex/server.mjs

# This image IS the unified host: web, backend and banking in one process, talking to themselves
# over loopback. Declaring it here rather than in every compose file means a deployment never has to
# restate the shape of the container it is running - and the same assemblies keep behaving as a plain
# web host when they run embedded in the test suite.
ENV ASPNETCORE_URLS=http://+:8080 \
    FullWorthHost__Unified=true \
    DataProtection__KeyPath=/data/dataprotection \
    ReverseProxy__KnownNetworks__0=172.16.0.0/12 \
    PLAYWRIGHT_BROWSERS_PATH=/ms-playwright

# Where this image expects its secrets. Paths, not values: a deploy stack only has to mount the
# volume here instead of restating nine paths in its own file. A path that is not mounted is simply
# skipped (SecretBootstrap only reads files that exist), so a stack that supplies its secrets some
# other way - the dev stack does - is unaffected.
#
# All six are THIS installation's own secrets. Nothing the Cloud owns is listed here on purpose.
ENV Database__Password_FILE=/run/fullworth-secrets/fullworth_db_password \
    Security__InternalKey_FILE=/run/fullworth-secrets/backend_internal_key \
    Security__IngestKey_FILE=/run/fullworth-secrets/ingest_key \
    Security__ApiKey_FILE=/run/fullworth-secrets/banking_api_key \
    Security__DataEncryptionKey_FILE=/run/fullworth-secrets/data_encryption_key \
    Services__BackendInternalKey_FILE=/run/fullworth-secrets/backend_internal_key \
    Services__BankingApiKey_FILE=/run/fullworth-secrets/banking_api_key \
    Backend__IngestKey_FILE=/run/fullworth-secrets/ingest_key \
    AiAccess__CodexBridgeKey_FILE=/run/fullworth-secrets/codex_bridge_key

# Where a Cloud pack verification key is expected, if one was handed over. A path, not a secret, and
# the only thing this image knows about the Cloud stack - which owns the key and publishes it into its
# own volume. Absent means pack verification fails closed; see the deploy stack's README.
#
# Its own ENV, not appended above: a # inside a line continuation is part of the instruction, not a
# comment, so the whole block would become one long broken variable.
ENV FullWorthCloud__KnowledgePackPublicKeyPath=/run/fullworth-secrets/cloud-public/public.pem

# Where Codex lives in this container. Enabled by default now that it IS this container: the launcher
# does not start Node until somebody needs it, so "enabled" costs a dormant shell rather than a second
# service an operator has to wire up.
#
# 127.0.0.1 is deliberately narrower than the sidecar was. The only caller is the .NET process in this
# same container. A stack that wants to share the bridge - the Cloud's AI review - sets
# CODEX_BRIDGE_BIND=0.0.0.0 in its own file, where the decision is visible.
#
# CODEX_BRIDGE_KEY_PATH points at the tmpfs copy the entrypoint places for the codex user, NOT at
# /run/fullworth-secrets: that directory is 0700 app:app and Codex cannot even list it.
#
# It is deliberately NOT called BRIDGE_KEY_FILE here. SecretBootstrap injects every <NAME>_FILE
# variable it finds in the environment, so the .NET process would try to read a 0400 codex:codex file
# and die at startup - measured, in a real container. The launcher exports the _FILE name for the
# bridge, which is the only process that has any business with it.
ENV AiAccess__CodexBridgeEnabled=true \
    CODEX_BRIDGE_PORT=8099 \
    CODEX_BRIDGE_BIND=127.0.0.1 \
    CODEX_HOME=/data/codex \
    CODEX_WORKDIR=/tmp/codex-work \
    CODEX_ARM_DIR=/tmp/fullworth-codex \
    CODEX_BRIDGE_KEY_PATH=/tmp/fullworth-codex/bridge_key

EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=3s --start-period=40s --retries=5 \
  CMD curl -fsS http://localhost:8080/health || exit 1

ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
CMD ["dotnet","FullWorth.Web.dll"]
