# syntax=docker/dockerfile:1

# --- build stage ---------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Restore as a separate layer for better caching.
COPY src/BitThicket.Steward.Ingestion.Akoya/BitThicket.Steward.Ingestion.Akoya.fsproj \
     src/BitThicket.Steward.Ingestion.Akoya/
RUN dotnet restore src/BitThicket.Steward.Ingestion.Akoya/BitThicket.Steward.Ingestion.Akoya.fsproj

# Publish the ingestion service.
COPY src/BitThicket.Steward.Ingestion.Akoya/ src/BitThicket.Steward.Ingestion.Akoya/
RUN dotnet publish src/BitThicket.Steward.Ingestion.Akoya/BitThicket.Steward.Ingestion.Akoya.fsproj \
        --configuration Release \
        --no-restore \
        --output /app/publish

# --- runtime stage -------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

# Northflank injects $PORT; default keeps the container runnable standalone.
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_NOLOGO=1 \
    PORT=8080

EXPOSE 8080

ENTRYPOINT ["dotnet", "BitThicket.Steward.Ingestion.Akoya.dll"]
