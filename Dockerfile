# -------- Base Image with ASP.NET runtime --------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
# Render will set PORT; exposing 10000 for local runs
EXPOSE 10000

# -------- Build Stage --------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj and restore
COPY *.csproj ./
RUN dotnet restore

# Copy source and publish
COPY . ./
RUN dotnet publish Metaverse-Backend-AF-02.csproj -c Release -o /app/publish

# -------- Final Image --------
FROM base AS final
WORKDIR /app
COPY --from=build /app/publish ./

# Kestrel will bind to PORT from env; default handled in Program.cs
ENTRYPOINT ["dotnet", "Metaverse-Backend-AF-02.dll"]
