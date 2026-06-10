import asyncio
import json
import logging
import re
from datetime import datetime
from pathlib import Path
from urllib.parse import urljoin

from playwright.async_api import async_playwright

from html_report import append_html_report

# HTML sample
# <tr>
#     <td style="padding-left:10">
#         <a href="/item/main.naver?code=010120" title="LS ELECTRIC" class="stock_item">LS ELECTRIC</a>
#     </td>
#     <td><a href="company_read.naver?nid=91792&page=1">연중 지속되는 분기 최대 실적 기록 경신</a><img src="https://ssl.pstatic.net/imgstock/images5/ico_research_new.gif" width="8" height="8" class="ico_new" alt="NEW"></td>
#     <td>하나증권</td>
#     <td class="file">
#         <a href="https://stock.pstatic.net/stock-research/company/57/20260422_company_865183000.pdf" target="_blank"><img src="https://ssl.pstatic.net/imgstock/images5/down.gif" alt="pdf" align="absmiddle"></a>
#     </td>
#     <td class="date" style="padding-left:5px">26.04.22</td>
#     <td class="date">1644</td>
# </tr>

BASE_DIR = Path(__file__).parent
with open(BASE_DIR / "config.json", encoding="utf-8") as f:
    CONFIG = json.load(f)

SAVE_DIR = Path(CONFIG["save_dir"])
SAVE_DIR.mkdir(parents=True, exist_ok=True)

LOG_DIR = BASE_DIR / "logs"
LOG_DIR.mkdir(exist_ok=True)
logging.basicConfig(
    filename=LOG_DIR / "runtime.log",
    level=logging.INFO,
    format="%(asctime)s %(levelname)s %(message)s",
    encoding="utf-8",
)
HTML_REPORT_FILE = LOG_DIR / "activity_log.html"

HISTORY_FILE = BASE_DIR / "downloaded.json"


def load_history() -> int:
    """가장 최근에 성공적으로 처리한 최고 NID를 반환합니다."""
    if not HISTORY_FILE.exists():
        return 0

    try:
        data = json.loads(HISTORY_FILE.read_text(encoding="utf-8"))
        return int(data.get("last_nid", 0))
    except (json.JSONDecodeError, ValueError, KeyError, TypeError):
        logging.warning("history file is invalid. Starting from 0.")
        return 0


def save_history(max_nid: int, success: int, fail: int) -> None:
    """NID와 함께 추가 정보를 저장합니다."""
    history_data = {
        "last_nid": max_nid,
        "last_run": datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
        "success_count": success,
        "fail_count": fail,
        "description": "NAVER Finance report crawler history"
    }
    HISTORY_FILE.write_text(
        json.dumps(history_data, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )


def sanitize(name: str) -> str:
    cleaned = re.sub(r'[\\/:*?"<>|]', "_", name)
    cleaned = re.sub(r"\s+", " ", cleaned).strip(" .")
    return cleaned or "untitled"


def normalize_date(date_raw: str) -> str:
    date = re.sub(r"\D", "", date_raw)
    if len(date) == 6:
        return "20" + date
    return date


def make_filename(date: str, stock: str, title: str, broker: str) -> str:
    return sanitize(f"{date}_{stock}_{title}_{broker}.pdf")


async def get_report_links(page, last_max_nid: int) -> list[dict]:
    reports = []
    base = "https://finance.naver.com/research/company_list.naver"
    reached_old_reports = False

    for page_no in range(1, CONFIG.get("pages_to_scan", 3) + 1):
        await page.goto(f"{base}?page={page_no}", wait_until="domcontentloaded")
        rows = await page.query_selector_all("table.type_1 tr")

        for row in rows:
            try:
                cells = await row.query_selector_all("td")
                if len(cells) < 6:
                    continue

                stock_el = await cells[0].query_selector("a")
                title_el = await cells[1].query_selector("a")
                pdf_el = await cells[3].query_selector("a")
                if not stock_el or not title_el or not pdf_el:
                    continue

                detail_href = await title_el.get_attribute("href")
                pdf_href = await pdf_el.get_attribute("href")
                nid_match = re.search(r"nid=(\d+)", detail_href or "")
                if not nid_match or not pdf_href:
                    continue

                nid = nid_match.group(1)
                # 숫자 비교를 통해 이전 다운로드 지점 도달 확인 (누락된 ID 대응)
                if int(nid) <= last_max_nid:
                    reached_old_reports = True
                    logging.info(f"Reached existing report nid={nid}; stopping list scan.")
                    break

                reports.append(
                    {
                        "nid": nid,
                        "stock": (await stock_el.inner_text()).strip(),
                        "title": (await title_el.inner_text()).strip(),
                        "broker": (await cells[2].inner_text()).strip(),
                        "date": normalize_date((await cells[4].inner_text()).strip()),
                        "pdf_url": urljoin(base, pdf_href),
                    }
                )
            except Exception as e:
                logging.warning(f"row parsing failed: {e}")

        if reached_old_reports:
            break

    return reports


async def download_report(
    request_context, report: dict
) -> Path | None:
    # 섹터 분류를 위해 브라우저 컨텍스트를 새로 생성하던 로직 주석 처리
    # context = await browser.new_context()

    try:
        filename = make_filename(
            report["date"],
            report["stock"],
            report["title"],
            report["broker"],
        )
        dest = SAVE_DIR / filename  # 기본 저장 경로에 직접 저장

        response = await request_context.get(report["pdf_url"])
        if not response.ok:
            logging.warning(f"PDF request failed: nid={report['nid']}, url={report['pdf_url']}")
            return None

        dest.write_bytes(await response.body())
        logging.info(f"OK: {filename}")
        print(f"  - {filename}")
        return dest
    except Exception as e:
        logging.error(f"download failed nid={report['nid']}: {e}")
        return None


async def main():
    last_max_nid = load_history()
    downloaded_files: list[Path] = []
    success_count = 0
    fail_count = 0
    
    print(f"[{datetime.now().strftime('%Y-%m-%d %H:%M')}] research downloader start")
    print(f"save path: {SAVE_DIR}")
    print(f"last max nid: {last_max_nid}")

    async with async_playwright() as pw:
        browser = await pw.chromium.launch(headless=CONFIG.get("headless", True))
        page = await browser.new_page()

        print("collecting report list...")
        reports = await get_report_links(page, last_max_nid)
        await page.close()

        print(f"new reports: {len(reports)}")

        request_context = browser.contexts[0].request if browser.contexts else (await browser.new_context()).request
        for i, report in enumerate(reports, 1):
            print(
                f"[{i}/{len(reports)}] "
                f"{report['stock']} | {report['title']} | {report['broker']} | {report['date']}"
            )
            saved_path = await download_report(request_context, report)
            if saved_path is not None:
                downloaded_files.append(saved_path)
                success_count += 1
            else:
                fail_count += 1
            await asyncio.sleep(1.5)

        await browser.close()

    # 이번 회차에서 수집된 리포트 중 가장 높은 NID로 업데이트
    if reports:
        current_max_nid = max(int(r["nid"]) for r in reports)
        # 다운로드 성공한 파일이 있을 때만 업데이트하거나, 
        # 리스트 자체를 최신 기준으로 관리하려면 아래와 같이 작성
        new_checkpoint = max(last_max_nid, current_max_nid)
        save_history(new_checkpoint, success_count, fail_count)

    append_html_report(HTML_REPORT_FILE, "Download", downloaded_files)
    print("done.")


if __name__ == "__main__":
    asyncio.run(main())
