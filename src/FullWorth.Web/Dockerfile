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
RUN dotnet publish src/FullWorth.Web/FullWorth.Web.csproj -c Release -p:Version="$FULLWORTH_VERSION" -o /app --no-restore /p:UseAppHost=false

# The unified image includes the backend's browser/OCR runtime so Amazon, receipt and PDF features
# keep working after Web + Backend + Banking move into one process.
FROM mcr.microsoft.com/playwright/dotnet:v1.62.0-noble
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl gosu tesseract-ocr tesseract-ocr-deu poppler-utils \
 && (getent group app >/dev/null 2>&1 || groupadd --system app) \
 && (id -u app >/dev/null 2>&1 || useradd --system --gid app --create-home app) \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app .
COPY ops/docker/entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh

ENV ASPNETCORE_URLS=http://+:8080 \
    PLAYWRIGHT_BROWSERS_PATH=/ms-playwright

EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=3s --start-period=40s --retries=5 \
  CMD curl -fsS http://localhost:8080/health || exit 1

ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
CMD ["dotnet","FullWorth.Web.dll"]
