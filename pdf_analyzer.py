"""
pdf_analyzer.py - 증권사 PDF 목표주가 추출기 (Phase 1)
============================================================
실행 방법
    python pdf_analyzer.py
        # pending 전체 처리

    python pdf_analyzer.py --force
        # 전체 재분석 (done/failed 포함)

    python pdf_analyzer.py --only-missing-tp
        # target_price가 없는 건만 재분석 --- done이지만 tp 추출 실패한 건 재시도 가능

    python pdf_analyzer.py --id 1542
        # 특정 pdf_reports.id 1건만 처리

    python pdf_analyzer.py --limit 50
        # 최대 50건만 처리

    python pdf_analyzer.py --only-missing-tp --limit 50
        # target_price 없는 건 중 최대 50건만 처리

    python pdf_analyzer.py --check
        # 상태 요약만 출력 (분석 미실행)

    python pdf_analyzer.py --db-path <path>
        # DB 경로 지정


pip install pymupdf


pool.... D:/Trabajo/ai/quant/data/pdf

============================================================
"""

import argparse
import os
import re
import sys
import time
from datetime import date
from pathlib import Path

import duckdb

DB_PATH = Path(
    os.environ.get(
        "QUANT_DB",
        Path.home() / "AppData" / "Local" / "quant" / "quant.duckdb",
    )
)

DELAY_PER_PDF = 0.1
BATCH_LOG = 25

TARGET_PRICE_PATTERNS: list[re.Pattern] = [
    re.compile(r"목표\s*주\s*가\s*[（(]?원?[）)]?\s*[:：]?\s*[▲▼→]?\s*([\d,]+)\s*(?:원)?", re.IGNORECASE),
    re.compile(r"목표\s*가\s*[:：]?\s*[▲▼→]?\s*([\d,]+)\s*(?:원)?", re.IGNORECASE),
    re.compile(r"적\s*정\s*주\s*가\s*[:：]?\s*[▲▼→]?\s*([\d,]+)\s*(?:원)?", re.IGNORECASE),
    re.compile(r"\bTP\s*[:：]?\s*[▲▼→₩]?\s*([\d,]+)", re.IGNORECASE),
    re.compile(r"Target\s+Price\s*[:：]?\s*[▲▼→₩]?\s*([\d,]+)", re.IGNORECASE),
    re.compile(r"목표\s+[▲▼→]?\s*([\d,]+)\s*원", re.IGNORECASE),
]

TP_MIN = 1_000
TP_MAX = 9_999_999


def log(msg: str):
    print(f"[{date.today().isoformat()} {time.strftime('%H:%M:%S')}] {msg}", flush=True)


def open_db(db_path: Path) -> duckdb.DuckDBPyConnection:
    if not db_path.exists():
        log(f"[ERROR] DB 파일 없음: {db_path}")
        sys.exit(1)
    return duckdb.connect(str(db_path))


def extract_first_page_text(filepath: str) -> str | None:
    try:
        import pdfplumber
        with pdfplumber.open(filepath) as pdf:
            if not pdf.pages:
                return ""
            return pdf.pages[0].extract_text() or ""
    except ImportError:
        log("[ERROR] pdfplumber 미설치: pip install pdfplumber")
        sys.exit(1)
    except Exception as e:
        log(f"  [ERR] 텍스트 추출 실패: {filepath}  {e}")
        return None


def parse_target_price(text: str) -> float | None:
    for pattern in TARGET_PRICE_PATTERNS:
        for raw in pattern.findall(text):
            try:
                value = float(raw.replace(",", ""))
                if TP_MIN <= value <= TP_MAX:
                    return value
            except ValueError:
                continue
    return None


def is_mirae_report(filepath: str) -> bool:
    return "미래에셋증권" in (filepath or "")

def parse_target_price_mirae_ocr(filepath: str) -> float | None:
    """
    디지털 텍스트 레이어 완전 유실 대응: PDF 1페이지 이미지화 후 OCR 수치 파싱
    """
    try:
        from pdf2image import convert_from_path
        import pytesseract
        import re
    except ImportError:
        return None

    tp_min, tp_max = 10000, 1000000

    try:
        # 1페이지를 고해상도 이미지(300 DPI)로 변환
        pages = convert_from_path(filepath, dpi=300, first_page=1, last_page=1)
        if not pages:
            return None
            
        img1 = pages[0]
        
        # 전역 OCR 분석 또는 특정 바운딩 박스(목표주가 표 영역) 크롭 후 분석 가능
        # 한국어 팩 수급 상태에 따라 'kor', 'eng' 지정
        ocr_text = pytesseract.image_to_string(img1, lang="kor+eng")
        flat_text = "".join(ocr_text.split())
        
        # 금액 추출 정규식 작동
        match = re.search(r"목표주가[^0-9]{0,20}([\d,]+)원", flat_text)
        if match:
            value = float(match.group(1).replace(",", ""))
            if tp_min <= value <= tp_max:
                return value
    except Exception:
        return None
    return None

