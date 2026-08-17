#!/usr/bin/env python3
"""
Backfill node_versions rows for catalog_cache entries that have a GitHub reference
but no version history yet.

Why this exists:
    v0.6.17 panel "node details auto-fetch versions" was implemented as on-demand
    fetches (triggered when node_versions is empty for the selected entry). User
    feedback: "肯定不能按需拉取了,我们现在就需要的是预先填充" — pre-fill the entire
    table once instead of triggering a network roundtrip on every detail panel
    open. This script mirrors backfill_catalog_metadata.py but targets the
    releases endpoint and the node_versions table.

Schema (from CatalogCacheStore.cs):
    node_versions(node_id TEXT, tag_name TEXT, published_at TEXT,
                  is_prerelease INTEGER, fetched_at TEXT,
                  PRIMARY KEY(node_id, tag_name))

Query: catalog_cache entries whose reference is github.com AND which have zero
       rows in node_versions for their id.

Per-entry API call: GET /repos/{owner}/{repo}/releases?per_page=10 (matches
                    in-app GitHubVersionService.MaxVersionsPerRepo).

Usage:
    # Default: 1 req/sec, full backfill
    GITHUB_TOKEN=ghp_xxx python backfill_catalog_versions.py

    # Test with first 50 entries
    python backfill_catalog_versions.py --limit 50

    # Dry run — list entries that would be processed
    python backfill_catalog_versions.py --dry-run

    # Re-fetch everything (ignore existing rows)
    python backfill_catalog_versions.py --include-existing

Rate limits:
    Token (5000/hr = 1.39/s): 1 req/sec is safe.
    No token (60/hr = 1/min): use --rps 0.0167 (= 1/60s).
    ~5352 entries needing backfill = ~89 min with token, ~89 hours without.

Resume-safe: only processes rows where id NOT IN (node_versions.node_id).
             Failed rows (5xx, network) leave existing rows intact, will retry.
             404 / non-GitHub rows are still attempted on next run (no fetched_at
             marker — they may simply be missing releases right now).
"""
from __future__ import annotations

import argparse
import json
import os
import sqlite3
import sys
import time
from urllib.parse import urlparse

try:
    import requests
    from requests.adapters import HTTPAdapter
except ImportError:
    sys.exit("ERROR: 'requests' not installed. Run: pip install requests")

DEFAULT_DB = r"D:\ToolDevelop\ComfyUI\release\staging\ComfyUI Manager\data\catalog-cache.db"


def parse_args() -> argparse.Namespace:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--db-path", default=os.environ.get("CATALOG_DB_PATH", DEFAULT_DB),
                   help=f"path to catalog-cache.db (default: {DEFAULT_DB})")
    p.add_argument("--rps", type=float, default=1.0,
                   help="requests per second (default 1.0; safe for 5000/hr token tier)")
    p.add_argument("--per-page", type=int, default=10,
                   help="releases per entry (default 10, matches in-app MaxVersionsPerRepo)")
    p.add_argument("--limit", type=int, default=0,
                   help="max entries to process (0 = all)")
    p.add_argument("--progress-every", type=int, default=25)
    p.add_argument("--dry-run", action="store_true",
                   help="list entries that would be processed, don't make API calls")
    p.add_argument("--max-retries", type=int, default=2,
                   help="retries per entry on 5xx/network (default 2)")
    p.add_argument("--include-existing", action="store_true",
                   help="process ALL github entries (ignore existing node_versions rows)")
    return p.parse_args()


def parse_owner_repo(reference: str) -> tuple[str | None, str | None]:
    """Extract (owner, repo) from a github.com URL. Returns (None, None) if not github."""
    if not reference:
        return None, None
    lower = reference.lower()
    if "github.com" not in lower:
        return None, None
    try:
        u = urlparse(reference)
        if u.netloc.lower() not in ("github.com", "www.github.com"):
            return None, None
        segs = [s for s in u.path.strip("/").split("/") if s]
        if len(segs) >= 2 and segs[0] and segs[1]:
            repo = segs[1].rstrip("/")
            if repo.endswith(".git"):
                repo = repo[:-4]
            return segs[0], repo
    except Exception:
        pass
    return None, None


