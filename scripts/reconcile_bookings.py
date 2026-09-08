#!/usr/bin/env python3
"""Reconcile the API's bookings against the worker's confirmed bookings.

Asynchronous processing has one characteristic failure: the two sides quietly
drift apart. A message is dead-lettered, an outbox row is stuck behind a broken
managed identity, a deploy loses a database file - and nothing raises an error,
because from each side's own point of view everything worked.

This compares the two stores and reports:

  * bookings the API accepted that the worker never confirmed
  * bookings the worker holds that the API does not (usually a replayed message
    against a rebuilt API store, occasionally something worse)
  * bookings present in both whose fields disagree
  * outbox rows that were never published
  * dead letters the worker recorded

Exit codes:
    0  the two sides agree
    1  discrepancies found
    2  could not read one of the sides

Usage:
    python scripts/reconcile_bookings.py --api-url http://localhost:5080 --worker-db worker.db
    python scripts/reconcile_bookings.py --api-url https://apim-x.azure-api.net/booking/v1 \
        --subscription-key "$KEY" --worker-db /home/data/worker.db --json
"""

from __future__ import annotations

import argparse
import json
import sqlite3
import sys
from dataclasses import dataclass, field
from decimal import Decimal
from pathlib import Path
from typing import Any

try:
    import requests
except ImportError:  # pragma: no cover - dependency guidance, not logic
    sys.exit("Missing dependency: requests\n"
             "Install it with: pip install -r scripts/requirements.txt")


# --------------------------------------------------------------------------- #
# Reading each side
# --------------------------------------------------------------------------- #

def read_api(api_url: str, subscription_key: str | None, token: str | None,
             limit: int, timeout: float) -> tuple[list[dict[str, Any]], dict[str, int]]:
    """Read the API's bookings and its outbox depth."""
    headers: dict[str, str] = {"Accept": "application/json"}
    if subscription_key:
        headers["Ocp-Apim-Subscription-Key"] = subscription_key
    if token:
        headers["Authorization"] = f"Bearer {token}"

    response = requests.get(f"{api_url.rstrip('/')}/internal/bookings",
                            params={"limit": limit}, headers=headers, timeout=timeout)
    response.raise_for_status()
    body = response.json()
    return body.get("items", []), body.get("outbox", {})


def read_worker(database_path: Path) -> tuple[dict[str, dict[str, Any]], list[dict[str, Any]]]:
    """Read confirmed bookings and dead letters from the worker's SQLite store."""
    if not database_path.exists():
        raise FileNotFoundError(database_path)

    # Read-only URI: reconciliation must never be able to modify what it audits,
    # and this also avoids creating an empty database from a typo in the path.
    connection = sqlite3.connect(f"file:{database_path}?mode=ro", uri=True)
    connection.row_factory = sqlite3.Row
    try:
        confirmed = {
            row["booking_id"]: dict(row)
            for row in connection.execute(
                "SELECT booking_id, property_id, nights, guests, total_amount, currency, "
                "confirmed_at, correlation_id FROM confirmed_bookings")
        }
        dead_letters = [
            dict(row) for row in connection.execute(
                "SELECT message_id, reason, description, recorded_at FROM dead_letters")
        ]
        return confirmed, dead_letters
    finally:
        connection.close()


# --------------------------------------------------------------------------- #
# Comparison
# --------------------------------------------------------------------------- #

@dataclass
class Report:
    api_count: int = 0
    worker_count: int = 0
    outbox_total: int = 0
    outbox_pending: int = 0

    awaiting_confirmation: list[dict[str, Any]] = field(default_factory=list)
    missing_in_worker: list[dict[str, Any]] = field(default_factory=list)
    missing_in_api: list[str] = field(default_factory=list)
    field_mismatches: list[dict[str, Any]] = field(default_factory=list)
    dead_letters: list[dict[str, Any]] = field(default_factory=list)

    @property
    def discrepancy_count(self) -> int:
        # `awaiting_confirmation` is excluded on purpose: a PENDING booking whose
        # message has not been published yet is the system working, not drifting.
        return (len(self.missing_in_worker)
                + len(self.missing_in_api)
                + len(self.field_mismatches)
                + len(self.dead_letters))


def compare(api_bookings: list[dict[str, Any]], outbox: dict[str, int],
            worker_bookings: dict[str, dict[str, Any]],
            dead_letters: list[dict[str, Any]]) -> Report:
    report = Report(
        api_count=len(api_bookings),
        worker_count=len(worker_bookings),
        outbox_total=int(outbox.get("total", 0)),
        outbox_pending=int(outbox.get("pending", 0)),
        dead_letters=dead_letters,
    )

    for booking in api_bookings:
        booking_id = booking["bookingId"]
        counterpart = worker_bookings.get(booking_id)

        if counterpart is None:
            entry = {
                "bookingId": booking_id,
                "status": booking.get("status"),
                "createdAt": booking.get("createdAt"),
                "correlationId": booking.get("correlationId"),
            }
            if booking.get("status") == "PENDING":
                # Not yet published, so the worker has not seen it. Expected.
                report.awaiting_confirmation.append(entry)
            else:
                # Published but never confirmed: the message was lost, dead-lettered,
                # or the worker is down. This is the one that matters.
                report.missing_in_worker.append(entry)
            continue

        mismatches = {}

        # Compared as Decimal via str: float would report 592.40 and 592.4 as
        # different, which is exactly the false positive that makes people ignore
        # a reconciliation report.
        api_amount = Decimal(str(booking["totalAmount"]))
        worker_amount = Decimal(str(counterpart["total_amount"]))
        if api_amount != worker_amount:
            mismatches["totalAmount"] = {"api": str(api_amount), "worker": str(worker_amount)}

        if int(booking["nights"]) != int(counterpart["nights"]):
            mismatches["nights"] = {"api": booking["nights"], "worker": counterpart["nights"]}

        if booking["propertyId"] != counterpart["property_id"]:
            mismatches["propertyId"] = {"api": booking["propertyId"],
                                        "worker": counterpart["property_id"]}

        if booking["currency"] != counterpart["currency"]:
            mismatches["currency"] = {"api": booking["currency"], "worker": counterpart["currency"]}

        if booking.get("correlationId") != counterpart.get("correlation_id"):
            mismatches["correlationId"] = {"api": booking.get("correlationId"),
                                           "worker": counterpart.get("correlation_id")}

        if mismatches:
            report.field_mismatches.append({"bookingId": booking_id, "fields": mismatches})

    api_ids = {booking["bookingId"] for booking in api_bookings}
    report.missing_in_api = sorted(set(worker_bookings) - api_ids)

    return report


