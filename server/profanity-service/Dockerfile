# ProfanityService — build from the repo root (context: .).
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY src/ProfanityService/ProfanityService.csproj src/ProfanityService/
RUN dotnet restore src/ProfanityService/ProfanityService.csproj
COPY src/ProfanityService/ src/ProfanityService/
RUN dotnet publish src/ProfanityService/ProfanityService.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app .
EXPOSE 4006
CMD ["dotnet", "ProfanityService.dll"]