def make_session(token: str) -> requests.Session:
    s = requests.Session()
    s.headers.update({
        "Accept": "application/vnd.github+json",
        "User-Agent": "comfyui-manager-versions-backfill",
    })
    if token:
        s.headers["Authorization"] = f"Bearer {token}"
    adapter = HTTPAdapter(pool_connections=4, pool_maxsize=4)
    s.mount("https://", adapter)
    s.mount("http://", adapter)
    return s


def gh_get_releases(session: requests.Session, url: str, timeout: float = 30.0
                    ) -> tuple[list | None, dict]:
    """GET GitHub /releases endpoint. Returns (releases_list_or_none, headers dict)."""
    try:
        resp = session.get(url, timeout=timeout)
        if resp.status_code == 404:
            return None, dict(resp.headers)
        if resp.status_code == 403 and resp.headers.get("X-RateLimit-Remaining") == "0":
            return None, dict(resp.headers)
        if resp.status_code >= 500:
            return None, dict(resp.headers)
        if not resp.ok:
            return None, dict(resp.headers)
        try:
            data = resp.json()
            if isinstance(data, list):
                return data, dict(resp.headers)
            return None, dict(resp.headers)
        except (ValueError, json.JSONDecodeError):
            return None, dict(resp.headers)
    except (requests.RequestException, json.JSONDecodeError, OSError) as e:
        print(f"  network error: {type(e).__name__}: {e}", file=sys.stderr, flush=True)
        return None, {}


def now_iso() -> str:
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())


def select_targets(conn: sqlite3.Connection, include_existing: bool) -> list[sqlite3.Row]:
    """Select catalog_cache rows whose reference is github.com and (by default)
    that have no rows in node_versions."""
    if include_existing:
        return conn.execute(
            "SELECT id, package, reference FROM catalog_cache "
            "WHERE reference LIKE '%github.com%' "
            "ORDER BY id"
        ).fetchall()
    return conn.execute(
        """
        SELECT c.id, c.package, c.reference
        FROM catalog_cache c
        WHERE c.reference LIKE '%github.com%'
          AND c.id NOT IN (SELECT DISTINCT node_id FROM node_versions)
        ORDER BY c.id
        """
    ).fetchall()


def upsert_versions(conn: sqlite3.Connection, node_id: str,
                    releases: list, fetched_at: str) -> int:
    """INSERT OR IGNORE one row per release. Returns count of rows actually
    inserted (existing (node_id, tag_name) combos are skipped)."""
    inserted = 0
    for rel in releases:
        tag = rel.get("tag_name")
        published = rel.get("published_at")
        if not tag or not published:
            continue
        is_pre = 1 if rel.get("prerelease") else 0
        cur = conn.execute(
            """
            INSERT OR IGNORE INTO node_versions
                (node_id, tag_name, published_at, is_prerelease, fetched_at)
            VALUES (?, ?, ?, ?, ?)
            """,
            (node_id, tag, published, is_pre, fetched_at),
        )
        if cur.rowcount > 0:
            inserted += 1
    return inserted


