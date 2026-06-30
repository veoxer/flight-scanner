# Flight Scanner

Self-hosted ASP.NET Core/Blazor PWA for searching flight fares with SerpApi Google Flights and creating price alerts by email, WhatsApp, and browser push notifications.

The app is built to be safe for a public GitHub repository. Database URLs, API keys, SMTP credentials, WhatsApp settings, VAPID keys, and other secrets should come from environment variables or from the admin UI.

## What The App Includes

- First-run setup wizard to create the admin user.
- ASP.NET Core Identity users, roles, passkeys, lockout, forgot-password, email confirmation, and admin user management.
- Search by airport, city, country, or continent.
- One-way and round-trip searches.
- Specific dates and flexible dates.
- Filters for travelers, cabin, bags, direct flights, stops, outbound time, return time, and currency.
- SerpApi Google Flights support with multiple API keys and key rotation.
- Dummy flight data mode for testing without burning SerpApi quota.
- Price alerts with min fare and/or max fare filters.
- Background alert scanner with configurable scan interval.
- Admin-managed SMTP, WhatsApp, Web Push/VAPID, SerpApi, and alert limits.
- PWA manifest, service worker, installable icons, and screenshots.
- PostgreSQL storage.
- Optional script to translate location names to French and Arabic.

## Project Paths

- App: `FlightScanner/`
- Docker Compose stack: `FlightScanner/docker-compose.yml`
- Example env file: `FlightScanner/.env.example`
- nginx example: `FlightScanner/deploy/nginx.flight.veoxer.com.conf`
- Location translation script: `FlightScanner/scripts/translate_locations.py`

## Requirements

Required:

- PostgreSQL.
- A SerpApi account and API key for live Google Flights data.

Optional:

- nginx and TLS for public access through a domain such as `flight.veoxer.com`.
- SMTP account for email alerts, forgot password, and email confirmation.
- WhatsApp HTTP API endpoint.
- VAPID keys for browser push notifications.
- Docker/Portainer for container deployment.

## PostgreSQL Setup

If PostgreSQL already runs in Portainer on the same host, keep using that container. The app only needs network access to it.

Open `psql` as a PostgreSQL admin user and create the app database:

```sql
CREATE USER flightscanner WITH PASSWORD 'PUT_A_LONG_RANDOM_PASSWORD_HERE';
CREATE DATABASE flightscanner OWNER flightscanner;
\c flightscanner
GRANT ALL ON SCHEMA public TO flightscanner;
```

If the database name contains a dash, quote it:

```sql
CREATE DATABASE "veoxer-live-db" OWNER flightscanner;
\c "veoxer-live-db"
GRANT ALL ON SCHEMA public TO flightscanner;
```

If PostgreSQL reports a collation version mismatch, connect to the affected database and refresh it:

```sql
\c "your_existing_db"
REINDEX DATABASE "your_existing_db";
ALTER DATABASE "your_existing_db" REFRESH COLLATION VERSION;
```

If the problem is `template1`, connect to it and run the same maintenance:

```sql
\c template1
REINDEX DATABASE template1;
ALTER DATABASE template1 REFRESH COLLATION VERSION;
```

## Docker Network For Portainer

The compose file expects an external Docker network named `veoxer_internal` by default.

Create it once on the host:

```bash
docker network create veoxer_internal
```

Then make sure both containers are attached to that network:

- PostgreSQL container.
- Flight Scanner container.

If your network has another name, set:

```text
DOCKER_NETWORK=your_network_name
```

## Portainer Stack Deployment

1. Push this repository to GitHub.
2. Open Portainer.
3. Go to `Stacks`.
4. Click `Add stack`.
5. Choose `Repository`.
6. Select your GitHub repository.
7. Set the compose path to:

```text
FlightScanner/docker-compose.yml
```

8. Add the environment variables shown below.
9. Deploy the stack.
10. Open the local app URL:

```text
http://192.168.11.120:18080
```

Replace `192.168.11.120` with your host IP.

## Minimum Portainer Environment Variables

