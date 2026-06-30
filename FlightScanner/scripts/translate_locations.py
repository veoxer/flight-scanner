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
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass
from pathlib import Path

try:
    import psycopg
    from psycopg.conninfo import make_conninfo
except ImportError as exc:
    raise SystemExit(
        "Missing dependency: install it with `pip install psycopg[binary]`."
    ) from exc


GOOGLE_TRANSLATE_URL = "https://translate.googleapis.com/translate_a/single"
DEFAULT_BATCH_SIZE = 30
DEFAULT_BATCH_MAX_CHARS = 3500


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


def load_cache(path: Path | None) -> dict[tuple[str, str], str]:
    if path is None or not path.exists():
        return {}

    with path.open("r", encoding="utf-8") as handle:
        raw = json.load(handle)
    return {
        tuple(key.split("\t", 1)): value
        for key, value in raw.items()
        if "\t" in key and isinstance(value, str) and value
    }


def save_cache(path: Path | None, cache: dict[tuple[str, str], str]) -> None:
    if path is None:
        return

    path.parent.mkdir(parents=True, exist_ok=True)
    raw = {f"{target}\t{text}": value for (target, text), value in sorted(cache.items())}
    temporary = path.with_suffix(path.suffix + ".tmp")
    with temporary.open("w", encoding="utf-8") as handle:
        json.dump(raw, handle, ensure_ascii=False, indent=2, sort_keys=True)
    temporary.replace(path)


def translate(
    text: str,
    target: str,
    pause_seconds: float,
    max_retries: int,
    retry_delay_seconds: float,
) -> str | None:
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

    for attempt in range(1, max_retries + 2):
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                payload = json.loads(response.read().decode("utf-8"))

            translated = "".join(part[0] for part in payload[0] if part and part[0]).strip()
            if pause_seconds > 0:
                time.sleep(pause_seconds)
            return translated or text
        except (urllib.error.HTTPError, urllib.error.URLError, TimeoutError, json.JSONDecodeError) as exc:
            if attempt > max_retries:
                print(f"WARNING: failed to translate {target}:{text!r}: {exc}")
                return None

            backoff = retry_delay_seconds * attempt
            print(f"Retry {attempt}/{max_retries} for {target}:{text!r} after error: {exc}")
            time.sleep(backoff)


def cached_translate(
    cache: dict[tuple[str, str], str],
    text: str | None,
    target: str,
    pause_seconds: float,
    max_retries: int,
    retry_delay_seconds: float,
) -> str | None:
    if not text or not text.strip():
        return None

    normalized = text.strip()
    key = (target, normalized)
    if key not in cache:
        translated = translate(normalized, target, pause_seconds, max_retries, retry_delay_seconds)
        if translated:
            cache[key] = translated
    return cache.get(key)


def chunk_texts(texts: list[str], batch_size: int, max_chars: int) -> list[list[str]]:
    chunks: list[list[str]] = []
    current: list[str] = []
    current_chars = 0
    for text in texts:
        text_chars = len(text) + 1
        if current and (len(current) >= batch_size or current_chars + text_chars > max_chars):
            chunks.append(current)
            current = []
            current_chars = 0

        current.append(text)
        current_chars += text_chars

    if current:
        chunks.append(current)
    return chunks


def batch_translate(
    texts: list[str],
    target: str,
    pause_seconds: float,
    max_retries: int,
    retry_delay_seconds: float,
) -> list[str | None]:
    if not texts:
        return []
    if len(texts) == 1:
        return [translate(texts[0], target, pause_seconds, max_retries, retry_delay_seconds)]

    joined = "\n".join(texts)
    translated = translate(joined, target, pause_seconds, max_retries, retry_delay_seconds)
    if not translated:
        return [None] * len(texts)

    lines = [line.strip() for line in translated.splitlines()]
    if len(lines) == len(texts) and all(lines):
        return lines

    print(
        f"WARNING: batch split mismatch for {target}. "
        f"Expected {len(texts)} lines, got {len(lines)}. Falling back to individual calls."
    )
    return [translate(text, target, pause_seconds, max_retries, retry_delay_seconds) for text in texts]


def prefill_cache(
    cache: dict[tuple[str, str], str],
    texts: list[str],
    target: str,
    pause_seconds: float,
    max_retries: int,
    retry_delay_seconds: float,
    batch_size: int,
    batch_max_chars: int,
    cache_file: Path | None,
) -> None:
    missing = sorted({
        text.strip()
        for text in texts
        if text and text.strip() and (target, text.strip()) not in cache
    })
    if not missing:
        print(f"{target}: no missing translations.")
        return

    chunks = chunk_texts(missing, max(1, batch_size), max(100, batch_max_chars))
    print(f"{target}: translating {len(missing)} unique strings in {len(chunks)} batches...")
    for index, chunk in enumerate(chunks, start=1):
        translations = batch_translate(chunk, target, pause_seconds, max_retries, retry_delay_seconds)
        for source, translated in zip(chunk, translations):
            if translated:
                cache[(target, source)] = translated

        if index % 10 == 0 or index == len(chunks):
            save_cache(cache_file, cache)
            print(f"{target}: translated batch {index}/{len(chunks)}; cache={len(cache)}")


