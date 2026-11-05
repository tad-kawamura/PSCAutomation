# -*- coding: utf-8 -*-
"""雛形 Excel(雛形/AS110ノイズ測定_雛形.xlsx)を生成する。

学習データの Excel(NF弁ノイズ確認: SN 行 × mA 列の制御圧力 PP 値、右側に平均値、下に平均値・標準偏差)を
参考にして、アプリが自動記入しやすく、日をまたいで追記しても壊れない形にしたもの。

    python Tools/ExcelTemplate/make_template.py            # 雛形/AS110ノイズ測定_雛形.xlsx を上書き生成
    python Tools/ExcelTemplate/make_template.py 出力.xlsx  # 別名で出力

レイアウトの説明は 雛形/README.md を参照。
"""
import sys
from pathlib import Path

from openpyxl import Workbook
from openpyxl.formatting.rule import CellIsRule, FormulaRule
from openpyxl.styles import Alignment, Border, Font, PatternFill, Side
from openpyxl.utils import get_column_letter
from openpyxl.workbook.defined_name import DefinedName
from openpyxl.worksheet.properties import PageSetupProperties

# ---- レイアウト定数(アプリ側と合わせる。変えたら 雛形/README.md も直す) ----
FONT_NAME = "游ゴシック"
MA_MIN, MA_MAX = 1, 35                 # 電流列は 1〜35 mA 固定(上限 35mA)
MA_FIRST_COL = 5                        # E 列 = 1mA(A 測定日 / B 型式 / C SN / D 備忘録)
HEADER_ROW = 7                          # 見出し行
FIRST_DATA_ROW = 8                      # データ 1 行目
PREPARED_ROWS = 100                     # 罫線・数式を入れておく行数
LAST_FORMULA_ROW = 1007                 # 集計・条件付き書式が見る最終行(追記の余裕)
SUMMARY_ROWS = {"平均値": 4, "標準偏差": 5, "最大": 6}

SHEET_PP, SHEET_MEAN, SHEET_RED, SHEET_DETAIL, SHEET_SET = "PP値", "平均値", "赤判定", "詳細", "設定"
YELLOW_CELL = f"'{SHEET_SET}'!$C$3"     # 黄しきい値のセル

# 条件付き書式(dxf)の塗りは Excel が bgColor を見るので、通常の塗りと別に両方の色を入れたものを使う
YELLOW = PatternFill("solid", fgColor="FFFF00")
RED = PatternFill("solid", fgColor="FF0000")
CF_YELLOW = PatternFill("solid", fgColor="FFFF00", bgColor="FFFF00")
CF_RED = PatternFill("solid", fgColor="FF0000", bgColor="FF0000")
GRAY = PatternFill("solid", fgColor="D9D9D9")
LIGHT = PatternFill("solid", fgColor="F2F2F2")
THIN = Side(style="thin", color="000000")
BOX = Border(left=THIN, right=THIN, top=THIN, bottom=THIN)
CENTER = Alignment(horizontal="center", vertical="center")
WRAP = Alignment(horizontal="center", vertical="center", wrap_text=True)


def col(ma: int) -> str:
    return get_column_letter(MA_FIRST_COL + ma - MA_MIN)


MA_FIRST, MA_LAST = col(MA_MIN), col(MA_MAX)      # D, AL
EXTRA = {"最大p-p": 40, "黄": 41, "赤": 42, "判定": 43, "備考": 44}   # AN..AR(PP値のみ)


def font(bold=False, size=11, color=None):
    return Font(name=FONT_NAME, size=size, bold=bold, color=color)


def set_font_all(ws):
    for row in ws.iter_rows():
        for c in row:
            if c.font.name != FONT_NAME:
                c.font = Font(name=FONT_NAME, size=c.font.sz or 11, bold=c.font.b, color=c.font.color)


def clear_legend(ws):
    for addr in ("E2", "Q2"):
        ws[addr].fill = PatternFill()
        ws[addr].value = None
    ws["F2"].value = None
    ws["R2"].value = None


