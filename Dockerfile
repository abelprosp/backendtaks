# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
WORKDIR /src

COPY backend-csharp-api/LuxusDemandas.Api.csproj backend-csharp-api/
RUN dotnet restore backend-csharp-api/LuxusDemandas.Api.csproj

COPY backend-csharp-api/ backend-csharp-api/
RUN dotnet publish backend-csharp-api/LuxusDemandas.Api.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS final
WORKDIR /app

COPY --from=build /app/publish ./

ENV ASPNETCORE_HTTP_PORTS=8080

EXPOSE 8080

# Railway and Render inject PORT at runtime. The fallback keeps local Docker
# execution predictable when no platform-specific port was provided.
ENTRYPOINT ["sh", "-c", "exec dotnet LuxusDemandas.Api.dll --urls http://0.0.0.0:${PORT:-8080}"]
