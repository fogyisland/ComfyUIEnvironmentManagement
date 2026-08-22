#!/usr/bin/env python3
"""v1.0.0 release: 预填 catalog-cache.db 的 catalog_cache 表。

T7-fix1 schema:
- 只创建 catalog_cache(33 列,与 C# CatalogCacheStore.InitSchemaIfMissing 完全一致)。
- 不写 node_versions(架构上不可能:C# 用 Guid.NewGuid() 给 catalog_cache.id 分配,
  NodeVersionRepository.UpsertBatch 通过 SELECT id FROM catalog_cache WHERE
  (source_url, package) 解析 node_id —— 脚本里预填的 id 永远跟 C# runtime id 对不上)。
- 不调 GitHub API(匿名限速 60/hr,~5800 repos × 10 versions ≈ 6 万请求不可行)。
- 一次 HTTPS GET 拿 custom-node-list.json,bulk INSERT。

Schema 镜像策略:用 PRAGMA table_info 检查列是否存在,再决定是否 ALTER —— 跟 C#
EnsureColumn 完全对齐(避免 try/except OperationalError 这种异常驱动控制流)。
"""
import json
import os
import sqlite3
import sys
import time
import uuid
import urllib.request

CATALOG_URL = (
    "https://raw.githubusercontent.com/ltdrdata/ComfyUI-Manager/main/custom-node-list.json"
)

# 跟 C# CatalogCacheStore.InitSchemaIfMissing 完全对齐 —— 顺序也按 PRAGMA table_info
# 的列序,方便人肉 diff 校对。latest_version 在 base CREATE 里(跟 C# 一致)。
_BASE_COLUMNS = [
    ("id", "TEXT PRIMARY KEY"),
    ("source_url", "TEXT NOT NULL"),
    ("package", "TEXT NOT NULL"),
    ("raw_metadata", "TEXT NOT NULL"),
    ("cached_at", "TEXT NOT NULL"),
    ("expires_at", "TEXT NOT NULL"),
    ("latest_version", "TEXT"),
]
_INCREMENTAL_COLUMNS = [
    # v0.6.7.4 typed columns(6)
    ("author", "TEXT"),
    ("description", "TEXT"),
    ("install_type", "TEXT"),
    ("reference", "TEXT"),
    ("last_update", "TEXT"),
    ("pip_json", "TEXT"),
    # v0.6.13-B GitHub metadata columns(11)
    ("license", "TEXT"),
    ("tags_json", "TEXT"),
    ("stars", "INTEGER"),
    ("downloads", "INTEGER"),
    ("last_commit", "TEXT"),
    ("readme_markdown", "TEXT"),
    ("latest_changelog", "TEXT"),
    ("deprecated", "INTEGER"),
    ("python_compat_json", "TEXT"),
    ("os_compat_json", "TEXT"),
    ("metadata_fetched_at", "TEXT"),
    # v0.6.14 incremental refresh + GitHub fields(9)
    ("content_hash", "TEXT NOT NULL DEFAULT ''"),
    ("html_url", "TEXT"),
    ("homepage", "TEXT"),
    ("language", "TEXT"),
    ("forks_count", "INTEGER"),
    ("open_issues_count", "INTEGER"),
    ("release_tag", "TEXT"),
    ("subscribers_count", "INTEGER"),
    ("created_at", "TEXT"),
]


def _column_exists(conn, table, column):
    """镜像 C# CatalogCacheStore.EnsureColumn 的 PRAGMA table_info 检查。"""
    cur = conn.execute(f"PRAGMA table_info({table})")
    rows = cur.fetchall()
    # PRAGMA table_info 返回 (cid, name, type, notnull, dflt_value, pk)
    for row in rows:
        if str(row[1]).lower() == column.lower():
            return True
    return False


def ensure_schema(conn):
    """创建 catalog_cache 表 + 26 个 incremental 列,完全镜像 C# InitSchemaIfMissing。"""
    base_cols_sql = ", ".join(f"{n} {t}" for n, t in _BASE_COLUMNS)
    conn.execute(
        f"CREATE TABLE IF NOT EXISTS catalog_cache "
        f"({base_cols_sql}, UNIQUE(source_url, package))"
    )
    for name, col_type in _INCREMENTAL_COLUMNS:
        if not _column_exists(conn, "catalog_cache", name):
            conn.execute(f"ALTER TABLE catalog_cache ADD COLUMN {name} {col_type}")
    # 跟 C# 一致:3 个排序/过滤索引
    conn.execute(
        "CREATE INDEX IF NOT EXISTS idx_catalog_cache_stars "
        "ON catalog_cache(stars DESC)"
    )
    conn.execute(
        "CREATE INDEX IF NOT EXISTS idx_catalog_cache_downloads "
        "ON catalog_cache(downloads DESC)"
    )
    conn.execute(
        "CREATE INDEX IF NOT EXISTS idx_catalog_cache_deprecated "
        "ON catalog_cache(deprecated)"
    )
    # catalog_http_cache 也建一下,跟 C# 一致 —— C# 启动会读这张表。
    conn.execute(
        "CREATE TABLE IF NOT EXISTS catalog_http_cache ("
        "url TEXT PRIMARY KEY, "
        "etag TEXT, "
        "last_modified TEXT, "
        "fetched_at TEXT NOT NULL)"
    )
    conn.commit()