def grid_frame(ws, subtitle: str, with_extra: bool):
    """PP値・平均値・赤判定で共通の枠(タイトル、凡例、集計行、見出し、罫線)を作る。"""
    ws["A1"] = "NF弁ノイズ確認"
    ws["A1"].font = font(bold=True, size=14)
    ws["E1"] = subtitle
    ws["E1"].font = font(bold=True)
    ws["A2"] = "Ps=440kPa"
    # 凡例
    ws["E2"].fill = YELLOW
    ws["E2"] = "黄"
    ws["E2"].alignment = CENTER
    ws["F2"] = f"p-p ≥ 黄しきい値({SHEET_SET}シート C3、既定 1.4 kPa)。条件付き書式で自動"
    ws["Q2"].fill = RED
    ws["Q2"] = "赤"
    ws["Q2"].alignment = CENTER
    ws["R2"] = f"矩形波的な変動。アプリが{SHEET_RED}シートに「赤」と書いた列(人が訂正するときはそのセルを消す/書く)"
    # 集計行(データ行の上に置く。下に置くと追記で位置が動くため)
    for label, r in SUMMARY_ROWS.items():
        ws.cell(r, 3, label + "(全行)").font = font(bold=True)
        for ma in range(MA_MIN, MA_MAX + 1):
            c = col(ma)
            rng = f"{c}${FIRST_DATA_ROW}:{c}${LAST_FORMULA_ROW}"
            if label == "平均値":
                f, nf = f'=IF(COUNT({rng})=0,"",AVERAGE({rng}))', "0.00"
            elif label == "標準偏差":
                f, nf = f'=IF(COUNT({rng})=0,"",_xlfn.STDEV.P({rng}))', "0.00"
            else:
                f, nf = f'=IF(COUNT({rng})=0,"",MAX({rng}))', "0.0"
            cell = ws[f"{c}{r}"]
            cell.value = f
            cell.number_format = nf
            cell.fill = LIGHT
            cell.border = BOX
            cell.alignment = CENTER
    # 見出し
    for ci, t in {1: "測定日", 2: "型式", 3: "SN", 4: "備忘録"}.items():
        ws.cell(HEADER_ROW, ci, t)
    for ma in range(MA_MIN, MA_MAX + 1):
        c = ws.cell(HEADER_ROW, MA_FIRST_COL + ma - MA_MIN, ma)
        c.number_format = '0"mA"'
    if with_extra:
        for t, ci in EXTRA.items():
            ws.cell(HEADER_ROW, ci, t)
    last_col = EXTRA["備考"] if with_extra else MA_FIRST_COL + MA_MAX - MA_MIN
    for ci in range(1, last_col + 1):
        c = ws.cell(HEADER_ROW, ci)
        c.font = font(bold=True)
        c.fill = GRAY
        c.border = BOX
        c.alignment = CENTER
    # データ行の罫線・書式
    for r in range(FIRST_DATA_ROW, FIRST_DATA_ROW + PREPARED_ROWS):
        for ci in range(1, last_col + 1):
            c = ws.cell(r, ci)
            c.border = BOX
            if ci == 1:
                c.number_format = "yyyy/m/d"
                c.alignment = CENTER
            elif MA_FIRST_COL <= ci <= MA_FIRST_COL + MA_MAX - MA_MIN:
                c.number_format = "0.0"
                c.alignment = CENTER
    # 列幅・固定・表示
    ws.column_dimensions["A"].width = 11
    ws.column_dimensions["B"].width = 12
    ws.column_dimensions["C"].width = 16
    ws.column_dimensions["D"].width = 22
    for ma in range(MA_MIN, MA_MAX + 1):
        ws.column_dimensions[col(ma)].width = 6
    ws.freeze_panes = f"{MA_FIRST}{FIRST_DATA_ROW}"
    ws.sheet_view.zoomScale = 80
    ws.page_setup.orientation = "landscape"
    ws.page_setup.fitToWidth = 1
    ws.page_setup.fitToHeight = 0
    ws.sheet_properties.pageSetUpPr = PageSetupProperties(fitToPage=True)
    ws.print_title_rows = f"{HEADER_ROW}:{HEADER_ROW}"


def link_keys(ws):
    """平均値・赤判定の A〜D 列は PP値シートの同じ行に数式で追従させる(SN の C 列が空なら空)。"""
    for r in range(FIRST_DATA_ROW, FIRST_DATA_ROW + PREPARED_ROWS):
        for ci, letter in ((1, "A"), (2, "B"), (3, "C"), (4, "D")):
            ws.cell(r, ci).value = f"=IF('{SHEET_PP}'!$C{r}=\"\",\"\",'{SHEET_PP}'!${letter}{r})"


