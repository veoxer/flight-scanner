# Flight Scanner

Self-hosted ASP.NET Core/Blazor PWA for searching SerpApi Google Flights fares and creating price alerts.

## What is included

- First-run setup wizard at `/setup` that creates the admin account.
- ASP.NET Core Identity users, roles, passkeys, lockout, and admin user management.
- Flight search by airport, city, country, or continent with one-way/round-trip mode and common filters: passengers, dates, cabin, bags, direct-only, and stops.
- Price alerts with a background scanner.
- Notification pipeline for browser push subscription records, SMTP email, and a configurable WhatsApp HTTP API.
- Admin reminder settings for delivery channels, SMTP, WhatsApp, and Web Push VAPID keys.
- Admin integration settings for SerpApi Google Flights.
- PWA manifest and service worker.
- PostgreSQL storage configured from environment variables.

## Configuration

Copy `.env.example` to `.env` for local Docker/Portainer use and fill in the values.

Database configuration supports either one full connection string:

```text
FLIGHTSCANNER_DATABASE_URL=Host=host.docker.internal;Port=5432;Database=flightscanner;Username=flightscanner;Password=change-me;Pooling=true
```

Or split variables:

```text
POSTGRES_HOST=host.docker.internal
POSTGRES_PORT=5432
POSTGRES_DB=flightscanner
POSTGRES_USER=flightscanner
POSTGRES_PASSWORD=change-me
POSTGRES_SSL_MODE=Disable
```

All deployment-sensitive values should be supplied through environment variables or Portainer secrets/env management. Do not commit `.env`.

Email, WhatsApp, mobile notification settings, the SerpApi key, and the alert scan interval are configured in the app after setup from the admin pages.

Price alerts are scanned every 3 hours by default. Change the cadence from `Admin > Flight API`; the allowed range is 15 minutes to 7 days.

## Run Locally

```powershell
dotnet restore --configfile NuGet.Config
$env:POSTGRES_HOST="localhost"
$env:POSTGRES_PORT="5432"
$env:POSTGRES_DB="flightscanner"
$env:POSTGRES_USER="flightscanner"
$env:POSTGRES_PASSWORD="change-me"
dotnet run --project FlightScanner.csproj --launch-profile http
```

Open `http://localhost:5043`, complete setup, then use the admin account to configure integrations.

## Docker / Portainer

Build and run with:

```powershell
docker compose up -d --build
```

For Portainer, create a stack from `docker-compose.yml`, add environment values from `.env.example`, and keep the `flightscanner_keys` volume. That volume persists ASP.NET Core data-protection keys, so auth cookies survive container restarts.

The container listens on port `8080` internally and is bound to `18080` on the host. That makes it reachable on your LAN, for example `http://192.168.11.120:18080`, while nginx can still proxy `flight.veoxer.com` to `http://127.0.0.1:18080`. A starter nginx server block is in `deploy/nginx.flight.veoxer.com.conf`.

For LAN access, `AllowedHosts` must include the LAN IP too:

```text
AllowedHosts=flight.veoxer.com;192.168.11.120;localhost
```

## Flight Data

The app ships with SerpApi Google Flights support. Create a SerpApi account, then enter the API key under `Admin > Flight API`.

SerpApi free plans have a monthly search quota. Airport searches use IATA codes; city, country, and continent searches use cached Wikidata Freebase IDs as Google Flights location KGMIDs. The app sends one SerpApi Google Flights request per search and does not expand cities or countries into multiple airport-pair searches.

Wikidata country, continent, and matching city Freebase IDs are imported once at startup when `LOCATION_IDENTIFIER_IMPORT_ENABLED=true`. Use `LOCATION_IDENTIFIER_IMPORT_REFRESH=true` to refresh the cache. City import is paged with `LOCATION_IDENTIFIER_IMPORT_CITY_BATCH_SIZE` and `LOCATION_IDENTIFIER_IMPORT_MAX_CITY_PAGES`.

## Notes

True background mobile push requires VAPID keys and a Web Push sender implementation. The app currently stores browser push subscriptions and supports local/browser notification permission flow, while email and WhatsApp dispatch are implemented server-side.
