
# TargetPriceUpsideRanging.py

# target DB만 사용해서 upside 기준으로 매수후보 선별하는 .py 만들어줘

# find_target_candidates.py

import duckdb

DB_PATH = r"C:\Users\tien7\AppData\Local\quant\quant.duckdb"

MIN_UPSIDE = 20.0
MIN_REPORT_COUNT = 3
TOP_N = 50


def main():
    conn = duckdb.connect(DB_PATH)

    # sql = f"""
    # SELECT
    #     ticker,
    #     name,
    #     report_count,
    #     cur_price,
    #     avg_tgt,
    #     upside
    # FROM target_price_stocks
    # WHERE
    #     report_count >= {MIN_REPORT_COUNT}
    #     AND avg_tgt > cur_price
    #     AND upside >= {MIN_UPSIDE}
    # ORDER BY
    #     upside DESC,
    #     report_count DESC
    # LIMIT {TOP_N}
    # """

    #이렇게 하면
    #     upside 80%, 리포트 1개
    # 보다
    #     upside 40%, 리포트 25개
    # 를 더 높게 평가할 수 있습니다.

    sql1 = """
    SELECT
        ticker,
        name,
        report_count,
        cur_price,
        avg_tgt,
        upside,

        (
            upside * 0.7
            + LN(report_count + 1) * 10
        ) AS score

    FROM target_price_stocks

    WHERE
        report_count >= 3
        AND upside > 0

    ORDER BY score DESC
    LIMIT 50
    """

    sql = """
    SELECT
        ticker,
        name,
        report_count,
        cur_price,
        avg_tgt,
        upside,

        upside * SQRT(report_count) AS score

    FROM target_price_stocks

    WHERE
        report_count >= 5
        AND upside >= 15
        AND cur_price > 1000

    ORDER BY
        upside DESC,
        report_count DESC

    LIMIT 50
    """
#upside * SQRT(report_count) AS score
#upside * LN(report_count + 1) AS score

    # 추가로 Quant 프로젝트라면 아래 필터를 같이 넣는 것을 권장합니다.
    # report_count >= 5
    # upside >= 15
    # avg_tgt > cur_price

    # SELECT
    #     ticker,
    #     name,
    #     report_count,
    #     cur_price,
    #     avg_tgt,
    #     upside
    # FROM target_price_stocks
    # WHERE
    #     report_count >= 5
    #     AND upside >= 15
    # ORDER BY
    #     upside DESC,
    #     report_count DESC;


    rows = conn.execute(sql).fetchall()

    print()
    print("=" * 90)
    print("목표주가 기반 매수후보")
    print("=" * 90)
    print(
        f"{'Score':>8}"
        f"{'Rpt':>4} "
        f"{'Upside':>8} "
        f"{'Ticker':8} "
        f"{'Name':20} "
        f"{'Price':>12} "
        f"{'Target':>12} "
    )
    print("-" * 90)

    for ticker, name, rpt, price, target, upside, score in rows:
        print(
            f"{score:8.1f} "
            f"{rpt:4d} "
            f"{upside:7.1f}% "
            f"{ticker:8} "
            f"{name[:20]:20} "
            f"{price:12,.0f} "
            f"{target:12,.0f} "
        )

    print("-" * 90)
    print(f"총 {len(rows)} 종목")

    conn.close()


if __name__ == "__main__":
    main()




