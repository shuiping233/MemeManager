#!/usr/bin/env python3
"""拼接 Release 正文：changelog + 选包表格。

changelog 由 mikepenz/release-changelog-builder-action 的 outputFile 产出,
表格模板为 .github/workflows/ReleaseSelect.md, 其中 {{TAG}} / {{APPX_VERSION}} 会被替换.

用法:
    python scripts/compose_release_body.py \\
        --tag v1.2.3 \\
        --changelog .release-changelog.md \\
        --output release-body.md

本地只验证表格时可省略 --changelog:
    python scripts/compose_release_body.py --tag v1.13.14 --output body.md
"""

from __future__ import annotations

import argparse
from pathlib import Path

SELECTION_FILE = Path(__file__).resolve().parent.parent / ".github" / "workflows" / "ReleaseSelect.md"


def appx_version(tag: str) -> str:
    """由 tag 派生 MSIX 四段版本, 与 release.yml 的 Compute AppxPackageVersion 保持一致."""
    version = tag[1:] if tag.startswith("v") else tag
    return version if version.count(".") >= 3 else f"{version}.0"


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--tag", required=True, help="发布 tag, 如 v1.2.3")
    parser.add_argument("--changelog", default="", help="changelog 文件路径; 省略则只输出选包表格")
    parser.add_argument("--selection", default=str(SELECTION_FILE), help="选包表格模板路径")
    parser.add_argument("--output", default="release-body.md", help="输出文件路径")
    return parser.parse_args(argv)


def read_file(path: str, *, required: bool) -> str:
    file = Path(path)
    if not file.is_file():
        if required:
            raise SystemExit(f"错误: 文件不存在: {path}")
        return ""
    return file.read_text(encoding="utf-8")


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    if not args.tag.startswith("v"):
        raise SystemExit(f"错误: tag 需形如 v1.2.3, 实际为 {args.tag}")

    selection = (
        read_file(args.selection, required=True)
        .replace("{{TAG}}", args.tag)
        .replace("{{APPX_VERSION}}", appx_version(args.tag))
        .rstrip()
    )
    changelog = read_file(args.changelog, required=bool(args.changelog)).rstrip() if args.changelog else ""

    body = "\n\n".join(part for part in (changelog, selection) if part) + "\n"
    if "{{" in body:
        print("警告: 输出中仍存在未替换的占位符", flush=True)

    output = Path(args.output)
    output.write_text(body, encoding="utf-8", newline="\n")
    print(f"已写入 {output} ({len(body)} 字符, {body.count(chr(10))} 行)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