def fetch_catalog():
    """单次 HTTPS GET 拿 custom-node-list.json。无 auth,无 GitHub API 依赖。"""
    req = urllib.request.Request(
        CATALOG_URL,
        headers={"User-Agent": "ComfyUIManagement-prefill/1.0.0"},
    )
    with urllib.request.urlopen(req, timeout=30) as resp:
        raw = resp.read()
    return json.loads(raw.decode("utf-8"))


def _entry_to_row(entry, now_iso, expires_iso):
    """一个 catalog entry → catalog_cache INSERT 参数。defensive .get() 全程。

    id = uuid5(source_url + "|" + package) —— 确定性 id 方便调试
    (虽然 C# 首次 refresh 会用 Guid.NewGuid() 替换,见 NodeVersionRepository.cs
    line 24-29 的注释)。确定性至少让"重跑脚本不会变 id"成立。
    """
    # defensive .get(..., default) 全程 —— entry 缺字段不崩。
    package = entry.get("id", "")
    if not package:
        raise ValueError("entry missing 'id' (package name)")
    source_url = entry.get("reference", "") or entry.get("repository", "")
    if not source_url:
        raise ValueError(f"entry '{package}' missing both reference and repository")
    # uuid5 确定性 hash —— 32 字符 hex,跟 Guid.NewGuid().ToString("N") 等长。
    row_id = uuid.uuid5(uuid.NAMESPACE_URL, source_url + "|" + package).hex
    html_url = entry.get("reference", "") or entry.get("repository", "")
    return (
        row_id,                  # id
        source_url,              # source_url
        package,                 # package
        json.dumps(entry, ensure_ascii=False),  # raw_metadata
        now_iso,                 # cached_at
        expires_iso,             # expires_at
        None,                    # latest_version — C# 首次 refresh 填
        entry.get("author", ""), # author
        entry.get("description", ""),  # description
        None,                    # install_type — JSON 没这字段
        entry.get("reference", ""),    # reference
        "",                      # last_update — C# refresh 填
        None,                    # pip_json — JSON 没这字段
        None,                    # license
        None,                    # tags_json
        None,                    # stars
        None,                    # downloads
        None,                    # last_commit
        None,                    # readme_markdown
        None,                    # latest_changelog
        None,                    # deprecated
        None,                    # python_compat_json
        None,                    # os_compat_json
        None,                    # metadata_fetched_at
        "",                      # content_hash — C# refresh 会重算
        html_url,                # html_url
        None,                    # homepage
        None,                    # language
        None,                    # forks_count
        None,                    # open_issues_count
        None,                    # release_tag
        None,                    # subscribers_count
        None,                    # created_at
    )


def _all_columns():
    """跟 _BASE_COLUMNS + _INCREMENTAL_COLUMNS 顺序一致 —— INSERT 列序。"""
    return [n for n, _ in _BASE_COLUMNS] + [n for n, _ in _INCREMENTAL_COLUMNS]


def main():
    out_path = sys.argv[1] if len(sys.argv) > 1 else "catalog-cache.db"
    out_dir = os.path.dirname(out_path)
    if out_dir and not os.path.isdir(out_dir):
        os.makedirs(out_dir, exist_ok=True)

    print(f"fetching {CATALOG_URL} ...", file=sys.stderr)
    catalog = fetch_catalog()
    custom_nodes = catalog.get("custom_nodes", [])
    print(f"got {len(custom_nodes)} entries", file=sys.stderr)

    now_iso = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    # 远期 expiry:C# 自己根据 raw_metadata.fetched 决定实际过期。
    # 这里给 1 年,够 ship 后的首次 refresh 跑完。
    expires_iso = time.strftime(
        "%Y-%m-%dT%H:%M:%SZ",
        time.gmtime(time.time() + 365 * 24 * 3600),
    )

    conn = sqlite3.connect(out_path)
    ensure_schema(conn)

    cols = _all_columns()
    placeholders = ", ".join(["?"] * len(cols))
    insert_sql = (
        f"INSERT OR REPLACE INTO catalog_cache ({', '.join(cols)}) "
        f"VALUES ({placeholders})"
    )

    written = 0
    skipped = 0
    for entry in custom_nodes:
        try:
            row = _entry_to_row(entry, now_iso, expires_iso)
        except ValueError as e:
            skipped += 1
            print(f"  [skip] {e}", file=sys.stderr)
            continue
        try:
            conn.execute(insert_sql, row)
            written += 1
        except sqlite3.Error as e:
            # 单行写失败不 abort 整批,继续下一行。
            skipped += 1
            print(f"  [skip] insert failed: {e}", file=sys.stderr)
    conn.commit()
    conn.close()
    print(
        f"wrote {written} rows ({skipped} skipped) → {out_path}",
        file=sys.stderr,
    )


if __name__ == "__main__":
    main()