from __future__ import annotations
import re
import subprocess
from dataclasses import dataclass
from comfy_mgr.result import Result, ServiceError


@dataclass
class CudaInfo:
    driver_version: str | None
    max_cuda_version: str | None
    gpu_name: str | None
    available: bool


class CudaDetector:
    """通过 nvidia-smi 检测 CUDA 环境。"""

    @staticmethod
    def detect() -> Result[CudaInfo]:
        try:
            result = subprocess.run(
                ["nvidia-smi"],
                capture_output=True,
                text=True,
                timeout=10,
            )
        except FileNotFoundError:
            return Result.ok(CudaInfo(None, None, None, False))
        except Exception as e:
            return Result.fail(ServiceError(
                code="CUDA_DETECT_FAILED",
                message=str(e),
            ))
        if result.returncode != 0:
            return Result.fail(ServiceError(
                code="CUDA_DETECT_FAILED",
                message=result.stderr.strip() or "nvidia-smi 返回非零",
            ))
        return Result.ok(CudaDetector._parse(result.stdout))

    @staticmethod
    def _parse(output: str) -> CudaInfo:
        driver_match = re.search(r"Driver Version:\s*([\d.]+)", output)
        cuda_match = re.search(r"CUDA Version:\s*([\d.]+)", output)
        gpu_match = re.search(r"\|\s+\d+\s+(NVIDIA[^\|]+?)\s*\|", output)
        return CudaInfo(
            driver_version=driver_match.group(1) if driver_match else None,
            max_cuda_version=cuda_match.group(1) if cuda_match else None,
            gpu_name=gpu_match.group(1).strip() if gpu_match else None,
            available=True,
        )

    @staticmethod
    def suggest_cu_version(info: CudaInfo) -> list[str]:
        """根据驱动 CUDA 版本推荐 cu 索引。返回有序列表（推荐项在前）。"""
        if not info.available or not info.max_cuda_version:
            return ["cpu"]
        try:
            major = int(info.max_cuda_version.split(".")[0])
        except (ValueError, IndexError):
            return ["cu124", "cu118", "cpu"]
        if major >= 12:
            return ["cu124", "cu126", "cu118", "cpu"]
        if major >= 11:
            return ["cu118", "cu124", "cpu"]
        return ["cu118", "cpu"]
