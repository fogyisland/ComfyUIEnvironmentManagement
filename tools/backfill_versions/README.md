# backfill_catalog_versions.py

Batch pre-fill script for `node_versions` in `catalog-cache.db`.

## Why this exists

The in-app Catalog panel showed a "Versions" dropdown per entry, but the data
came from on-demand `GET /repos/{o}/{r}/releases` calls — every panel open
triggered a network roundtrip. User feedback (2026-08-17):

> 肯定不能按需拉取了,我们现在就需要的是预先填充
> ("Definitely can't be on-demand. What we need is pre-fill.")

This script walks the entire catalog-cache.db and pre-populates `node_versions`
once, so panel opens hit only the local SQLite query.

## Schema

`node_versions(node_id TEXT, tag_name TEXT, published_at TEXT, is_prerelease INTEGER, fetched_at TEXT, PRIMARY KEY(node_id, tag_name))`

## Usage

```bash
# Default: 1 req/sec, full backfill (token = 5000/hr tier)
GITHUB_TOKEN=ghp_xxx python backfill_catalog_versions.py

# No token: 60/hr unauth — use ~0.0167 rps or it'll throttle
python backfill_catalog_versions.py --rps 0.0167

# Test on first 50 entries
python backfill_catalog_versions.py --limit 50

# Dry run (no API calls)
python backfill_catalog_versions.py --dry-run

# Re-fetch every github entry (overwrite existing)
python backfill_catalog_versions.py --include-existing

# Custom DB path
python backfill_catalog_versions.py --db-path /path/to/catalog-cache.db
```

## Rate limits

| Token   | Limit          | Safe `--rps` | ~5352 entries ETA |
|---------|----------------|--------------|-------------------|
| Yes     | 5000/hr        | 1.0          | ~89 min           |
| No      | 60/hr          | 0.0167       | ~89 hours         |

## Resume safety

The script only processes catalog entries where `id NOT IN (node_versions.node_id)` —
interrupted runs pick up exactly where they left off. Use `INSERT OR IGNORE` so even
manual additions don't conflict. 404 / non-GitHub / zero-release rows are NOT marked
as done (different from metadata backfill) because a repo may add a release later
and we want a future run to pick it up. Use `--include-existing` to refetch.

## Mirrors `backfill_catalog_metadata.py`

Same session/header/parse_owner_repo pattern. Differs in:
- Endpoint: `/releases?per_page=10` (matches in-app `MaxVersionsPerRepo`)
- Target table: `node_versions` (vs `catalog_cache` columns)
- Idempotency: `INSERT OR IGNORE` per (node_id, tag_name) row
- No `mark_skipped` step — failed/skipped rows are retryable