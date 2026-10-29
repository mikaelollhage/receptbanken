# Stage 1: Build & Publish (.NET 10 LTS)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files for layer-cached restore
COPY Directory.Build.props ./
COPY Receptbanken.sln ./
COPY src/Receptbanken.Core/Receptbanken.Core.csproj src/Receptbanken.Core/
COPY src/Receptbanken.Infrastructure/Receptbanken.Infrastructure.csproj src/Receptbanken.Infrastructure/
COPY src/Receptbanken.Web/Receptbanken.Web.csproj src/Receptbanken.Web/

RUN dotnet restore src/Receptbanken.Web/Receptbanken.Web.csproj

# Copy source code, publish, and verify critical static web assets exist
COPY src/ src/
RUN dotnet publish src/Receptbanken.Web/Receptbanken.Web.csproj -c Release -o /app/publish /p:UseAppHost=false && \
    test -f /app/publish/wwwroot/_framework/blazor.web.js && \
    test -f /app/publish/wwwroot/Receptbanken.Web.styles.css && \
    test -f /app/publish/Receptbanken.Web.staticwebassets.endpoints.json

# Stage 2: Runtime (.NET 10 ASP.NET Core Linux container)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Create persistent data directories with non-root permissions
RUN mkdir -p /app/data/images/recipes /app/data/keys && chown -R app:app /app/data

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV Storage__DataPath=/app/data

EXPOSE 8080
VOLUME ["/app/data"]

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD ["dotnet", "Receptbanken.Web.dll", "--healthcheck"]

USER app
ENTRYPOINT ["dotnet", "Receptbanken.Web.dll"]

