"""
obsidian_wikilink.py
────────────────────────────────────────────────────────────
DuckDB stocks.name 목록을 읽어, 지정 경로의 .md 파일에서
종목명을 [[종목명]] 형식으로 치환한다.

규칙
  - 이미 [[ ]] 안에 있는 문자열은 치환 대상에서 제외
  - 긴 이름 우선 치환 (부분 매칭 방지)
  - --dry-run 옵션으로 실제 수정 없이 미리보기 가능

사용 예
  python obsidian_wikilink.py --path "D:/Cloud/GoogleDrive/Obsidian/ceWiki"
  python obsidian_wikilink.py --path "D:/Cloud/GoogleDrive/Obsidian/ceWiki" --dry-run
  python obsidian_wikilink.py --path "D:/Cloud/GoogleDrive/Obsidian/ceWiki" --db-path "C:/Users/tien7/AppData/Local/quant/quant.duckdb"
  python obsidian_wikilink.py --path "D:/Cloud/GoogleDrive/Obsidian/ceWiki" --min-rating 5
"""

import argparse
import re
import sys
from pathlib import Path


# ──────────────────────────────────────────────────────────
#  설정 기본값
# ──────────────────────────────────────────────────────────

DEFAULT_DB = Path.home() / "AppData" / "Local" / "quant" / "quant.duckdb"
DEFAULT_VAULT = "D:/Cloud/GoogleDrive/Obsidian/ceWiki"


# ──────────────────────────────────────────────────────────
#  종목명 목록 로드
# ──────────────────────────────────────────────────────────

def load_stock_names(db_path: Path, min_rating: int) -> list[str]:
    """DuckDB에서 stocks.name 목록 반환."""
    try:
        import duckdb
    except ImportError:
        sys.exit("[ERROR] duckdb 패키지가 없습니다.  pip install duckdb")

    if not db_path.exists():
        sys.exit(f"[ERROR] DB 파일 없음: {db_path}")

    con = duckdb.connect(str(db_path), read_only=True)
    rows = con.execute(
        "SELECT name FROM stocks WHERE rating >= ? ORDER BY LENGTH(name) DESC",
        [min_rating],
    ).fetchall()
    con.close()

    names = [r[0].strip() for r in rows if r[0] and r[0].strip()]
    if not names:
        sys.exit("[ERROR] stocks 테이블에서 종목명을 찾지 못했습니다.")
    print(f"[INFO] 종목 {len(names)}개 로드 완료 (min_rating={min_rating})")
    return names


# ──────────────────────────────────────────────────────────
#  치환 로직
# ──────────────────────────────────────────────────────────
import re

# Markdown 링크: [text](url)
MD_LINK_RE = re.compile(r"\[([^\]]+)\]\([^)]+\)")

# Obsidian 위키링크: [[text]]
WIKILINK_RE = re.compile(r"\[\[.*?\]\]", re.DOTALL)


def build_pattern(names: list[str]) -> re.Pattern:
    """
    종목명 매칭 패턴.
    - names 는 길이 내림차순 정렬되어 있다고 가정
    - 긴 이름 우선 매칭
    """
    escaped = [re.escape(n) for n in names]
    return re.compile("|".join(escaped))


def convert_markdown_links(text: str) -> str:
    """
    Markdown 링크를 Obsidian 링크로 변환

    [SK하이닉스](https://...)
      -> [[SK하이닉스]]
    """

    return MD_LINK_RE.sub(
        lambda m: f"[[{m.group(1)}]]",
        text
    )

def fix_broken_links(text: str) -> str:
    return re.sub(
        r"\[\[\[([^\]]+)\]\]\]\([^)]+\)",
        r"[[\1]]",
        text
    )

