import os
import re
from datetime import datetime
from html import escape
from pathlib import Path
from urllib.parse import quote


NAVER_RESEARCH_LIST_URL = "https://finance.naver.com/research/company_list.naver"

BASE_STYLES = """    body { font-family: Segoe UI, Arial, sans-serif; margin: 24px; line-height: 1.5; }
    .page-header { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; margin-bottom: 24px; }
    .page-header h1 { margin: 0; }
    .action-button {
      display: inline-block;
      padding: 8px 14px;
      border-radius: 8px;
      background: #0b57d0;
      color: #fff;
      font-weight: 600;
      text-decoration: none;
    }
    .action-button:hover { text-decoration: none; background: #0847aa; }
    section { margin-bottom: 28px; padding-bottom: 20px; border-bottom: 1px solid #d9d9d9; }
    h2 { margin: 0 0 10px; font-size: 20px; }
    ul { margin: 0; padding-left: 20px; }
    li { margin: 4px 0; }
    a { color: #0b57d0; text-decoration: none; }
    a:hover { text-decoration: underline; }
    .empty { color: #666; }"""

HEADER_HTML = f"""  <div class="page-header">
    <h1>PDF Activity Log</h1>
    <a class="action-button" href="{NAVER_RESEARCH_LIST_URL}" target="_blank" rel="noopener noreferrer">check new report</a>
  </div>"""

HTML_TEMPLATE = f"""<!DOCTYPE html>
<html lang="ko">
<head>
  <meta charset="utf-8">
  <title>PDF Activity Log</title>
  <style>
{BASE_STYLES}
  </style>
</head>
<body>
{HEADER_HTML}
  <!-- entries -->
</body>
</html>
"""


def format_section_title(action: str, count: int, timestamp: datetime | None = None) -> str:
    now = timestamp or datetime.now()
    am_pm = now.strftime("%p")
    hour = now.hour % 12 or 12
    return f"{now:%Y-%m-%d} {am_pm} {hour}:{now:%M}  {action}: {count}files."


def path_to_href(path: Path, report_path: Path) -> str:
    relative = os.path.relpath(path, start=report_path.parent)
    return quote(relative.replace("\\", "/"), safe="/._-()")


def build_section(action: str, files: list[Path], report_path: Path) -> str:
    title = escape(format_section_title(action, len(files)))
    if files:
        items = "\n".join(
            f'    <li><a href="{path_to_href(file_path, report_path)}">{escape(file_path.name)}</a></li>'
            for file_path in files
        )
        content = f"  <ul>\n{items}\n  </ul>"
    else:
        content = '  <p class="empty">No files.</p>'

    return f"<section>\n  <h2>{title}</h2>\n{content}\n</section>\n"


def ensure_report_styles(html: str) -> str:
    if ".page-header" in html and ".action-button" in html:
        return html

    style_pattern = re.compile(r"<style>\s*.*?</style>", re.DOTALL)
    replacement = f"<style>\n{BASE_STYLES}\n  </style>"
    if style_pattern.search(html):
        return style_pattern.sub(replacement, html, count=1)

    return html


def ensure_html_header(html: str) -> str:
    body_pattern = re.compile(r"(<body>\s*)(.*?)(\s*<!-- entries -->)", re.DOTALL)
    if body_pattern.search(html):
        return body_pattern.sub(r"\1" + HEADER_HTML + r"\3", html, count=1)

    header_pattern = re.compile(r'<div class="page-header">.*?</div>|<h1>PDF Activity Log</h1>', re.DOTALL)
    if header_pattern.search(html):
        return header_pattern.sub(HEADER_HTML, html, count=1)
    return html


def read_or_default(report_path: Path, default_content: str) -> str:
    if report_path.exists():
        return report_path.read_text(encoding="utf-8")
    return default_content


def write_report(report_path: Path, html: str, action: str, files: list[Path]) -> None:
    section = build_section(action, files, report_path)
    marker = "  <!-- entries -->"
    if marker in html:
        html = html.replace(marker, f"{marker}\n{section}", 1)
    else:
        html = html.replace("</body>", f"{section}</body>", 1)

    report_path.write_text(html, encoding="utf-8")


def append_html_report(report_path: Path, action: str, files: list[Path]) -> None:
    report_path.parent.mkdir(parents=True, exist_ok=True)

    html = read_or_default(report_path, HTML_TEMPLATE)
    html = ensure_report_styles(html)
    html = ensure_html_header(html)
    write_report(report_path, html, action, files)