def build_pp(ws):
    grid_frame(ws, "制御圧力 p-p [kPa](CH3 の max − min、0.1 kPa 丸め)", with_extra=True)
    y, rd = get_column_letter(EXTRA["黄"]), get_column_letter(EXTRA["赤"])
    for r in range(FIRST_DATA_ROW, FIRST_DATA_ROW + PREPARED_ROWS):
        rng = f"{MA_FIRST}{r}:{MA_LAST}{r}"
        red_rng = f"'{SHEET_RED}'!{rng}"
        ws.cell(r, EXTRA["最大p-p"]).value = f'=IF(COUNT({rng})=0,"",MAX({rng}))'
        ws.cell(r, EXTRA["最大p-p"]).number_format = "0.0"
        ws.cell(r, EXTRA["黄"]).value = (
            f'=IF(COUNT({rng})=0,"",SUMPRODUCT(ISNUMBER({rng})*({rng}>={YELLOW_CELL}-0.001)*({red_rng}<>"赤")))'
        )
        ws.cell(r, EXTRA["赤"]).value = f'=IF(COUNT({rng})=0,"",COUNTIF({red_rng},"赤"))'
        ws.cell(r, EXTRA["判定"]).value = f'=IF(COUNT({rng})=0,"",IF({rd}{r}>0,"赤",IF({y}{r}>0,"黄","OK")))'
        for key in ("最大p-p", "黄", "赤", "判定"):
            ws.cell(r, EXTRA[key]).alignment = CENTER
    for key in ("最大p-p", "黄", "赤", "判定"):
        ws.column_dimensions[get_column_letter(EXTRA[key])].width = 7
    ws.column_dimensions[get_column_letter(EXTRA["備考"])].width = 24
    # 条件付き書式: 赤(赤判定シート参照)を優先し、次に黄(しきい値以上)
    data_rng = f"{MA_FIRST}{FIRST_DATA_ROW}:{MA_LAST}{LAST_FORMULA_ROW}"
    d = f"{MA_FIRST}{FIRST_DATA_ROW}"
    ws.conditional_formatting.add(
        data_rng, FormulaRule(formula=[f"'{SHEET_RED}'!{d}=\"赤\""], fill=CF_RED, stopIfTrue=True))
    ws.conditional_formatting.add(
        data_rng, FormulaRule(formula=[f"AND(ISNUMBER({d}),{d}>={YELLOW_CELL}-0.001)"], fill=CF_YELLOW))
    j = get_column_letter(EXTRA["判定"])
    judge_rng = f"{j}{FIRST_DATA_ROW}:{j}{LAST_FORMULA_ROW}"
    ws.conditional_formatting.add(judge_rng, CellIsRule(operator="equal", formula=['"赤"'], fill=CF_RED))
    ws.conditional_formatting.add(judge_rng, CellIsRule(operator="equal", formula=['"黄"'], fill=CF_YELLOW))


def build_mean(ws):
    grid_frame(ws, "制御圧力 平均値 [kPa](CH3 の mean)", with_extra=False)
    link_keys(ws)
    clear_legend(ws)
    ws["E2"] = f"測定日・型式・SN・備忘録は {SHEET_PP} シートの同じ行に数式で追従。値はアプリが書く"
    for r in range(FIRST_DATA_ROW, FIRST_DATA_ROW + PREPARED_ROWS):
        for ma in range(MA_MIN, MA_MAX + 1):
            ws[f"{col(ma)}{r}"].number_format = "0.0"


def build_red(ws):
    grid_frame(ws, "赤判定(アプリが「赤」を書く。空白 = 赤でない)", with_extra=False)
    link_keys(ws)
    clear_legend(ws)
    ws["E2"] = ("「赤」と書いた列が PP値シートで赤くなる。人が訂正するときはこのシートのセルを消す/「赤」と書く。"
                "赤の根拠(台地/低周波/段差)は 詳細 シート")
    for r in SUMMARY_ROWS.values():          # 文字の表なので集計行は使わない
        for ma in range(MA_MIN, MA_MAX + 1):
            c = ws[f"{col(ma)}{r}"]
            c.value = None
            c.fill = PatternFill()
            c.border = Border()
        ws.cell(r, 3).value = None
    for r in range(FIRST_DATA_ROW, FIRST_DATA_ROW + PREPARED_ROWS):
        for ma in range(MA_MIN, MA_MAX + 1):
            ws[f"{col(ma)}{r}"].number_format = "General"
    rng = f"{MA_FIRST}{FIRST_DATA_ROW}:{MA_LAST}{LAST_FORMULA_ROW}"
    ws.conditional_formatting.add(rng, CellIsRule(operator="equal", formula=['"赤"'], fill=CF_RED))


