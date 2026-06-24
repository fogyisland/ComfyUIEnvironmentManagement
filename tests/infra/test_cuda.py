import pytest
from unittest.mock import MagicMock
from comfy_mgr.infra.cuda import CudaDetector
from comfy_mgr.result import Result


NVSMI_OUTPUT = """\
Sun Jun 21 21:11:31 2026
+-----------------------------------------------------------------------------------------+
| NVIDIA-SMI 596.36                 Driver Version: 596.36         CUDA Version: 13.2     |
+-----------------------------------------+------------------------+----------------------+
| GPU  Name                  Persistence-M | Bus-Id          Disp.A | Volatile Uncorr. ECC |
|  0   NVIDIA GeForce RTX 4060 ...  WDDM  | 00000000:01:00.0  On |                  N/A |
+-----------------------------------------+------------------------+----------------------+
"""


def test_detect_parses_nvidia_smi(mocker):
    mock_run = mocker.patch("comfy_mgr.infra.cuda.subprocess.run")
    mock_run.return_value = MagicMock(returncode=0, stdout=NVSMI_OUTPUT, stderr="")
    result = CudaDetector.detect()
    assert result.ok
    info = result.value
    assert info.driver_version == "596.36"
    assert info.max_cuda_version == "13.2"
    assert "RTX 4060" in info.gpu_name
    assert info.available is True


def test_detect_returns_unavailable_when_nvidia_smi_missing(mocker):
    mocker.patch("comfy_mgr.infra.cuda.subprocess.run", side_effect=FileNotFoundError)
    result = CudaDetector.detect()
    assert result.ok
    assert result.value.available is False
    assert result.value.driver_version is None


def test_detect_returns_fail_on_subprocess_error(mocker):
    mock_run = mocker.patch("comfy_mgr.infra.cuda.subprocess.run")
    mock_run.return_value = MagicMock(returncode=1, stderr="error")
    result = CudaDetector.detect()
    assert not result.ok
    assert result.error.code == "CUDA_DETECT_FAILED"


def test_suggest_cu_version_for_cuda_13_driver():
    from comfy_mgr.infra.cuda import CudaInfo
    info = CudaInfo(driver_version="596.36", max_cuda_version="13.2", gpu_name="RTX 4060", available=True)
    suggestions = CudaDetector.suggest_cu_version(info)
    assert "cu124" in suggestions  # 默认推荐
    assert suggestions[0] == "cu124"  # 第一个是推荐


def test_suggest_cu_version_for_no_gpu():
    from comfy_mgr.infra.cuda import CudaInfo
    info = CudaInfo(driver_version=None, max_cuda_version=None, gpu_name="", available=False)
    suggestions = CudaDetector.suggest_cu_version(info)
    assert suggestions == ["cpu"]


def test_suggest_cu_version_for_cuda_11_driver():
    from comfy_mgr.infra.cuda import CudaInfo
    info = CudaInfo(driver_version="470.0", max_cuda_version="11.4", gpu_name="GTX 1080", available=True)
    suggestions = CudaDetector.suggest_cu_version(info)
    assert "cu118" in suggestions

