"""Download every Singapore bus stop, service and route from LTA DataMall into SQLite.

Run from backend/: python -m src.transit.import_lta [--output PATH]

Needs a free DataMall account key in LTA_ACCOUNT_KEY (environment or backend/.env).
The default output is the API's transit database, backend/fixtures/transit/transit.sqlite.
"""

from __future__ import annotations

import argparse
import json
import sys
import time
from datetime import UTC, datetime
from os import getenv
from pathlib import Path
from typing import Callable
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

from dotenv import load_dotenv

from src.core.config import get_settings
from src.transit.store import write_database

BASE_URL = "https://datamall2.mytransport.sg/ltaodataservice"
# DataMall returns at most 500 records per call; $skip pages through the rest
PAGE_SIZE = 500
# BusRoutes has about 26,000 rows; stop well past that rather than loop forever
MAX_PAGES = 200

Fetch = Callable[[str, str], list[dict]]


class DataMallError(RuntimeError):
    pass


def fetch_page(url: str, account_key: str) -> list[dict]:
    request = Request(url, headers={"AccountKey": account_key, "accept": "application/json"})
    for attempt in range(3):
        try:
            with urlopen(request, timeout=30) as response:
                body = json.load(response)
            break
        except HTTPError as error:
            if error.code in (401, 403):
                raise DataMallError("DataMall rejected the account key (check LTA_ACCOUNT_KEY)") from error
            if attempt == 2:
                raise DataMallError(f"DataMall answered {error.code} for {url}") from error
        except (URLError, TimeoutError) as error:
            if attempt == 2:
                raise DataMallError(f"Could not reach DataMall: {error}") from error
        time.sleep(2 * (attempt + 1))
    value = body.get("value")
    if not isinstance(value, list):
        raise DataMallError(f"DataMall answer for {url} has no 'value' list")
    return value


def fetch_all(dataset: str, account_key: str, fetch: Fetch = fetch_page) -> list[dict]:
    rows: list[dict] = []
    for page in range(MAX_PAGES):
        batch = fetch(f"{BASE_URL}/{dataset}?$skip={page * PAGE_SIZE}", account_key)
        rows.extend(batch)
        if len(batch) < PAGE_SIZE:
            return rows
    raise DataMallError(f"{dataset} did not end after {MAX_PAGES} pages")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--output", type=Path, default=None, help="SQLite file to write")
    arguments = parser.parse_args(argv)
    project_root = Path(__file__).resolve().parents[3]
    load_dotenv(project_root / "backend" / ".env", override=False)
    account_key = getenv("LTA_ACCOUNT_KEY", "").strip()
    if not account_key:
        print("Set LTA_ACCOUNT_KEY in backend/.env (free key from datamall.lta.gov.sg)", file=sys.stderr)
        return 2
    output = arguments.output
    if output is None:
        output = get_settings().transit_database_path
    try:
        datasets = {}
        for dataset in ("BusStops", "BusServices", "BusRoutes"):
            datasets[dataset] = fetch_all(dataset, account_key)
            print(f"{dataset}: {len(datasets[dataset])} records")
    except DataMallError as error:
        print(error, file=sys.stderr)
        return 1
    counts = write_database(output, datasets["BusStops"], datasets["BusServices"], datasets["BusRoutes"],
                            datetime.now(UTC).isoformat(timespec="seconds"))
    print(f"Wrote {counts['stops']} stops, {counts['services']} services, {counts['routes']} route stops to {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