DETAIL_COLUMNS = [
    ("測定日時", 18, "yyyy/m/d h:mm:ss"),
    ("型式", 12, None),
    ("SN", 14, None),
    ("備忘録", 20, None),
    ("指令電流 [mA]", 9, "0.0"),
    ("実測電流 [mA]\n(7351E、記録前)", 11, "0.000"),
    ("CH1 電流\n平均 [mA]", 9, "0.000"),
    ("CH1 電流\np-p [mA]", 9, "0.000"),
    ("CH2 供給圧力\n平均 [kPa]", 10, "0.0"),
    ("CH2 供給圧力\np-p [kPa]", 10, "0.0"),
    ("CH3 制御圧力\n平均 [kPa]", 10, "0.0"),
    ("CH3 制御圧力\np-p [kPa]", 10, "0.0"),
    ("CH3 制御圧力\n最小 [kPa]", 10, "0.0"),
    ("CH3 制御圧力\n最大 [kPa]", 10, "0.0"),
    ("CH4\n平均 [V]", 8, "0.000"),
    ("CH4\np-p [V]", 8, "0.000"),
    ("判定\n(空白/黄/赤)", 8, None),
    ("赤の根拠\n(A台地/B低周波/C段差)", 12, None),
    ("台地\n連続サンプル数", 9, "0"),
    ("低周波/高周波\nパワー比", 10, "0.0"),
    ("段差 [kPa]", 8, "0.00"),
    ("段差 t 値", 8, "0.0"),
    ("xdt ファイル名", 48, None),
]
DETAIL_JUDGE_COL = 17


def build_detail(ws):
    for i, (name, width, nf) in enumerate(DETAIL_COLUMNS, start=1):
        c = ws.cell(1, i, name)
        c.font = font(bold=True)
        c.fill = GRAY
        c.border = BOX
        c.alignment = WRAP
        ws.column_dimensions[get_column_letter(i)].width = width
        if nf:
            for r in range(2, 2 + 2000):
                ws.cell(r, i).number_format = nf
    ws.row_dimensions[1].height = 45
    ws.freeze_panes = "E2"
    ws.auto_filter.ref = f"A1:{get_column_letter(len(DETAIL_COLUMNS))}1"
    judge_col = get_column_letter(DETAIL_JUDGE_COL)
    rng = f"{judge_col}2:{judge_col}5000"
    ws.conditional_formatting.add(rng, CellIsRule(operator="equal", formula=['"赤"'], fill=CF_RED))
    ws.conditional_formatting.add(rng, CellIsRule(operator="equal", formula=['"黄"'], fill=CF_YELLOW))
    ws.sheet_view.zoomScale = 90


SETTINGS = [
    # (区分, 項目, 値, 説明)  行 3〜8。C3 は PP値シートの黄の条件付き書式・黄の数が参照する
    ("黄", "p-p しきい値 [kPa]", 1.4, "0.1 kPa に丸めた p-p がこれ以上なら黄(PP値シートの色付け・黄の数が参照)"),
    ("赤 (A) 台地", "中央値からの偏差 [kPa]", 0.4, "中央値からこれ以上離れた生サンプルが連続すると赤(丸め余裕 0.01)"),
    ("赤 (A) 台地", "連続サンプル数", 15, "20 ms × 15 = 0.3 秒"),
    ("赤 (B) 低周波構造", "低周波/高周波パワー比", 7.8, "0.1〜2 Hz の平均パワー ÷ 5〜25 Hz の平均パワー(ハニング窓)"),
    ("赤 (C) 段差", "段差 [kPa]", 0.35, "隣接する 2 秒区間(100 点)の平均の差"),
    ("赤 (C) 段差", "t 値", 6.0, "段差 ÷ 区間内ばらつき"),
]
CONDITIONS = [
    ("供給圧力 Ps [kPa]", 440),
    ("サンプリング周期 [ms]", 20),
    ("サンプル数", 2000),
    ("記録時間 [s]", 40),
    ("電流の許容 [mA]", 0.1),
    ("電流の上限 [mA]", 35),
    ("電流を合わせてから記録開始までの待ち [s]", 1),
    ("雛形 xdt", "雛形/AS110ノイズ測定用.xdt"),
]
NOTES = [
    "・PP値・平均値・赤判定の 3 シートは同じ行 = 同じ型式・SN・備忘録。アプリは PP値シートの A〜D に書き、他の 2 シートは数式で追従する",
    "・電流の列は 1〜35 mA 固定(見出しは数値、表示だけ \"mA\" 付き)。アプリは見出しの値と一致する列に書く",
    "・新しい測定は、SN 列が空の最初の行に追記する(日をまたいでも同じブックに追記できる)",
    "・黄は条件付き書式で自動(C3 のしきい値)。赤はアプリが赤判定シートに「赤」と書いた列。人が訂正するときは赤判定シートのセルを消す/書く",
    "・最大p-p・黄の数・赤の数・判定(OK/黄/赤)は数式。備考は人が書く欄",
    "・詳細シートは 1 測定 1 行で、全チャンネルの平均・p-p と赤の根拠(台地・低周波比・段差)を残す",
    "・101 行目以降が必要なときは上の行をコピーする(数式・罫線を引き継ぐ。条件付き書式は 1007 行目まで有効)",
    "・この表の設定値はアプリが実行時に使った値を書き込む欄(既定値は docs/赤判定規則メモ.md と Analysis/NoiseJudgeSettings.cs)",
]