def main() -> int:
    args = parse_args()
    try:
        sys.stdout.reconfigure(line_buffering=True)
        sys.stderr.reconfigure(line_buffering=True)
    except Exception:
        pass
    token = os.environ.get("GITHUB_TOKEN", "").strip()
    if not token:
        print("WARNING: GITHUB_TOKEN env var not set — 60/hr unauth limit will throttle heavily",
              file=sys.stderr, flush=True)
    if args.rps > 1.5 and token:
        print(f"WARNING: --rps {args.rps} exceeds 5000/hr token tier (1.39/s)",
              file=sys.stderr, flush=True)
    elif args.rps > 0.0167 and not token:
        print(f"WARNING: --rps {args.rps} exceeds 60/hr unauth tier (0.0167/s)",
              file=sys.stderr, flush=True)

    sleep_s = 1.0 / args.rps if args.rps > 0 else 0

    if not os.path.isfile(args.db_path):
        print(f"DB not found: {args.db_path}", file=sys.stderr)
        return 1

    conn = sqlite3.connect(args.db_path)
    conn.row_factory = sqlite3.Row
    conn.execute("PRAGMA journal_mode = WAL")
    conn.execute("PRAGMA busy_timeout = 5000")

    rows = select_targets(conn, args.include_existing)
    total = len(rows)
    print(f"DB: {args.db_path}", flush=True)
    print(f"per_page: {args.per_page}", flush=True)
    print(f"include_existing: {args.include_existing}", flush=True)
    print(f"Total entries needing versions: {total}", flush=True)
    if args.limit:
        rows = rows[:args.limit]
        print(f"Limiting to first {args.limit}", flush=True)
    if args.dry_run:
        print(f"\n--- DRY RUN: would process {len(rows)} entries ---", flush=True)
        for r in rows[:20]:
            owner, repo = parse_owner_repo(r["reference"])
            tag = f"{owner}/{repo}" if owner else "<non-github>"
            print(f"  {r['package']:<45} -> {tag}", flush=True)
        if len(rows) > 20:
            print(f"  ... and {len(rows) - 20} more", flush=True)
        return 0

    if total == 0:
        print("Nothing to do — all github entries already have node_versions rows", flush=True)
        return 0

    done = 0
    inserted_total = 0
    skipped_non_github = 0
    skipped_404 = 0
    zero_release_count = 0
    rate_limited_hits = 0
    errors = 0
    start = time.monotonic()
    session = make_session(token)

    for i, row in enumerate(rows, 1):
        owner, repo = parse_owner_repo(row["reference"])
        if not owner:
            skipped_non_github += 1
            continue

        url = f"https://api.github.com/repos/{owner}/{repo}/releases?per_page={args.per_page}"
        releases = None
        last_status = 0
        for attempt in range(args.max_retries + 1):
            data, headers = gh_get_releases(session, url)
            last_status = int(headers.get(":status", headers.get("status", 0)) or 0)
            if data is not None:
                releases = data
                break
            if headers.get("X-RateLimit-Remaining") == "0":
                reset = int(headers.get("X-RateLimit-Reset", 0) or 0)
                wait_s = max(reset - int(time.time()) + 5, 60)
                print(f"  ⚠ rate limited, sleeping {wait_s}s until reset", file=sys.stderr, flush=True)
                rate_limited_hits += 1
                time.sleep(wait_s)
                continue
            if attempt < args.max_retries:
                time.sleep(2 ** attempt)
                continue
            break

        if releases is None:
            if last_status == 404:
                skipped_404 += 1
            else:
                errors += 1
            if i % args.progress_every == 0 or i <= 3:
                print(f"  [{i}/{len(rows)}] {row['package']!r} FAILED status={last_status} "
                      f"(skip_404={skipped_404} err={errors})", flush=True)
            time.sleep(sleep_s)
            continue

        if len(releases) == 0:
            zero_release_count += 1

        fetched_at = now_iso()
        inserted = upsert_versions(conn, row["id"], releases, fetched_at)
        conn.commit()
        done += 1
        inserted_total += inserted

        if i % args.progress_every == 0 or i == len(rows):
            elapsed = time.monotonic() - start
            rate = done / elapsed if elapsed > 0 else 0
            eta_s = (len(rows) - i) / rate if rate > 0 else 0
            print(f"[{i:5d}/{len(rows)}] done={done} inserted={inserted_total} "
                  f"zero_rel={zero_release_count} skip_404={skipped_404} "
                  f"skip_ng={skipped_non_github} rl={rate_limited_hits} err={errors} | "
                  f"elapsed={elapsed:.0f}s rate={rate:.2f}/s eta={eta_s:.0f}s | "
                  f"last={row['package']!r}", flush=True)

        time.sleep(sleep_s)
    session.close()

    conn.close()
    elapsed = time.monotonic() - start
    print(f"\n=== DONE ===", flush=True)
    print(f"  Entries processed:        {done}", flush=True)
    print(f"  Total version rows added: {inserted_total}", flush=True)
    print(f"  Skipped (non-github):     {skipped_non_github}", flush=True)
    print(f"  Skipped (404):            {skipped_404}", flush=True)
    print(f"  Zero-release entries:     {zero_release_count}", flush=True)
    print(f"  Rate-limit waits:         {rate_limited_hits}", flush=True)
    print(f"  Errors (will retry):      {errors}", flush=True)
    print(f"  Elapsed:                  {elapsed:.0f}s", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())