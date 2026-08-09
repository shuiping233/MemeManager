#!/usr/bin/env python3
"""把 Release 附件发布到 CNB (cnb.cool) 的版本发布页。

用法:
    python scripts/publish_cnb.py \
        --repo 组织名/仓库名 \
        --tag v1.2.3 \
        --name "MemeManager v1.2.3" \
        --body "版本说明..." \
        --asset path/to/a.zip \
        --asset path/to/b.msix \
        [--prerelease]

环境变量:
    CNB_TOKEN  必填。CNB 访问令牌，对应 API 的 `Authorization: Bearer <token>`。

流程（幂等，重复跑安全）:
    1. GET  /{repo}/-/releases/tags/{tag}   —— 查版本；已存在则 PATCH 更新，不存在则 POST 创建
    2. 对每个附件:
       POST /{repo}/-/releases/{id}/asset-upload-url   —— 申请上传 URL（asset_name/size/overwrite）
       PUT  upload_url                                  —— 上传文件内容（预签名 URL）
       POST .../asset-upload-confirmation/{token}/{path} —— 确认上传
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import urllib.error
import urllib.request
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Sequence
from urllib.parse import quote, urlparse

API_BASE = "https://api.cnb.cool"

# verify_url 形如:
#   https://api.cnb.cool/{repo}/-/releases/{release_id}/asset-upload-confirmation/{upload_token}/{asset_path}
_VERIFY_URL_RE = re.compile(r"asset-upload-confirmation/([^/]+)/(.+)$")


class CnbError(RuntimeError):
    """CNB API 调用失败（HTTP 非 2xx）。"""


@dataclass(frozen=True)
class Config:
    repo: str
    tag: str
    name: str
    body: str
    assets: Sequence[Path]
    prerelease: bool
    token: str


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--repo", required=True, help="CNB 仓库路径，格式 `组织名/仓库名`（不带 .git 后缀）")
    p.add_argument("--tag", required=True, help="标签名，如 v1.2.3")
    p.add_argument("--name", required=True, help="版本标题")
    p.add_argument("--body", default="", help="版本描述")
    p.add_argument("--asset", action="append", default=[], help="要上传的附件路径（可重复传多个）")
    p.add_argument("--prerelease", action="store_true", help="标记为预发布版本")
    return p.parse_args(argv)


def load_config(args: argparse.Namespace) -> Config:
    token = os.environ.get("CNB_TOKEN", "").strip()
    if not token:
        raise SystemExit("错误: 环境变量 CNB_TOKEN 未设置")
    assets = [Path(a) for a in args.asset]
    for a in assets:
        if not a.is_file():
            raise SystemExit(f"错误: 附件不存在: {a}")
    return Config(
        repo=args.repo,
        tag=args.tag,
        name=args.name,
        body=args.body,
        assets=assets,
        prerelease=args.prerelease,
        token=token,
    )


def _request(
    method: str,
    url: str,
    *,
    token: str,
    json_body: Any | None = None,
    data: bytes | None = None,
    headers: dict[str, str] | None = None,
    timeout: int = 60,
) -> dict[str, Any]:
    """发送 HTTP 请求并解析 JSON 响应；非 2xx 抛 CnbError。"""
    req_headers = {"Authorization": f"Bearer {token}"}
    if json_body is not None:
        req_headers["Content-Type"] = "application/json"
        data = json.dumps(json_body).encode("utf-8")
    if headers:
        req_headers.update(
            {
                "accept": "application/json"
            }
        )
        req_headers.update(headers)
    else:
        req_headers.update(
            {
                "accept": "application/json"
            }
        )

    req = urllib.request.Request(url, data=data, headers=req_headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            raw = resp.read()
            if not raw:
                return {}
            try:
                return json.loads(raw)
            except json.JSONDecodeError:
                return {"_raw": raw.decode("utf-8", errors="replace")}
    except urllib.error.HTTPError as e:
        detail = e.read().decode("utf-8", errors="replace")[:500]
        raise CnbError(f"{method} {url} -> HTTP {e.code}: {detail}") from e
    except urllib.error.URLError as e:
        raise CnbError(f"{method} {url} -> 网络错误: {e}") from e


def api_url(repo: str, path: str) -> str:
    """拼 CNB OpenAPI URL。repo 需要 URL 编码（组织/仓库 含 /）。"""
    return f"{API_BASE}/{repo}{path}"


def get_release_by_tag(cfg: Config) -> dict[str, Any] | None:
    """按 tag 查版本；404 返回 None，其它错误抛出。"""
    url = api_url(cfg.repo, "/-/releases/tags/" + quote(cfg.tag, safe=""))
    try:
        return _request("GET", url, token=cfg.token)
    except CnbError as e:
        if "HTTP 404" in str(e):
            return None
        raise


def create_release(cfg: Config) -> dict[str, Any]:
    url = api_url(cfg.repo, "/-/releases")
    body = {
        "tag_name": cfg.tag,
        "name": cfg.name,
        "body": cfg.body,
        "prerelease": cfg.prerelease,
        "draft": False,
        "make_latest": "true",
        "target_commitish": "main"
    }
    return _request("POST", url, token=cfg.token, json_body=body)


def update_release(cfg: Config, release_id: str) -> dict[str, Any]:
    url = api_url(cfg.repo, f"/-/releases/{release_id}")
    body = {
        "name": cfg.name,
        "body": cfg.body,
        "prerelease": cfg.prerelease,
        "draft": False,
        "make_latest": "true",
    }
    return _request("PATCH", url, token=cfg.token, json_body=body)


def get_upload_url(cfg: Config, release_id: str, asset: Path) -> dict[str, Any]:
    url = api_url(cfg.repo, f"/-/releases/{release_id}/asset-upload-url")
    body = {
        "asset_name": asset.name,
        "size": asset.stat().st_size,
        "overwrite": True,
    }
    return _request("POST", url, token=cfg.token, json_body=body)


def upload_file(upload_url: str, asset: Path, token: str) -> None:
    """把文件内容 PUT 到预签名 upload_url。"""
    with asset.open("rb") as f:
        data = f.read()
    _request("PUT", upload_url, token=token, data=data)


def confirm_upload(cfg: Config, release_id: str, verify_url: str) -> None:
    """解析 verify_url 中的 upload_token / asset_path，并调用确认接口。"""
    m = _VERIFY_URL_RE.search(urlparse(verify_url).path)
    if not m:
        raise CnbError(f"无法解析 verify_url: {verify_url}")
    upload_token, asset_path = m.group(1), m.group(2)
    url = api_url(cfg.repo, f"/-/releases/{release_id}/asset-upload-confirmation/{upload_token}/{asset_path}")
    _request("POST", url, token=cfg.token)


def publish_assets(cfg: Config, release_id: str) -> int:
    failed = 0
    for asset in cfg.assets:
        size_mb = asset.stat().st_size / (1024 * 1024)
        print(f"[1/3] 申请上传 URL: {asset.name} ({size_mb:.1f} MB)")
        try:
            info = get_upload_url(cfg, release_id, asset)
            upload_url = info.get("upload_url", "")
            verify_url = info.get("verify_url", "")
            if not upload_url or not verify_url:
                raise CnbError(f"响应缺少 upload_url/verify_url: {info}")
            print(f"[2/3] 上传文件内容: {asset.name}")
            upload_file(upload_url, asset, cfg.token)
            print(f"[3/3] 确认上传: {asset.name}")
            confirm_upload(cfg, release_id, verify_url)
            print(f"      ✓ {asset.name} 上传完成")
        except CnbError as e:
            failed += 1
            print(f"      ✗ {asset.name} 上传失败: {e}", file=sys.stderr)
    return failed


def main(argv: Sequence[str] | None = None) -> int:
    try:
        cfg = load_config(parse_args(argv))
    except SystemExit as e:
        print(e, file=sys.stderr)
        return 2

    try:
        print(f"目标仓库: {cfg.repo}  标签: {cfg.tag}  附件数: {len(cfg.assets)}")

        # 幂等：已存在同 tag 版本则更新，否则创建
        existing = get_release_by_tag(cfg)
        if existing:
            release_id = str(existing["id"])
            print(f"版本已存在 (id={release_id})，更新元信息…")
            update_release(cfg, release_id)
        else:
            created = create_release(cfg)
            release_id = str(created["id"])
            print(f"已创建版本 (id={release_id})")

        failed = publish_assets(cfg, release_id)
        if failed:
            print(f"完成，但 {failed} 个附件上传失败", file=sys.stderr)
            return 1
        print(f"全部完成 ✓  https://cnb.cool/{cfg.repo}/releases/tag/{cfg.tag}")
        return 0
    except CnbError as e:
        print(f"失败: {e}", file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        print("已取消", file=sys.stderr)
        return 130


if __name__ == "__main__":
    raise SystemExit(main())
