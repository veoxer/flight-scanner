#!/usr/bin/env python3
"""
Translate FlightLocations display fields into French and Arabic.

The script fills only missing translated columns:
  NameFr, NameAr, CountryNameFr, CountryNameAr, ContinentFr, ContinentAr

It reads the PostgreSQL connection from FLIGHTSCANNER_DATABASE_URL, DATABASE_URL,
POSTGRES_URL, or PG* environment variables. Install a PostgreSQL driver first:

  pip install psycopg[binary]

Example:

  python FlightScanner/scripts/translate_locations.py --dry-run
  python FlightScanner/scripts/translate_locations.py --limit 500
"""

from __future__ import annotations

import argparse
import json
import os
import time
import urllib.parse
import urllib.request
from dataclasses import dataclass

try:
    import psycopg
    from psycopg.conninfo import make_conninfo
except ImportError as exc:
    raise SystemExit(
        "Missing dependency: install it with `pip install psycopg[binary]`."
    ) from exc


GOOGLE_TRANSLATE_URL = "https://translate.googleapis.com/translate_a/single"


@dataclass(frozen=True)
class LocationRow:
    id: int
    name: str
    country_name: str | None
    continent: str
    name_fr: str | None
    name_ar: str | None
    country_name_fr: str | None
    country_name_ar: str | None
    continent_fr: str | None
    continent_ar: str | None


def connection_string() -> str:
    for key in ("FLIGHTSCANNER_DATABASE_URL", "DATABASE_URL", "POSTGRES_URL"):
        value = os.getenv(key)
        if value:
            return normalize_connection_string(value)

    host = os.getenv("POSTGRES_HOST") or os.getenv("PGHOST")
    db = os.getenv("POSTGRES_DB") or os.getenv("PGDATABASE")
    user = os.getenv("POSTGRES_USER") or os.getenv("PGUSER")
    password = os.getenv("POSTGRES_PASSWORD") or os.getenv("PGPASSWORD")
    port = os.getenv("POSTGRES_PORT") or os.getenv("PGPORT") or "5432"
    if not all([host, db, user, password]):
        raise SystemExit(
            "Set FLIGHTSCANNER_DATABASE_URL or POSTGRES_HOST/POSTGRES_DB/"
            "POSTGRES_USER/POSTGRES_PASSWORD."
        )

    return make_conninfo(host=host, port=port, dbname=db, user=user, password=password)


def normalize_connection_string(value: str) -> str:
    value = value.strip()
    if "://" in value:
        return value

    if ";" not in value:
        return value

    parts: dict[str, str] = {}
    for piece in value.split(";"):
        if not piece.strip() or "=" not in piece:
            continue
        key, raw = piece.split("=", 1)
        normalized_key = key.strip().lower().replace(" ", "")
        parts[normalized_key] = raw.strip()

    mapped = {
        "host": parts.get("host") or parts.get("server"),
        "port": parts.get("port"),
        "dbname": parts.get("database") or parts.get("dbname"),
        "user": parts.get("username") or parts.get("userid") or parts.get("user"),
        "password": parts.get("password"),
    }
    ssl_mode = parts.get("sslmode")
    if ssl_mode:
        mapped["sslmode"] = ssl_mode.lower().replace(" ", "-")

    return make_conninfo(**{key: val for key, val in mapped.items() if val})


def translate(text: str, target: str, pause_seconds: float) -> str:
    query = urllib.parse.urlencode(
        {
            "client": "gtx",
            "sl": "en",
            "tl": target,
            "dt": "t",
            "q": text,
        }
    )
    request = urllib.request.Request(
        f"{GOOGLE_TRANSLATE_URL}?{query}",
        headers={"User-Agent": "FlightScannerLocationTranslator/1.0"},
    )
    with urllib.request.urlopen(request, timeout=30) as response:
        payload = json.loads(response.read().decode("utf-8"))

    translated = "".join(part[0] for part in payload[0] if part and part[0]).strip()
    if pause_seconds > 0:
        time.sleep(pause_seconds)
    return translated or text


def cached_translate(cache: dict[tuple[str, str], str], text: str | None, target: str, pause_seconds: float) -> str | None:
    if not text or not text.strip():
        return None

    normalized = text.strip()
    key = (target, normalized)
    if key not in cache:
        cache[key] = translate(normalized, target, pause_seconds)
    return cache[key]


def load_rows(conn, limit: int | None) -> list[LocationRow]:
    sql = """
        SELECT "Id", "Name", "CountryName", "Continent",
               "NameFr", "NameAr", "CountryNameFr", "CountryNameAr",
               "ContinentFr", "ContinentAr"
        FROM "FlightLocations"
        WHERE "NameFr" IS NULL OR "NameAr" IS NULL
           OR ("CountryName" IS NOT NULL AND ("CountryNameFr" IS NULL OR "CountryNameAr" IS NULL))
           OR "ContinentFr" IS NULL OR "ContinentAr" IS NULL
        ORDER BY "Id"
    """
    if limit:
        sql += " LIMIT %s"
        params = (limit,)
    else:
        params = ()

    with conn.cursor() as cur:
        cur.execute(sql, params)
        return [LocationRow(*row) for row in cur.fetchall()]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--limit", type=int, default=None, help="Maximum rows to process.")
    parser.add_argument("--dry-run", action="store_true", help="Translate but do not update the database.")
    parser.add_argument("--pause", type=float, default=0.08, help="Delay between uncached translate calls.")
    parser.add_argument("--commit-every", type=int, default=100, help="Commit after this many updated rows.")
    args = parser.parse_args()

    cache: dict[tuple[str, str], str] = {}
    updated = 0
    with psycopg.connect(connection_string()) as conn:
        rows = load_rows(conn, args.limit)
        print(f"Rows to inspect: {len(rows)}")

        for row in rows:
            values = {
                "NameFr": row.name_fr or cached_translate(cache, row.name, "fr", args.pause),
                "NameAr": row.name_ar or cached_translate(cache, row.name, "ar", args.pause),
                "CountryNameFr": row.country_name_fr or cached_translate(cache, row.country_name, "fr", args.pause),
                "CountryNameAr": row.country_name_ar or cached_translate(cache, row.country_name, "ar", args.pause),
                "ContinentFr": row.continent_fr or cached_translate(cache, row.continent, "fr", args.pause),
                "ContinentAr": row.continent_ar or cached_translate(cache, row.continent, "ar", args.pause),
            }

            print(f"{row.id}: {row.name} -> fr={values['NameFr']} | ar={values['NameAr']}")
            if args.dry_run:
                continue

            with conn.cursor() as cur:
                cur.execute(
                    """
                    UPDATE "FlightLocations"
                    SET "NameFr" = %s,
                        "NameAr" = %s,
                        "CountryNameFr" = %s,
                        "CountryNameAr" = %s,
                        "ContinentFr" = %s,
                        "ContinentAr" = %s
                    WHERE "Id" = %s
                    """,
                    (
                        values["NameFr"],
                        values["NameAr"],
                        values["CountryNameFr"],
                        values["CountryNameAr"],
                        values["ContinentFr"],
                        values["ContinentAr"],
                        row.id,
                    ),
                )
            updated += 1
            if updated % max(1, args.commit_every) == 0:
                conn.commit()
                print(f"Committed {updated} rows...")

        if args.dry_run:
            conn.rollback()
            print("Dry run complete. No database changes were written.")
        else:
            conn.commit()
            print(f"Done. Updated {updated} rows.")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