def build_settings(ws):
    ws["A1"] = "判定・測定の設定値"
    ws["A1"].font = font(bold=True, size=14)
    for ci, t in enumerate(("区分", "項目", "値", "説明"), start=1):
        c = ws.cell(2, ci, t)
        c.font = font(bold=True)
        c.fill = GRAY
        c.border = BOX
        c.alignment = CENTER
    for i, (k, item, v, desc) in enumerate(SETTINGS, start=3):
        for ci, val in enumerate((k, item, v, desc), start=1):
            c = ws.cell(i, ci, val)
            c.border = BOX
        ws.cell(i, 3).alignment = CENTER
    base = 3 + len(SETTINGS) + 1          # 10
    ws.cell(base, 1, "測定条件").font = font(bold=True)
    for i, (item, v) in enumerate(CONDITIONS, start=base + 1):
        ws.cell(i, 2, item).border = BOX
        c = ws.cell(i, 3, v)
        c.border = BOX
        c.alignment = Alignment(horizontal="left" if isinstance(v, str) else "center")
    nbase = base + len(CONDITIONS) + 2
    ws.cell(nbase, 1, "使い方").font = font(bold=True)
    for i, t in enumerate(NOTES, start=nbase + 1):
        ws.cell(i, 1, t)
    ws.column_dimensions["A"].width = 18
    ws.column_dimensions["B"].width = 38
    ws.column_dimensions["C"].width = 30
    ws.column_dimensions["D"].width = 70
    return base + 1  # 測定条件の先頭行(名前定義用)


def add_names(wb, cond_first_row: int):
    names = {
        "PP_Header": f"'{SHEET_PP}'!${MA_FIRST}${HEADER_ROW}:${MA_LAST}${HEADER_ROW}",
        "PP_FirstRow": f"'{SHEET_PP}'!$A${FIRST_DATA_ROW}",
        "Mean_Header": f"'{SHEET_MEAN}'!${MA_FIRST}${HEADER_ROW}:${MA_LAST}${HEADER_ROW}",
        "Mean_FirstRow": f"'{SHEET_MEAN}'!$A${FIRST_DATA_ROW}",
        "Red_Header": f"'{SHEET_RED}'!${MA_FIRST}${HEADER_ROW}:${MA_LAST}${HEADER_ROW}",
        "Red_FirstRow": f"'{SHEET_RED}'!$A${FIRST_DATA_ROW}",
        "Detail_Header": f"'{SHEET_DETAIL}'!$A$1",
        "Setting_YellowPp": f"'{SHEET_SET}'!$C$3",
        "Setting_PlateauKpa": f"'{SHEET_SET}'!$C$4",
        "Setting_PlateauSamples": f"'{SHEET_SET}'!$C$5",
        "Setting_LowHighRatio": f"'{SHEET_SET}'!$C$6",
        "Setting_StepKpa": f"'{SHEET_SET}'!$C$7",
        "Setting_StepT": f"'{SHEET_SET}'!$C$8",
        "Condition_FirstRow": f"'{SHEET_SET}'!$B${cond_first_row}",
    }
    for n, ref in names.items():
        wb.defined_names[n] = DefinedName(n, attr_text=ref)


def main(out: Path):
    wb = Workbook()
    ws_pp = wb.active
    ws_pp.title = SHEET_PP
    ws_mean = wb.create_sheet(SHEET_MEAN)
    ws_red = wb.create_sheet(SHEET_RED)
    ws_detail = wb.create_sheet(SHEET_DETAIL)
    ws_set = wb.create_sheet(SHEET_SET)
    build_pp(ws_pp)
    build_mean(ws_mean)
    build_red(ws_red)
    build_detail(ws_detail)
    cond_row = build_settings(ws_set)
    add_names(wb, cond_row)
    for ws in wb.worksheets:
        set_font_all(ws)
    wb.active = 0
    out.parent.mkdir(parents=True, exist_ok=True)
    wb.save(out)
    print("wrote", out)


if __name__ == "__main__":
    root = Path(__file__).resolve().parents[2]
    target = Path(sys.argv[1]) if len(sys.argv) > 1 else root / "雛形" / "AS110ノイズ測定_雛形.xlsx"
    main(target)
