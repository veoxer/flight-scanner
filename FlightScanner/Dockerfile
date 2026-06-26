# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY NuGet.Config ./
COPY FlightScanner.csproj ./
RUN dotnet restore --configfile NuGet.Config

COPY . ./
RUN dotnet publish FlightScanner.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /var/lib/flightscanner/keys
COPY --from=build /app/publish ./

ENTRYPOINT ["dotnet", "FlightScanner.dll"]
