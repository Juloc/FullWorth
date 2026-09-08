FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files first so Docker can cache restore independently from source changes.
COPY src/FullWorth.Web/FullWorth.Web.csproj src/FullWorth.Web/
COPY src/FullWorth.Backend/FullWorth.Backend.csproj src/FullWorth.Backend/
COPY src/FullWorth.Banking/FullWorth.Banking.csproj src/FullWorth.Banking/
COPY src/FullWorth.FinTs/FullWorth.FinTs.csproj src/FullWorth.FinTs/
RUN dotnet restore src/FullWorth.Web/FullWorth.Web.csproj

COPY src/FullWorth.Web/ src/FullWorth.Web/
COPY src/FullWorth.Backend/ src/FullWorth.Backend/
COPY src/FullWorth.Banking/ src/FullWorth.Banking/
COPY src/FullWorth.FinTs/ src/FullWorth.FinTs/
COPY src/Shared/ src/Shared/
RUN dotnet publish src/FullWorth.Web/FullWorth.Web.csproj -c Release -o /app /p:UseAppHost=false

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
