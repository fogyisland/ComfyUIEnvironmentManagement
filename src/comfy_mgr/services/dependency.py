"""DepService:依赖解析(requirements.txt + pyproject.toml) + 本地冲突检测。

冲突规则(spec §4.2):
  - 同 env 内不同 package 对同一 dep_name 有不兼容 SpecifierSet
  - 用 packaging.requirements.SpecifierSet 判定
"""
from __future__ import annotations
import re
import sqlite3
import sys
import uuid
from datetime import datetime
from pathlib import Path

if sys.version_info >= (3, 11):
    import tomllib as _toml
else:
    import tomli as _toml  # type: ignore

from packaging.requirements import Requirement
from packaging.specifiers import SpecifierSet
from packaging.version import Version

from comfy_mgr.db.dep_repo import DepRepo
from comfy_mgr.db.scanned_node_repo import ScannedNodeRepo
from comfy_mgr.infra.event_bus import EventBus
from comfy_mgr.infra.compat_http_client import CompatHTTPClient
from comfy_mgr.result import Result, ServiceError


_REQ_LINE = re.compile(r"^\s*([A-Za-z0-9_.\-]+)\s*([<>=!~].*)?\s*$")


class DepService:
    def __init__(
        self,
        *,
        dep_repo: DepRepo,
        scanned_repo: ScannedNodeRepo,
        conn: sqlite3.Connection,
        bus: EventBus,
        compat_client: CompatHTTPClient | None,
    ):
        self.dep_repo = dep_repo
        self.scanned_repo = scanned_repo
        self.conn = conn
        self.bus = bus
        self.compat_client = compat_client

    # ----- scan_deps -----

    def scan_deps(self, env_id: str, package: str) -> Result[list[dict]]:
        node = self.scanned_repo.get_by_env_package(env_id, package)
        if not node:
            return Result.fail(ServiceError(
                code="NODE_NOT_FOUND",
                message=f"节点 {package} 不存在"))
        pkg_path = Path(node.package_path)

        records: list[dict] = []
        now = datetime.now().isoformat(timespec="seconds")

        r = self._parse_requirements_txt(pkg_path)
        if not r.ok:
            return r
        for d in r.value:
            records.append({
                "id": f"dr-{uuid.uuid4().hex[:8]}",
                "env_id": env_id, "package": package,
                "source": "requirements_txt",
                "dep_name": d["dep_name"],
                "dep_version_spec": d["dep_version_spec"],
                "scanned_at": now,
            })

        r = self._parse_pyproject(pkg_path)
        if not r.ok:
            return r
        for d in r.value:
            records.append({
                "id": f"dr-{uuid.uuid4().hex[:8]}",
                "env_id": env_id, "package": package,
                "source": "pyproject_toml",
                "dep_name": d["dep_name"],
                "dep_version_spec": d["dep_version_spec"],
                "scanned_at": now,
            })

        # 清旧 + upsert 新
        self.dep_repo.delete_by_package(env_id, package)
        for rec in records:
            self.dep_repo.upsert(rec)
        self.bus.emit("depsChanged", env_id, package)
        return Result.ok(records)

    def list_deps(self, env_id: str,
                  package: str | None = None) -> Result[list[dict]]:
        if package:
            return Result.ok(self.dep_repo.list_by_env_and_package(env_id, package))
        return Result.ok(self.dep_repo.list_by_env(env_id))

    # ----- detect_conflicts (本地) -----

    def detect_conflicts(self, env_id: str) -> Result[list[dict]]:
        """Spec §4.2:同 env 内不同 package 对同一 dep_name 不兼容。"""
        rows = self.dep_repo.list_by_env(env_id)
        by_dep: dict[str, list[dict]] = {}
        for row in rows:
            by_dep.setdefault(row["dep_name"], []).append(row)

        conflicts: list[dict] = []
        for dep_name, entries in by_dep.items():
            if len(entries) < 2:
                continue
            for i in range(len(entries)):
                for j in range(i + 1, len(entries)):
                    if self._is_incompatible(
                        entries[i]["dep_version_spec"],
                        entries[j]["dep_version_spec"],
                    ):
                        conflicts.append({
                            "id": f"cf-{uuid.uuid4().hex[:8]}",
                            "env_id": env_id,
                            "conflict_type": "local_dep_version",
                            "node_ids": sorted({
                                entries[i]["package"],
                                entries[j]["package"],
                            }),
                            "detail": {
                                "dep_name": dep_name,
                                "spec_a": entries[i]["dep_version_spec"],
                                "spec_b": entries[j]["dep_version_spec"],
                            },
                            "detected_at": datetime.now().isoformat(timespec="seconds"),
                            "resolved_at": None,
                            "ignored": 0,
                        })
        return Result.ok(conflicts)

    # ----- check_global (外部 hook,M3 base_url 空时跳过) -----

    def check_global(self, env_id: str) -> Result[list[dict]]:
        if not self.compat_client or not self.compat_client.base_url:
            return Result.ok([])
        rows = self.dep_repo.list_by_env(env_id)
        deps = [
            {"name": r["dep_name"], "spec": r["dep_version_spec"]}
            for r in rows
        ]
        if not deps:
            return Result.ok([])
        r = self.compat_client.check_known_incompat(deps)
        if not r.ok:
            # 外部 API 不可达 → 降级,返回空列表(不抛错给 UI)
            return Result.ok([])
        # 把 API 返回的 incompat 转成 conflict dict
        conflicts = []
        for inc in r.value:
            conflicts.append({
                "id": f"cf-{uuid.uuid4().hex[:8]}",
                "env_id": env_id,
                "conflict_type": "global_dep_known_incompat",
                "node_ids": inc.get("node_ids", []),
                "detail": inc,
                "detected_at": datetime.now().isoformat(timespec="seconds"),
                "resolved_at": None,
                "ignored": 0,
            })
        return Result.ok(conflicts)

    # ----- parsers (public for unit tests) -----

    def _parse_requirements_txt(self, pkg_path: Path) -> Result[list[dict]]:
        f = pkg_path / "requirements.txt"
        if not f.exists():
            return Result.ok([])
        try:
            lines = f.read_text(encoding="utf-8", errors="ignore").splitlines()
        except Exception as e:
            return Result.fail(ServiceError(
                code="DEP_PARSE_FAILED", message=str(e)))
        deps: list[dict] = []
        for raw in lines:
            line = raw.strip()
            if not line or line.startswith("#") or line.startswith("-r"):
                continue
            m = _REQ_LINE.match(line)
            if not m:
                continue
            name, spec = m.group(1), (m.group(2) or "").strip()
            deps.append({"dep_name": name, "dep_version_spec": spec or None})
        return Result.ok(deps)

    def _parse_pyproject(self, pkg_path: Path) -> Result[list[dict]]:
        f = pkg_path / "pyproject.toml"
        if not f.exists():
            return Result.ok([])
        try:
            data = _toml.loads(f.read_text(encoding="utf-8"))
        except Exception:
            return Result.ok([])  # 容忍解析失败,继续其它源
        deps_raw = data.get("project", {}).get("dependencies", []) or []
        deps: list[dict] = []
        for line in deps_raw:
            try:
                req = Requirement(line)
            except Exception:
                continue
            # 保留原始 specifier 顺序(避免 packaging 重排)
            spec_text = line.split(";", 1)[0]
            if "==" in spec_text or ">=" in spec_text or "<=" in spec_text \
                    or ">" in spec_text or "<" in spec_text \
                    or "~=" in spec_text or "!=" in spec_text:
                # 提取包名后的 specifier 部分
                head = spec_text.split(req.name, 1)
                spec_part = head[1].strip() if len(head) == 2 else ""
            else:
                spec_part = ""
            deps.append({
                "dep_name": req.name,
                "dep_version_spec": spec_part or None,
            })
        return Result.ok(deps)

    # ----- helpers -----

    @staticmethod
    def _is_incompatible(spec_a: str | None, spec_b: str | None) -> bool:
        if not spec_a or not spec_b:
            return False
        try:
            a = SpecifierSet(spec_a)
            b = SpecifierSet(spec_b)
        except Exception:
            return False
        if not a and not b:
            return False
        # 探测多个候选版本:边界版本 + 极值 (0.0.0 / 9999...)
        # 单一边界版本无法捕捉 "<2.0" vs "<3.0" 这类上界范围
        probes = {"0.0.0", "9999.9999.9999"}
        for s in list(a) + list(b):
            probes.add(s.version)
        for v in probes:
            try:
                if Version(v) in a and Version(v) in b:
                    return False
            except Exception:
                continue
        return True
