# Build from the repository root so Directory.Packages.props and the shared
# BuildingBlocks projects are inside the context.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy the manifests first: this layer is cached until a project file or a
# package version actually changes, so a code edit does not re-run restore.
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/Services/Identity/Identity.Api/Identity.Api.csproj src/Services/Identity/Identity.Api/
RUN dotnet restore src/Services/Identity/Identity.Api/Identity.Api.csproj

COPY src/ src/
RUN dotnet publish src/Services/Identity/Identity.Api/Identity.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Runs unprivileged: the image ships a non-root user for exactly this.
USER $APP_UID

COPY --from=build /app .
ENTRYPOINT ["dotnet", "Identity.Api.dll"]