def parse_target_price_mirae_fallback_fitz(filepath: str) -> float | None:
    """
    pdfplumber 텍스트 추출 불능 시 PyMuPDF(fitz) 라이브러리를 통한 인코딩 우회 파싱
    """

    log(f"      [mirae] parse_target_price_mirae_fallback_fitz: {filepath}")

    try:
        import fitz  # PyMuPDF
        import re
    except ImportError:
        log("      [mirae] PyMuPDF(fitz)가 설치되어 있지 않습니다. 'pip install pymupdf'를 실행하세요.")
        return None

    log(f"      [mirae] parse_target_price_mirae_fallback_fitz: PyMuPDF 임포트 성공")
    tp_min, tp_max = 10000, 1000000

    try:
        doc = fitz.open(filepath)
        if len(doc) > 0:
            # 1페이지 텍스트 추출 시도
            page1 = doc[0]
            text1 = page1.get_text()
            log(f"      [mirae] text1({len(text1.strip())}): {text1}")
            if text1 and len(text1.strip()) > 100:  # 파편화가 아닌 정상 텍스트 확보 확인
                flat_text = "".join(text1.split())
                
                # 패턴 매칭 순회
                match = re.search(r"목표주가\([^)]+\)(?:매수|보유|매도)?([\d,]+)원", flat_text)
                if match:
                    return float(match.group(1).replace(",", ""))
                    
                match_txt = re.search(r"목표주가를(\d+)만원", flat_text)
                if match_txt:
                    return float(match_txt.group(1)) * 10000
    except Exception:
        return None
    return None

def parse_target_price_mirae_from_pdf(filepath: str) -> float | None:
    """
    미래에셋증권 리포트 전용 (2페이지 텍스트 불능 대응판):
    1페이지 개행 구조 파괴 및 수치 결합 정규식 적용
    """
    try:
        import pdfplumber
        import re
    except ImportError:
        return None

    tp_min, tp_max = 10000, 1000000 

    try:
        with pdfplumber.open(filepath) as pdf:
            # 1페이지 텍스트 추출
            page1 = pdf.pages[0]
            log(f"      [mirae] page1: {page1.page_number}  width={page1.width}  height={page1.height}  chars={len(page1.chars):,}")
            text1 = page1.extract_text()
            log(f"      [mirae] text1: {text1}")

            if not text1:
                return None
                
            # 공백 및 개행 제거 후 단일 스트림 텍스트로 압축
            flat_text = "".join(text1.split())
            
            # 패턴 1: '목표주가(상향)매수480,000원' 또는 '목표주가(유지)매수480,000원' 대응
            # '원' 직전의 숫자 군집을 캡처
            match1 = re.search(r"목표주가\([^)]+\)(?:매수|보유|매도)?([\d,]+)원", flat_text)
            if match1:
                value = float(match1.group(1).replace(",", ""))
                if tp_min <= value <= tp_max:
                    return value
                    
            # 패턴 2: 본문 내 "목표주가를 48만원으로 상향" 대응 (만원 단위 처리)
            match2 = re.search(r"목표주가를(\d+)만원", flat_text)
            if match2:
                value = float(match2.group(1)) * 10000
                if tp_min <= value <= tp_max:
                    return value

            # 패턴 3: 일반 Fallback 규칙 (목표주가 단어 뒤 20자 내 최초 등장 숫자)
            match3 = re.search(r"목표주가.{0,20}?([\d,]+)", flat_text)
            if match3:
                value = float(match3.group(1).replace(",", ""))
                if tp_min <= value <= tp_max:
                    return value

    except Exception:
        return None

    return None

