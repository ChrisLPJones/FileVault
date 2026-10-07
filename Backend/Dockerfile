# FileVault API
# Build:  docker build -t filevault-api ./Backend
# Config comes from environment variables (see the root docker-compose.yml).

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first so the package layer is cached between code changes
COPY Backend.csproj ./
RUN dotnet restore Backend.csproj

COPY . ./
RUN dotnet publish Backend.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# Encrypted file storage lives on a volume owned by the non-root app user
RUN mkdir -p /data/storage && chown "$APP_UID" /data/storage

ENV ASPNETCORE_HTTP_PORTS=8080 \
    StorageRoot=/data/storage

USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Backend.dll"]
