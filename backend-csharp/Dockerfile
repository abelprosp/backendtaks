# syntax=docker/dockerfile:1.7

FROM node:20-bookworm-slim AS node-build
WORKDIR /src/node-backend

COPY package*.json ./
COPY nest-cli.json ./
COPY tsconfig*.json ./
COPY prisma ./prisma
RUN npm ci

COPY src ./src
COPY api ./api
COPY supabase ./supabase
RUN npm run build && npm prune --omit=dev

FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS dotnet-build
WORKDIR /src

COPY backend-csharp/LuxusDemandas.Host.csproj backend-csharp/
RUN dotnet restore backend-csharp/LuxusDemandas.Host.csproj

COPY backend-csharp/ backend-csharp/
RUN dotnet publish backend-csharp/LuxusDemandas.Host.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS final
WORKDIR /app

COPY --from=node-build /usr/local/ /usr/local/
COPY --from=dotnet-build /app/publish ./csharp-host/
COPY --from=node-build /src/node-backend/package*.json ./node-backend/
COPY --from=node-build /src/node-backend/node_modules ./node-backend/node_modules
COPY --from=node-build /src/node-backend/dist ./node-backend/dist

ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV PORT=8080
ENV NODE_BACKEND_PORT=5000
ENV NODE_BACKEND_PATH=/app/node-backend

EXPOSE 8080

ENTRYPOINT ["dotnet", "/app/csharp-host/LuxusDemandas.Host.dll"]