def parse_target_price_mirae_from_pdf___(filepath: str) -> float | None:
    """
    미래에셋증권 리포트 전용 수정본:
    표 추출(extract_tables) 실패 대응형 텍스트 라인 파싱 로직
    """
    try:
        import pdfplumber
        import re
    except ImportError:
        return None

    # 사용자 환경 상수 설정 가정
    tp_min, tp_max = 10000, 1000000 

    try:
        with pdfplumber.open(filepath) as pdf:
            log(f"      [mirae] len(pdf.pages): {int(len(pdf.pages)):,}")
            if len(pdf.pages) < 2:
                return None
                
            page2 = pdf.pages[1]
            log(f"      [mirae] page2: {page2.page_number}  width={page2.width}  height={page2.height}  chars={len(page2.chars):,}")
            text2 = page2.extract_text()
            log(f"      [mirae] text2: {text2}")
            
            if text2:
                log("      [mirae] 2페이지 텍스트 기반 라인 분석 시작")
                lines = text2.split("\n")
                
                # 2페이지 내부를 줄바꿈 단위로 순회
                for line in lines:
                    # 공백 제거 및 정형화
                    cleaned_line = line.replace(" ", "").replace('"', '').replace("'", "")
                    log(f"      [mirae] cleaning line: {line} -> {cleaned_line}")
                    # '목표가(보정)' 또는 '목표가' 키워드가 포함된 라인 선별
                    if "목표가(보정)" in cleaned_line or "목표가" in cleaned_line:
                        # 해당 라인에서 숫자와 쉼표 조합 추출
                        match = re.search(r"([\d,]+)", line)
                        if match:
                            value = float(match.group(1).replace(",", ""))
                            if tp_min <= value <= tp_max:
                                log(f"      [mirae] 2페이지 텍스트 라인에서 추출 성공: {int(value):,}")
                                return value

            # 1페이지 우선순위 또는 전체 페이지 fallback 규칙 적용
            log("      [mirae] 2페이지 라인 분석 실패, 1페이지 전방 탐색 체계 전환")
            page1 = pdf.pages[0]
            text1 = page1.extract_text() or ""
            # "목표주가(상향)" 문구 뒤 개행을 고려한 와일드카드 매칭
            match_p1 = re.search(r"목표주가(?:[\s\S]{0,50}?)([\d,]+)\s*원", text1)
            if match_p1:
                value = float(match_p1.group(1).replace(",", ""))
                if tp_min <= value <= tp_max:
                    log(f"      [mirae] 1페이지 텍스트에서 추출 성공: {int(value):,}")
                    return value

    except Exception as e:
        log(f"      [mirae] 예외 발생: {str(e)}")
        return None

    log("      [mirae] 전용 로직 최종 추출 실패")
    return None

def load_pending(
    conn: duckdb.DuckDBPyConnection,
    force: bool,
    only_missing_tp: bool,
    report_id: int | None,
    limit: int | None,
) -> list[tuple[int, str]]:
    if report_id is not None:
        where = f"WHERE id = {report_id}"
    elif only_missing_tp:
        # target_price가 없는 항목을 찾되, 
        # force가 아니면 이미 실패/스킵/완료(검증완료)된 것은 제외
        where = "WHERE target_price IS NULL"
        if not force:
            where += " AND analyze_status NOT IN ('failed', 'skip', 'done')"
    else:
        where = "" if force else "WHERE analyze_status = 'pending'"

    lim = f"LIMIT {limit}" if limit else ""
    return conn.execute(
        f"SELECT id, filepath FROM pdf_reports {where} ORDER BY date DESC {lim}"
    ).fetchall()


def update_result(conn: duckdb.DuckDBPyConnection, id_: int, target_price: float | None, status: str):
    conn.execute(
        """UPDATE pdf_reports
           SET target_price   = ?,
               analyze_status = ?
           WHERE id = ?""",
        [target_price, status, id_],
    )


def format_target_price(value: float | None) -> str:
    if value is None:
        return "-"
    return f"{int(value):,}"


def stock_name_from_filepath(filepath: str) -> str:
    try:
        return Path(filepath).stem
    except Exception:
        return filepath or "(unknown)"