def replace_in_text(text: str, pattern: re.Pattern) -> tuple[str, int]:
    """
    처리 순서:
      1. Markdown 링크 -> [[링크]]
      2. 기존 [[...]] 보호
      3. 종목명 -> [[종목명]]

    반환:
      (변환된 텍스트, 치환 횟수)
    """

    # 깨진 Markdown+Wiki 링크 복구
    # 예:
    #   [[[삼성전자]]](https://...)
    # -> [[삼성전자]]
    text = fix_broken_links(text)

    # 1. Markdown 링크 변환
    text = convert_markdown_links(text)

    # 2. 기존 위키링크 보호
    parts = WIKILINK_RE.split(text)
    links = WIKILINK_RE.findall(text)

    total_count = 0
    new_parts = []

    for part in parts:
        replaced, count = pattern.subn(
            lambda m: f"[[{m.group(0)}]]",
            part
        )

        total_count += count
        new_parts.append(replaced)

    # 3. 재조합
    result = []

    for i, part in enumerate(new_parts):
        result.append(part)

        if i < len(links):
            result.append(links[i])

    return "".join(result), total_count

# ──────────────────────────────────────────────────────────
#  파일 순회
# ──────────────────────────────────────────────────────────

def process_path(root: Path, pattern: re.Pattern, dry_run: bool):
    md_files = list(root.rglob("*.md"))
    if not md_files:
        print(f"[WARN] .md 파일이 없습니다: {root}")
        return

    total_files = 0
    total_replacements = 0

    for fpath in md_files:
        try:
            original = fpath.read_text(encoding="utf-8")
        except UnicodeDecodeError:
            try:
                original = fpath.read_text(encoding="cp949")
            except Exception as e:
                print(f"[SKIP] 인코딩 오류 {fpath}: {e}")
                continue
        except Exception as e:
            print(f"[SKIP] 읽기 오류 {fpath}: {e}")
            continue

        modified, count = replace_in_text(original, pattern)

        if modified != original:
            print(f"변경됨: {fpath}")

        if modified == original: # if count == 0:
            continue

        total_files += 1
        total_replacements += count
        rel = fpath.relative_to(root)

        if dry_run:
            print(f"[DRY] {rel}  ({count}건 예정)")
        else:
            try:
                fpath.write_text(modified, encoding="utf-8")
                print(f"[OK]  {rel}  ({count}건 치환)")
            except Exception as e:
                print(f"[ERR] 쓰기 실패 {fpath}: {e}")

    label = "예정" if dry_run else "완료"
    print(
        f"\n{'[DRY-RUN] ' if dry_run else ''}총 {total_files}개 파일 / "
        f"{total_replacements}건 치환 {label}"
    )


# ──────────────────────────────────────────────────────────
#  진입점
# ──────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(
        description="Obsidian .md 파일에서 종목명을 [[종목명]] 위키링크로 치환"
    )
    parser.add_argument(
        "--path", default=DEFAULT_VAULT,
        help=f"순회할 Obsidian vault 경로 (하위 폴더 포함) (기본: {DEFAULT_VAULT})"
    )
    parser.add_argument(
        "--db-path", default=str(DEFAULT_DB),
        help=f"DuckDB 파일 경로 (기본: {DEFAULT_DB})"
    )
    parser.add_argument(
        "--min-rating", type=int, default=1,
        help="stocks.rating 최솟값 필터 (기본: 1 = 전체)"
    )
    parser.add_argument(
        "--dry-run", action="store_true",
        help="실제 파일 수정 없이 치환 예정 건수만 출력"
    )
    args = parser.parse_args()

    root = Path(args.path)
    if not root.exists():
        sys.exit(f"[ERROR] 경로 없음: {root}")
    if not root.is_dir():
        sys.exit(f"[ERROR] 디렉터리가 아님: {root}")

    db_path = Path(args.db_path)
    names = load_stock_names(db_path, args.min_rating)
    pattern = build_pattern(names)

    print(f"[INFO] 경로: {root}")
    print(f"[INFO] dry-run: {args.dry_run}")
    process_path(root, pattern, args.dry_run)


if __name__ == "__main__":
    main()
