# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src

# Copy solution and project files first for layer caching
COPY steward.slnx .
COPY src/BitThicket.Steward.Ingestion.Akoya/BitThicket.Steward.Ingestion.Akoya.fsproj src/BitThicket.Steward.Ingestion.Akoya/
RUN dotnet restore src/BitThicket.Steward.Ingestion.Akoya/BitThicket.Steward.Ingestion.Akoya.fsproj

# Copy source and build
COPY src/BitThicket.Steward.Ingestion.Akoya/ src/BitThicket.Steward.Ingestion.Akoya/
RUN dotnet publish src/BitThicket.Steward.Ingestion.Akoya/BitThicket.Steward.Ingestion.Akoya.fsproj \
    -c Release -o /app/publish --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "BitThicket.Steward.Ingestion.Akoya.dll"]
