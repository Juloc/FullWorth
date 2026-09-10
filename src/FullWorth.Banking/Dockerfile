FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Without this the published assemblies carry the 0.0.0-dev default from Directory.Build.props, and
# every instance reports that as its client version. The release workflow passes the real tag.
ARG FULLWORTH_VERSION=0.0.0-dev

# Directory.Build.props sets the version default and has to be inside the build context copy, or
# MSBuild finds no props file at all when it walks up from the project directory.
COPY Directory.Build.props .
COPY src/FullWorth.Banking/FullWorth.Banking.csproj src/FullWorth.Banking/
COPY src/FullWorth.FinTs/FullWorth.FinTs.csproj src/FullWorth.FinTs/
RUN dotnet restore src/FullWorth.Banking/FullWorth.Banking.csproj
COPY src/FullWorth.Banking/ src/FullWorth.Banking/
COPY src/FullWorth.FinTs/ src/FullWorth.FinTs/
COPY src/Shared/ src/Shared/
RUN dotnet publish src/FullWorth.Banking/FullWorth.Banking.csproj -c Release -p:Version="$FULLWORTH_VERSION" -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# curl: container HEALTHCHECK; gosu: drop to the non-root app user after staging the key secret.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl gosu \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
COPY ops/docker/entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=3s --start-period=40s --retries=5 \
  CMD curl -fsS http://localhost:8080/health || exit 1
# Entrypoint stages the Enable Banking key readable by app, then execs the app as non-root "app".
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
CMD ["dotnet","FullWorth.Banking.dll"]
