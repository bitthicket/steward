# syntax=docker/dockerfile:1

# --- portal build stage --------------------------------------------------
FROM node:22-alpine AS portal-build
WORKDIR /portal

# Cache dependency install layer.
COPY portal/package.json portal/package-lock.json ./
RUN npm ci

# Build the portal.
COPY portal/ ./
RUN npm run build

# --- .NET build stage ----------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Restore as a separate layer for better caching.
COPY src/BitThicket.Steward.Api/BitThicket.Steward.Api.fsproj src/BitThicket.Steward.Api/
RUN dotnet restore src/BitThicket.Steward.Api/BitThicket.Steward.Api.fsproj

# Publish the API.
COPY src/ src/
RUN dotnet publish src/BitThicket.Steward.Api/BitThicket.Steward.Api.fsproj \
        --configuration Release \
        --no-restore \
        --output /app/publish

# --- runtime stage -------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
COPY --from=portal-build /portal/build ./wwwroot/portal

# Northflank injects $PORT; default keeps the container runnable standalone.
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_NOLOGO=1 \
    PORT=8080

EXPOSE 8080

ENTRYPOINT ["dotnet", "BitThicket.Steward.Api.dll"]
