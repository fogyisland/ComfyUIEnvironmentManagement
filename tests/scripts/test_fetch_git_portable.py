"""fetch_git_portable:幂等 + urlretrieve mock。"""
from pathlib import Path
from unittest.mock import patch, MagicMock
import scripts.fetch_git_portable as fgp


def test_fetch_skips_when_version_matches(tmp_path: Path):
    bin_dir = tmp_path / "bin" / "git-portable"
    bin_dir.mkdir(parents=True)
    (bin_dir / "VERSION").write_text(fgp.GIT_VERSION)
    assert fgp.fetch(tmp_path) == 0


def test_fetch_downloads_when_missing(tmp_path: Path):
    """mock urlretrieve 和 subprocess.run 验证下载流程。"""
    with patch.object(fgp.urllib.request, "urlretrieve") as mock_dl, \
         patch.object(fgp.subprocess, "run") as mock_run:
        mock_run.return_value = MagicMock(returncode=0)
        fgp.fetch(tmp_path)
        mock_dl.assert_called_once()
        mock_run.assert_called_once()
        # VERSION 写出来了
        vf = tmp_path / "bin" / "git-portable" / "VERSION"
        assert vf.read_text() == fgp.GIT_VERSION