# 2026-05-27
# ==========================================================================================
# 목표주가 기반 매수후보
# ==========================================================================================
#    Score Rpt   Upside Ticker   Name                        Price       Target 
# ------------------------------------------------------------------------------------------
#    405.0    8   143.2% 241590   화승엔터프라이즈                    3,965        9,642 
#    475.4   17   115.3% 462870   시프트업                       29,500       63,509 
#    439.2   16   109.8% 253450   스튜디오드래곤                    27,950       58,643 
#    410.8   14   109.8% 067160   SOOP                       53,200      111,609 
#    244.8    5   109.5% 148150   세경하이테크                      4,305        9,021 
#    449.0   17   108.9% 035760   CJ ENM                     41,500       86,696 
#    376.2   12   108.6% 036420   콘텐트리중앙                      6,380       13,308 
#    233.0    5   104.2% 352480   씨앤씨인터내셔널                   21,200       43,280 
#    459.2   21   100.2% 122870   와이지엔터테인먼트                  48,200       96,481 
#    386.5   15    99.8% 214450   파마리서치                     305,000      609,519 
#    408.2   17    99.0% 376300   디어유                        27,150       54,037 
#    279.4    8    98.8% 034120   SBS                        14,060       27,953 
#    242.7    8    85.8% 251970   펌텍코리아                      39,250       72,942 
#    359.1   20    80.3% 000100   유한양행                       88,300      159,188 
#    191.1    6    78.0% 006040   동원산업                       37,500       66,761 
#    216.1    8    76.4% 039130   하나투어                       38,050       67,125 
#    263.3   12    76.0% 272450   진에어                         6,220       10,947 
#    353.2   24    72.1% 035720   카카오                        41,850       72,029 
#    345.3   23    72.0% 041510   에스엠                        88,200      151,729 
#    172.7    6    70.5% 033500   동성화인텍                      22,850       38,950 
#    321.7   21    70.2% 251270   넷마블                        43,400       73,886 
#    156.3    5    69.9% 196170   알테오젠                      364,500      619,400 
#    166.1    6    67.8% 439260   DAEHAN SHIPBUILDING        70,200      117,784 
#    164.1    6    67.0% 003220   대원제약                        9,520       15,895 
#    250.3   14    66.9% 034230   파라다이스                      14,700       24,539 
#    187.5    8    66.3% 051500   CJ프레시웨이                    24,850       41,319 
#    324.3   24    66.2% 352820   하이브                       235,000      390,554 
#    144.9    5    64.8% 388210   씨엠티엑스                     122,600      202,000 
#    153.8    6    62.8% 377300   카카오페이                      50,000       81,404 
#    279.5   20    62.5% 010120   LS ELECTRIC               281,000      456,686 
#    152.4    6    62.2% 030520   한글과컴퓨터                     20,700       33,567 
#    194.5   10    61.5% 078340   컴투스                        28,950       46,758 
#    203.6   11    61.4% 170900   동아에스티                      41,900       67,620 
#    259.6   18    61.2% 103140   풍산                         88,000      141,842 
#    214.2   13    59.4% 105630   한세실업                        9,450       15,066 
#    290.5   25    58.1% 035420   NAVER                     203,000      320,845 
#    141.8    6    57.9% 069080   웹젠                         11,150       17,609 
#    223.1   15    57.6% 069620   대웅제약                      136,200      214,598 
#    259.4   21    56.6% 259960   크래프톤                      271,000      424,309 
#    192.3   12    55.5% 114090   GKL                        11,730       18,244 
#    196.5   13    54.5% 000080   하이트진로                      16,930       26,164 
#    254.7   22    54.3% 035900   JYP Ent.                   61,600       95,063 
#    179.4   11    54.1% 009240   한샘                         32,900       50,685 
#    247.5   21    54.0% 326030   SK바이오팜                     97,800      150,629 
#    149.6    8    52.9% 145720   덴티움                        51,100       78,151 
#    166.7   10    52.7% 257720   실리콘투                       38,800       59,253 
#    191.6   14    51.2% 293490   카카오게임즈                     10,640       16,085 
#    114.0    5    51.0% 097520   엠씨넥스                       23,200       35,029 
#    190.1   14    50.8% 095660   네오위즈                       20,750       31,297 
#    168.5   11    50.8% 073240   금호타이어                       4,975        7,504 
# ------------------------------------------------------------------------------------------
# 총 50 종목