# --------------------------------------------------------------------------- #
# Output
# --------------------------------------------------------------------------- #

def print_report(report: Report) -> None:
    print()
    print("Reconciliation")
    print("=" * 60)
    print(f"  API bookings          : {report.api_count}")
    print(f"  Worker confirmations  : {report.worker_count}")
    print(f"  Outbox rows           : {report.outbox_total} ({report.outbox_pending} pending)")
    print(f"  Awaiting confirmation : {len(report.awaiting_confirmation)}  (expected, not a discrepancy)")
    print()

    if report.missing_in_worker:
        print(f"  ACCEPTED BUT NEVER CONFIRMED ({len(report.missing_in_worker)})")
        print("  The API published these but the worker never recorded them.")
        print("  Check the queue's dead-letter sub-queue and the worker logs.")
        for entry in report.missing_in_worker[:20]:
            print(f"    - {entry['bookingId']}  status={entry['status']}  "
                  f"correlation={entry['correlationId']}")
        if len(report.missing_in_worker) > 20:
            print(f"    ... and {len(report.missing_in_worker) - 20} more")
        print()

    if report.missing_in_api:
        print(f"  IN THE WORKER BUT NOT IN THE API ({len(report.missing_in_api)})")
        print("  Usually a rebuilt API store; occasionally a replayed message for a")
        print("  booking that no longer exists.")
        for booking_id in report.missing_in_api[:20]:
            print(f"    - {booking_id}")
        print()

    if report.field_mismatches:
        print(f"  FIELD MISMATCHES ({len(report.field_mismatches)})")
        print("  The same booking, different values. Almost always a contract change")
        print("  deployed to one side only.")
        for mismatch in report.field_mismatches[:20]:
            print(f"    - {mismatch['bookingId']}")
            for name, values in mismatch["fields"].items():
                print(f"        {name}: api={values['api']!r} worker={values['worker']!r}")
        print()

    if report.dead_letters:
        print(f"  DEAD LETTERS ({len(report.dead_letters)})")
        for entry in report.dead_letters[:20]:
            print(f"    - {entry['message_id']}  {entry['reason']}: {entry['description'][:100]}")
        print()

    if report.outbox_pending > 0:
        print(f"  NOTE: {report.outbox_pending} outbox row(s) are unpublished. If that number")
        print("  is not falling, the API cannot reach Service Bus - check the managed")
        print("  identity role assignment on the queue.")
        print()

    if report.discrepancy_count == 0:
        print("  No discrepancies. The two sides agree.")
    else:
        print(f"  {report.discrepancy_count} discrepancy/discrepancies found.")
    print()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--api-url", default="http://localhost:5080",
                        help="Base URL of the Booking API or its gateway")
    parser.add_argument("--worker-db", default="worker.db", type=Path,
                        help="Path to the worker's SQLite store")
    parser.add_argument("--subscription-key", default=None,
                        help="APIM subscription key when going through the gateway")
    parser.add_argument("--token", default=None, help="Entra ID bearer token")
    parser.add_argument("--limit", type=int, default=1000, help="Maximum bookings to read")
    parser.add_argument("--timeout", type=float, default=30.0, help="HTTP timeout in seconds")
    parser.add_argument("--json", action="store_true", help="Emit JSON instead of a report")
    args = parser.parse_args()

    try:
        api_bookings, outbox = read_api(args.api_url, args.subscription_key, args.token,
                                        args.limit, args.timeout)
    except requests.exceptions.RequestException as exc:
        print(f"Could not read the API at {args.api_url}: {exc}", file=sys.stderr)
        return 2

    try:
        worker_bookings, dead_letters = read_worker(args.worker_db)
    except FileNotFoundError:
        print(f"Worker store not found at {args.worker_db}.", file=sys.stderr)
        print("Point --worker-db at the worker's SQLite file; in Azure it is under "
              "/home/data on whatever hosts the worker.", file=sys.stderr)
        return 2
    except sqlite3.DatabaseError as exc:
        print(f"Could not read {args.worker_db}: {exc}", file=sys.stderr)
        return 2

    report = compare(api_bookings, outbox, worker_bookings, dead_letters)

    if args.json:
        print(json.dumps({
            "apiCount": report.api_count,
            "workerCount": report.worker_count,
            "outbox": {"total": report.outbox_total, "pending": report.outbox_pending},
            "awaitingConfirmation": report.awaiting_confirmation,
            "missingInWorker": report.missing_in_worker,
            "missingInApi": report.missing_in_api,
            "fieldMismatches": report.field_mismatches,
            "deadLetters": report.dead_letters,
            "discrepancyCount": report.discrepancy_count,
        }, indent=2, default=str))
    else:
        print_report(report)

    return 0 if report.discrepancy_count == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
