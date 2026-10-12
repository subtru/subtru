# ---------- Build stage ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

# Copy project files first (for layer caching)
COPY Directory.Build.props ./
COPY src/SubTru.Api/SubTru.Api.csproj SubTru.Api/
COPY src/SubTru.Domain/SubTru.Domain.csproj SubTru.Domain/
COPY src/SubTru.Infrastructure/SubTru.Infrastructure.csproj SubTru.Infrastructure/

# Restore dependencies
RUN dotnet restore SubTru.Api/SubTru.Api.csproj

# Copy the rest of the source
COPY src/. .

# Publish API
WORKDIR /src/SubTru.Api
RUN dotnet publish -c Release -o /app/publish

# ---------- Runtime stage ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://0.0.0.0:3000
EXPOSE 3000

COPY --from=build /app/publish .
COPY --from=build /src/SubTru.Api/certs/apple /app/certs/apple
COPY LICENSE /app/LICENSE

ENV AppleVerification__RootCertificateDirectory=/app/certs/apple

USER $APP_UID

ENTRYPOINT ["dotnet", "SubTru.Api.dll"]
