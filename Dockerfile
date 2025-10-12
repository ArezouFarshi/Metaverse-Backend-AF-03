# -------- Base Image with ASP.NET runtime (not just plain .NET) --------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 10000

# -------- Build Stage --------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project file and restore dependencies
COPY *.csproj ./
RUN dotnet restore

# Copy all source and publish
COPY . ./
RUN dotnet publish Metaverse-Backend-AF-02.csproj -c Release -o /app/publish

# -------- Final Image --------
FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Metaverse-Backend-AF-02.dll"]