def collect_missing_texts(rows: list[LocationRow], target: str) -> list[str]:
    texts: list[str] = []
    for row in rows:
        if target == "fr":
            if not row.name_fr:
                texts.append(row.name)
            if row.country_name and not row.country_name_fr:
                texts.append(row.country_name)
            if not row.continent_fr:
                texts.append(row.continent)
        elif target == "ar":
            if not row.name_ar:
                texts.append(row.name)
            if row.country_name and not row.country_name_ar:
                texts.append(row.country_name)
            if not row.continent_ar:
                texts.append(row.continent)
    return texts


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


def ensure_translation_column_lengths(conn) -> None:
    with conn.cursor() as cur:
        cur.execute(
            """
            ALTER TABLE "FlightLocations"
            ALTER COLUMN "NameFr" TYPE character varying(300);

            ALTER TABLE "FlightLocations"
            ALTER COLUMN "NameAr" TYPE character varying(300);

            ALTER TABLE "FlightLocations"
            ALTER COLUMN "CountryNameFr" TYPE character varying(160);

            ALTER TABLE "FlightLocations"
            ALTER COLUMN "CountryNameAr" TYPE character varying(160);

            ALTER TABLE "FlightLocations"
            ALTER COLUMN "ContinentFr" TYPE character varying(80);

            ALTER TABLE "FlightLocations"
            ALTER COLUMN "ContinentAr" TYPE character varying(80);
            """
        )
    conn.commit()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--limit", type=int, default=None, help="Maximum rows to process.")
    parser.add_argument("--dry-run", action="store_true", help="Translate but do not update the database.")
    parser.add_argument("--pause", type=float, default=0.08, help="Delay between uncached translate calls.")
    parser.add_argument("--retries", type=int, default=5, help="Retries per translation before leaving that field empty.")
    parser.add_argument("--retry-delay", type=float, default=3.0, help="Base seconds for retry backoff.")
    parser.add_argument("--batch-size", type=int, default=DEFAULT_BATCH_SIZE, help="Unique strings per Google Translate request.")
    parser.add_argument("--batch-max-chars", type=int, default=DEFAULT_BATCH_MAX_CHARS, help="Maximum source characters per batch.")
    parser.add_argument(
        "--cache-file",
        type=Path,
        default=Path(__file__).with_name("translation-cache.json"),
        help="Persistent translation cache JSON file.",
    )
    parser.add_argument("--commit-every", type=int, default=100, help="Commit after this many updated rows.")
    args = parser.parse_args()

    cache = load_cache(args.cache_file)
    print(f"Loaded {len(cache)} cached translations from {args.cache_file}")
    updated = 0
    with psycopg.connect(connection_string()) as conn:
        if not args.dry_run:
            ensure_translation_column_lengths(conn)
        rows = load_rows(conn, args.limit)
        print(f"Rows to inspect: {len(rows)}")
        prefill_cache(
            cache,
            collect_missing_texts(rows, "fr"),
            "fr",
            args.pause,
            args.retries,
            args.retry_delay,
            args.batch_size,
            args.batch_max_chars,
            args.cache_file,
        )
        prefill_cache(
            cache,
            collect_missing_texts(rows, "ar"),
            "ar",
            args.pause,
            args.retries,
            args.retry_delay,
            args.batch_size,
            args.batch_max_chars,
            args.cache_file,
        )

        for row in rows:
            values = {
                "NameFr": row.name_fr or cached_translate(cache, row.name, "fr", args.pause, args.retries, args.retry_delay),
                "NameAr": row.name_ar or cached_translate(cache, row.name, "ar", args.pause, args.retries, args.retry_delay),
                "CountryNameFr": row.country_name_fr or cached_translate(cache, row.country_name, "fr", args.pause, args.retries, args.retry_delay),
                "CountryNameAr": row.country_name_ar or cached_translate(cache, row.country_name, "ar", args.pause, args.retries, args.retry_delay),
                "ContinentFr": row.continent_fr or cached_translate(cache, row.continent, "fr", args.pause, args.retries, args.retry_delay),
                "ContinentAr": row.continent_ar or cached_translate(cache, row.continent, "ar", args.pause, args.retries, args.retry_delay),
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
                save_cache(args.cache_file, cache)
                print(f"Committed {updated} rows...")

        if args.dry_run:
            conn.rollback()
            save_cache(args.cache_file, cache)
            print("Dry run complete. No database changes were written.")
        else:
            conn.commit()
            save_cache(args.cache_file, cache)
            print(f"Done. Updated {updated} rows.")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