Use this when PostgreSQL is reachable by container name on the same Docker network:

```env
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://+:8080
AllowedHosts=flight.veoxer.com;192.168.11.120;localhost

DOCKER_NETWORK=veoxer_internal

POSTGRES_HOST=postgres
POSTGRES_PORT=5432
POSTGRES_DB=flightscanner
POSTGRES_USER=flightscanner
POSTGRES_PASSWORD=PUT_A_LONG_RANDOM_PASSWORD_HERE
POSTGRES_SSL_MODE=Disable

FLIGHTSCANNER_DATA_PROTECTION_KEYS_PATH=/var/lib/flightscanner/keys
FLIGHTSCANNER_USE_DUMMY_FLIGHT_DATA=false
```

If PostgreSQL is exposed on the host instead, use the host IP:

```env
POSTGRES_HOST=192.168.11.120
POSTGRES_PORT=5432
POSTGRES_DB=flightscanner
POSTGRES_USER=flightscanner
POSTGRES_PASSWORD=PUT_A_LONG_RANDOM_PASSWORD_HERE
POSTGRES_SSL_MODE=Disable
```

You can also provide a full connection string:

```env
FLIGHTSCANNER_DATABASE_URL=Host=192.168.11.120;Port=5432;Database=flightscanner;Username=flightscanner;Password=PUT_A_LONG_RANDOM_PASSWORD_HERE;Ssl Mode=Disable
```

## Optional Environment Variables

Live flight data:

```env
SERPAPI_API_KEY=
SERPAPI_API_KEYS=
```

`SERPAPI_API_KEYS` can contain multiple keys separated by commas, semicolons, or new lines. The app rotates through them and falls back to another key if one fails.

SMTP defaults:

```env
SMTP_HOST=
SMTP_PORT=587
SMTP_USERNAME=
SMTP_PASSWORD=
SMTP_FROM_EMAIL=
SMTP_FROM_NAME=Flight Scanner
SMTP_USE_TLS=true
```

WhatsApp defaults:

```env
WHATSAPP_ENABLED=false
WHATSAPP_URL=
WHATSAPP_HTTP_METHOD=POST
WHATSAPP_HEADERS=
WHATSAPP_BODY_TEMPLATE=
WHATSAPP_TO=
```

Browser push defaults:

```env
VAPID_PUBLIC_KEY=
VAPID_PRIVATE_KEY=
VAPID_SUBJECT=mailto:noreply@example.com
```

Location import:

```env
LOCATION_DATA_IMPORT_ENABLED=false
LOCATION_IDENTIFIER_IMPORT_ENABLED=false
```

Testing without SerpApi:

```env
FLIGHTSCANNER_USE_DUMMY_FLIGHT_DATA=true
```

## Ports And Health Check

The container listens on port `8080`.

The compose file publishes it on host port `18080`:

```yaml
ports:
  - "18080:8080"
```

Health check:

```text
http://192.168.11.120:18080/health
```

If Portainer says port `8080` is already allocated, the host port is conflicting. Change the left side only:

```yaml
ports:
  - "18081:8080"
```

Then open:

```text
http://192.168.11.120:18081
```

## nginx And Public Domain

The app can run locally and still be exposed publicly through nginx.

Point your DNS record for `flight.veoxer.com` to the public IP of your host, then proxy nginx to the local container port.

Example target:

```text
flight.veoxer.com -> http://127.0.0.1:18080
```

Use the included example:

```bash
sudo cp FlightScanner/deploy/nginx.flight.veoxer.com.conf /etc/nginx/sites-available/flight.veoxer.com.conf
sudo ln -s /etc/nginx/sites-available/flight.veoxer.com.conf /etc/nginx/sites-enabled/flight.veoxer.com.conf
sudo nginx -t
sudo systemctl reload nginx
```

Enable HTTPS:

```bash
sudo apt install -y certbot python3-certbot-nginx
sudo certbot --nginx -d flight.veoxer.com
```

For production, open the app through:

```text
https://flight.veoxer.com
```

## First Startup

