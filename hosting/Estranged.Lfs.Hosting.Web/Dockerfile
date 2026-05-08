# Multi-stage build for Estranged.Lfs.Hosting.Web. Final image is the
# Microsoft-supplied ASP.NET 9 runtime; the build stage uses the .NET 9
# SDK and publishes the host project plus its referenced csproj's.
#
# Build context expected at the repo root (the .sln dir). Build with:
#   docker build -f hosting/Estranged.Lfs.Hosting.Web/Dockerfile -t harbor.appmana.com/appmana-shared/estranged-lfs:<tag> .
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore hosting/Estranged.Lfs.Hosting.Web/Estranged.Lfs.Hosting.Web.csproj
RUN dotnet publish hosting/Estranged.Lfs.Hosting.Web/Estranged.Lfs.Hosting.Web.csproj \
    -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER 1001
ENTRYPOINT ["dotnet", "Estranged.Lfs.Hosting.Web.dll"]
