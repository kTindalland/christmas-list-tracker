# --- Stage 1: Build Stage ---
FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src

# Copy solution and package configuration files first for optimal NuGet layer caching
COPY ["ChristmasListTracker.slnx", "./"]
COPY ["Directory.Packages.props", "./"]
COPY ["src/ChristmasListTracker/ChristmasListTracker.csproj", "src/ChristmasListTracker/"]
COPY ["src/ChristmasListTracker.Client/ChristmasListTracker.Client.csproj", "src/ChristmasListTracker.Client/"]

# Restore dependencies for the solution
RUN dotnet restore "ChristmasListTracker.slnx"

# Copy the remaining source code
COPY . .

# Build and publish the Server project (which bundles the Client WebAssembly assets)
WORKDIR "/src/src/ChristmasListTracker"
RUN dotnet publish "ChristmasListTracker.csproj" -c Release -o /app/publish /p:UseAppHost=false

# --- Stage 2: Runtime Stage ---
FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS final
WORKDIR /app

# Render expects containers to listen on port 8080 by default
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# Copy published application artifacts from build stage
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "ChristmasListTracker.dll"]
