#!/usr/bin/env python3
"""一次性脚本:用用户的 GitHub token 拉 catalog_cache 里所有 github repo 的最近
10 个 release,写入 catalog_cache.latest_version 列 + node_versions 表。
这样 v0.6.4 详情面板立刻显示版本号和历史下拉,
不用等用户点 app 内「刷新」全量拉(5846 节点 ~10-15 分钟)。

并发 10,直接用 aiohttp。"""
import sqlite3
import json
import re
import os
import sys
import time
import asyncio
import aiohttp

DB_PATH = r"D:/ToolDevelop/ComfyUI/src-wpf/ComfyUI.Manager/bin/Debug/net8.0-windows/data/catalog-cache.db"
SETTINGS_PATH = r"C:/Users/徐鹏/AppData/Roaming/ComfyUI-Manager/settings.json"
MAX_VERSIONS = 10

# GitHub URL 正则
RE_GH = re.compile(r'^https?://github\.com/([^/]+)/([^/]+?)(?:\.git)?/?$', re.I)

def ensure_schema(conn):
    """确保 node_versions 表 + 索引存在(对应 C# 端 CatalogCacheStore 的 schema)"""
    conn.executescript("""
        CREATE TABLE IF NOT EXISTS node_versions (
            node_id TEXT NOT NULL,
            tag_name TEXT NOT NULL,
            published_at TEXT NOT NULL,
            is_prerelease INTEGER NOT NULL DEFAULT 0,
            fetched_at TEXT NOT NULL,
            PRIMARY KEY(node_id, tag_name)
        );
        CREATE INDEX IF NOT EXISTS idx_node_versions_node
            ON node_versions(node_id, published_at DESC);
    """)
    conn.commit()

def main():
    # 读 token
    with open(SETTINGS_PATH, encoding='utf-8') as f:
        token = json.load(f).get('github_token', '')
    if not token:
        print("ERROR: no github_token in settings.json", file=sys.stderr)
        sys.exit(1)
    print(f"using token (length={len(token)})")

    # 读 catalog,挑 GitHub repo 的行
    conn = sqlite3.connect(DB_PATH)
    ensure_schema(conn)
    rows = conn.execute(
        "SELECT id, package, raw_metadata FROM catalog_cache"
    ).fetchall()
    print(f"catalog has {len(rows)} rows")

    todo = []
    for id_, pkg, raw in rows:
        try:
            md = json.loads(raw)
        except Exception:
            continue
        ref = md.get('reference') or md.get('url') or ''
        m = RE_GH.match(ref.strip())
        if not m:
            continue
        owner, repo = m.group(1), m.group(2)
        todo.append((id_, pkg, owner, repo))

    print(f"{len(todo)} GitHub repos to fetch")

    async def fetch_all():
        sem = asyncio.Semaphore(10)
        results = []  # list of (node_id, [release dicts])
        async with aiohttp.ClientSession() as session:
            async def one(node_id, pkg, owner, repo):
                async with sem:
                    url = f"https://api.github.com/repos/{owner}/{repo}/releases?per_page={MAX_VERSIONS}"
                    try:
                        async with session.get(
                            url,
                            headers={
                                'User-Agent': 'ComfyUI-Manager-WPF',
                                'Accept': 'application/vnd.github+json',
                                'Authorization': f'Bearer {token}',
                            },
                            timeout=aiohttp.ClientTimeout(total=15),
                        ) as r:
                            if r.status == 200:
                                data = await r.json()
                                # 过滤 draft + 截前 10 个(API 已排序 published_at desc)
                                releases = []
                                for rel in data:
                                    if rel.get('draft'):
                                        continue
                                    releases.append({
                                        'tag': rel.get('tag_name', ''),
                                        'published_at': rel.get('published_at', ''),
                                        'prerelease': rel.get('prerelease', False),
                                    })
                                    if len(releases) >= MAX_VERSIONS:
                                        break
                                return (node_id, pkg, releases)
                            else:
                                return (node_id, pkg, [])
                    except Exception as e:
                        return (node_id, pkg, [])
            tasks = [one(*t) for t in todo]
            done = 0
            for fut in asyncio.as_completed(tasks):
                result = await fut
                results.append(result)
                done += 1
                if done % 100 == 0:
                    print(f"  {done}/{len(todo)}...")
        return results

    t0 = time.time()
    results = asyncio.run(fetch_all())
    elapsed = time.time() - t0
    print(f"fetched {len(results)} in {elapsed:.1f}s")

    # 写入 DB
    n_versions = 0
    n_nodes_with_versions = 0
    n_no_releases = 0
    cur = conn.cursor()
    now = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())

    for node_id, pkg, releases in results:
        if not releases:
            n_no_releases += 1
            continue
        n_nodes_with_versions += 1
        # 1) 删旧
        cur.execute("DELETE FROM node_versions WHERE node_id = ?", (node_id,))
        # 2) 插新
        for rel in releases:
            if not rel['tag']:
                continue
            cur.execute("""
                INSERT INTO node_versions
                    (node_id, tag_name, published_at, is_prerelease, fetched_at)
                VALUES (?, ?, ?, ?, ?)
            """, (node_id, rel['tag'], rel['published_at'],
                  1 if rel['prerelease'] else 0, now))
            n_versions += 1
        # 3) 更新 latest_version(取第一个非 prerelease,fallback 第一个)
        latest = next((r['tag'] for r in releases if not r['prerelease']),
                      releases[0]['tag'])
        cur.execute(
            "UPDATE catalog_cache SET latest_version = ? WHERE id = ?",
            (latest, node_id),
        )
    conn.commit()

    print(f"nodes with versions: {n_nodes_with_versions}")
    print(f"total version rows inserted: {n_versions}")
    print(f"nodes with no releases: {n_no_releases}")
    conn.close()

if __name__ == '__main__':
    main()