1. Open the app.
2. The first page should ask you to create the admin user.
3. Create the admin account.
4. Sign in.
5. Go through the admin pages and configure integrations.

Important admin pages:

- `Admin > Flight API`: enable SerpApi, add one or more API keys, configure alert scan interval.
- `Admin > Reminders`: enable email, WhatsApp, and/or browser push alert channels.
- `Admin > Management`: configure SMTP for account emails and configure max alert limits.
- `Admin > Users`: manage users.

The default alert scan interval is 3 hours unless changed in the admin UI.

## SerpApi Behavior

- Airport searches use IATA airport codes.
- City and country searches use cached Freebase/KGMID identifiers.
- The app should not expand a city or country into many airport-to-airport searches.
- Round-trip search prices returned by the first SerpApi call are treated as the full round-trip price.
- Return flight details are loaded only when a user opens the selected departure flight details.
- Alerts compare against the fare returned by the search result.

Use dummy mode while testing UI and alerts:

```env
FLIGHTSCANNER_USE_DUMMY_FLIGHT_DATA=true
```

Turn dummy mode off when you want real SerpApi calls:

```env
FLIGHTSCANNER_USE_DUMMY_FLIGHT_DATA=false
```

## Browser Push Notifications

Push notifications need VAPID keys and HTTPS.

Generate VAPID keys:

```bash
npm install -g web-push
web-push generate-vapid-keys
```

Put the public and private keys in `Admin > Reminders`, or provide them through environment variables:

```env
VAPID_PUBLIC_KEY=your_public_key
VAPID_PRIVATE_KEY=your_private_key
VAPID_SUBJECT=mailto:noreply@your-domain.com
```

Browser push works on `localhost` for development, but mobile devices normally require HTTPS.

On Samsung/Android, install behavior depends on the browser:

- Chrome may show `Add to Home screen` if install criteria are not fully satisfied.
- Samsung Internet can install PWAs, but it can hang if the browser has stale site data.
- Clear site data, reopen the HTTPS domain, then try install again.

## Gmail SMTP

If your Gmail account has 2FA enabled, do not use your normal Gmail password.

Create a Google App Password and use that as `SMTP_PASSWORD`.

Typical Gmail settings:

```env
SMTP_HOST=smtp.gmail.com
SMTP_PORT=587
SMTP_USERNAME=your.email@gmail.com
SMTP_PASSWORD=your_google_app_password
SMTP_FROM_EMAIL=your.email@gmail.com
SMTP_FROM_NAME=Flight Scanner
SMTP_USE_TLS=true
```

## Local Development With Visual Studio

Install the .NET 10 SDK.

Create or update `FlightScanner/appsettings.Development.json` with local values. Use placeholders for secrets if you commit the file.

