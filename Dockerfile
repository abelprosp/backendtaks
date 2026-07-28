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

ENV ASPNETCORE_URLS=http://0.0.0.0:10000
ENV PORT=10000

EXPOSE 10000

ENTRYPOINT ["dotnet", "LuxusDemandas.Api.dll"]