def run_analyzer(
    conn: duckdb.DuckDBPyConnection,
    force: bool,
    only_missing_tp: bool,
    report_id: int | None,
    limit: int | None,
):
    rows = load_pending(conn, force, only_missing_tp, report_id, limit)
    if not rows:
        log("분석 대상 없음")
        return

    log(f"분석 시작: {len(rows)}건  (force={force}, only_missing_tp={only_missing_tp}, id={report_id})")

    done = failed = skipped = 0
    found_tp = 0

    for i, (id_, filepath) in enumerate(rows, 1):
        stock = stock_name_from_filepath(filepath)

        if not filepath or not Path(filepath).exists():
            update_result(conn, id_, None, "failed")
            failed += 1
            if i % BATCH_LOG == 0:
                log(f"  진행: {i}/{len(rows)}  done={done}  skip={skipped}  fail={failed}  tp추출={found_tp}")
            continue

        text = extract_first_page_text(filepath)

        if text is None:
            update_result(conn, id_, None, "failed")
            failed += 1
            if i % BATCH_LOG == 0 or i == len(rows):
                log(f"  진행: {i}/{len(rows)}  done={done}  skip={skipped}  fail={failed}  tp추출={found_tp}")
            continue
        elif text.strip() == "":
            update_result(conn, id_, None, "skip")
            skipped += 1
            if i % BATCH_LOG == 0 or i == len(rows):
                log(f"  진행: {i}/{len(rows)}  done={done}  skip={skipped}  fail={failed}  tp추출={found_tp}")
            continue
        else:
            tp = parse_target_price(text)
            # [mirae] 전용 로직 비활성화 (기능 디버깅을 위해 코드는 유지)
            # if tp is None and is_mirae_report(filepath):
            #     log(f"    [{i}/{len(rows)}] {stock}  일반 정규식 미추출 -> 미래에셋 전용 로직(pdfplumber) 재시도")
            #     tp = parse_target_price_mirae_from_pdf(filepath)
            #     
            #     if tp is None:
            #         log(f"    [{i}/{len(rows)}] {stock}  pdfplumber 추출 실패 -> fitz(PyMuPDF) fallback 시도")
            #         tp = parse_target_price_mirae_fallback_fitz(filepath)

            update_result(conn, id_, tp, "done")
            done += 1
            if tp is not None:
                found_tp += 1
                log(f"    [{i}/{len(rows)}] {stock}  status=done  target_price={format_target_price(tp)}")

        if i % BATCH_LOG == 0 or i == len(rows):
            log(f"  진행: {i}/{len(rows)}  done={done}  skip={skipped}  fail={failed}  tp추출={found_tp}")

        time.sleep(DELAY_PER_PDF)

    log(f"분석 완료  done={done}  skip={skipped}  fail={failed}  목표주가추출={found_tp}/{done}")


def run_check(conn: duckdb.DuckDBPyConnection):
    rows = conn.execute(
        """
        SELECT
            analyze_status,
            COUNT(*) AS cnt,
            COUNT(target_price) AS has_tp,
            COUNT(*) - COUNT(target_price) AS no_tp
        FROM pdf_reports
        GROUP BY analyze_status
        ORDER BY analyze_status
        """
    ).fetchall()

    total = conn.execute("SELECT COUNT(*) FROM pdf_reports").fetchone()[0]
    log(f"=== pdf_reports 분석 현황 (총 {total}건) ===")
    for status, cnt, has_tp, no_tp in rows:
        status_str = (status or 'NULL').lower()
        log(f"  {status_str:10s} : {cnt:5d}건 (TP 있음: {has_tp:4d} / TP 없음: {no_tp:4d})")

    # 실질적 분석 가능 대상 요약 (TP NULL & 유효 상태)
    candidates = conn.execute(
        "SELECT COUNT(*) FROM pdf_reports WHERE target_price IS NULL AND analyze_status NOT IN ('failed', 'skip', 'done')"
    ).fetchone()[0]
    log(f"\n  => 실질적 분석 대상 (TP NULL & 유효): {candidates}건")


def parse_args():
    p = argparse.ArgumentParser(description="증권사 PDF 목표주가 추출기 (Phase 1)")
    p.add_argument("--db-path", dest="db_path", default=None, help="DuckDB 파일 경로")
    p.add_argument("--force", action="store_true", help="done/failed 포함 전체 재분석")
    p.add_argument("--only-missing-tp", action="store_true", help="target_price IS NULL 인 건만 재분석")
    p.add_argument("--check", action="store_true", help="상태 요약만 출력")
    p.add_argument("--id", type=int, default=None, help="특정 pdf_reports.id 1건만 분석")
    p.add_argument("--limit", type=int, default=None, help="최대 처리 건수")
    return p.parse_args()


def main():
    global DB_PATH
    args = parse_args()

    if args.db_path:
        DB_PATH = Path(args.db_path).expanduser()

    conn = open_db(DB_PATH)
    try:
        if args.check:
            run_check(conn)
        else:
            run_analyzer(
                conn,
                force=args.force,
                only_missing_tp=args.only_missing_tp,
                report_id=args.id,
                limit=args.limit,
            )
    finally:
        conn.close()


if __name__ == "__main__":
    main()