Example:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=192.168.11.120;Port=5432;Database=flightscanner;Username=flightscanner;Password=YOUR_PASSWORD;Ssl Mode=Disable"
  },
  "AllowedHosts": "flight.veoxer.com;192.168.11.120;localhost",
  "FlightScanner": {
    "UseDummyFlightData": true
  },
  "LOCATION_DATA_IMPORT_ENABLED": false,
  "LOCATION_IDENTIFIER_IMPORT_ENABLED": false
}
```

Run from Visual Studio, or from a terminal:

```powershell
cd "C:\Users\Gamer\OneDrive\Documents\Flights Lookup\FlightScanner"
dotnet restore --configfile NuGet.Config
dotnet run --launch-profile https
```

Then open the URL printed by `dotnet run`, usually:

```text
https://localhost:7272
```

## PWA Checklist

For best install behavior:

- Serve the app over HTTPS.
- Make sure the browser can fetch `/manifest.webmanifest`.
- Make sure the service worker loads.
- Make sure icons and screenshots are reachable.
- Use VAPID keys if you want mobile push notifications.

Maskable icons are used by Android and some PWA launchers so the installed app icon can be cropped into platform-specific shapes without looking broken.

## Location Translations

The app can show location names in English, French, and Arabic. The translation script fills missing French and Arabic columns in the database.

The script is safe to rerun. It updates missing values, keeps a local `translation-cache.json`, and should not insert duplicate location rows.

### Run On Windows

```powershell
cd "C:\Users\Gamer\OneDrive\Documents\Flights Lookup"
pip install "psycopg[binary]"
$env:FLIGHTSCANNER_DATABASE_URL = "Host=192.168.11.120;Port=5432;Database=flightscanner;Username=flightscanner;Password=YOUR_PASSWORD;Ssl Mode=Disable"
python .\FlightScanner\scripts\translate_locations.py --dry-run --limit 10
python .\FlightScanner\scripts\translate_locations.py --pause 0.2 --retries 8 --batch-size 50
```

### Run On The Raspberry Pi

Copy the script to the Pi:

```powershell
scp "C:\Users\Gamer\OneDrive\Documents\Flights Lookup\FlightScanner\scripts\translate_locations.py" pi@192.168.11.120:/home/pi/flightscanner-tools/
```

SSH into the Pi:

```bash
ssh pi@192.168.11.120
sudo apt update
sudo apt install -y python3 python3-pip python3-venv tmux
mkdir -p ~/flightscanner-tools
cd ~/flightscanner-tools
python3 -m venv .venv
source .venv/bin/activate
pip install "psycopg[binary]"
export FLIGHTSCANNER_DATABASE_URL='Host=127.0.0.1;Port=5432;Database=flightscanner;Username=flightscanner;Password=YOUR_PASSWORD;Ssl Mode=Disable'
python translate_locations.py --dry-run --limit 10
```

For a long run, use `tmux`:

```bash
tmux new -s translate-locations
cd ~/flightscanner-tools
source .venv/bin/activate
export FLIGHTSCANNER_DATABASE_URL='Host=127.0.0.1;Port=5432;Database=flightscanner;Username=flightscanner;Password=YOUR_PASSWORD;Ssl Mode=Disable'
python translate_locations.py --pause 0.2 --retries 8 --batch-size 50
```

Detach with `Ctrl+b`, then `d`.

Reattach later:

```bash
tmux attach -t translate-locations
```

Recommended batch size is `25` to `50`. Very large batches can cause Google Translate failures or malformed responses.

## Public GitHub Safety Checklist

Before pushing to a public repository:

- Do not commit `.env`.
- Do not commit real passwords in `appsettings.Development.json`.
- Do not commit SerpApi keys.
- Do not commit SMTP passwords.
- Do not commit WhatsApp API secrets.
- Do not commit VAPID private keys.
- Keep `.vs/` ignored.
- Keep `*.csproj.user` ignored.

## Troubleshooting

### Bad Request - Invalid Hostname

Add the host to `AllowedHosts`:

```env
AllowedHosts=flight.veoxer.com;192.168.11.120;localhost
```

### Port Is Already Allocated

Change the host port in `docker-compose.yml`:

```yaml
ports:
  - "18081:8080"
```

### Container Is Unhealthy

Check:

- `/health`.
- PostgreSQL host, port, database, username, and password.
- Docker network name.
- Whether PostgreSQL and the app are on the same Docker network.
- Portainer logs.

### No Live Flight Results

Check:

- `FLIGHTSCANNER_USE_DUMMY_FLIGHT_DATA=false`.
- SerpApi is enabled in `Admin > Flight API`.
- At least one API key is configured.
- SerpApi quota is not exhausted.

### Push Notifications Say VAPID Is Missing

Add VAPID keys in `Admin > Reminders`, then reload the alerts page and activate notifications again.

### Forgot Password Or Confirmation Email Does Not Arrive

Check:

- SMTP settings in `Admin > Management`.
- SMTP host, port, TLS, username, and password.
- Gmail App Password if using Gmail with 2FA.
- Spam folder.

### Translation Script Fails With Google Errors

Rerun it with conservative settings:

```bash
python translate_locations.py --pause 0.2 --retries 8 --batch-size 50
```

The cache and already saved rows are preserved.

### Translation Value Too Long

Use the latest script. It widens translation columns before updating rows.

