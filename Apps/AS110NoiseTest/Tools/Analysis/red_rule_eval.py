# -*- coding: utf-8 -*-
"""
赤判定(矩形波的な変動)の規則を、人が色付けした Excel と xdt 波形で検証するスクリプト。

使い方:
  python Tools/Analysis/red_rule_eval.py [データフォルダ] [--gallery 出力png]

- データフォルダ既定: 学習データ/(リポジトリ外、.gitignore 対象)。直下の「xlsx を持つフォルダ」をデータセットとして全部読む
- Excel の塗りつぶし FFFF0000 を「赤」、値 > 1.39(条件付き書式)を「黄」、それ以外を「無色」として読む
- xdt の CH3 制御圧力(V×100 → kPa)から特徴量を計算し、規則の一致率と食い違い一覧を出す
詳細は docs/赤判定規則メモ.md
"""
import sys, os, re, glob, struct, csv
import numpy as np

# ---------- xdt ----------
def read_xdt(path):
    b = open(path, 'rb').read()
    n_hdr = struct.unpack_from('<I', b, 0x68)[0]
    for off in range(0x1000, len(b) - 16, 4):
        n, c, z = struct.unpack_from('<III', b, off)
        if n == n_hdr and 1 <= c <= 16 and z == 0:
            idx = struct.unpack_from('<%dI' % c, b, off + 12)
            if tuple(idx) == tuple(range(c)):
                start = off + 12 + 4 * c
                return np.frombuffer(b, dtype='<f4', count=n * c, offset=start).reshape(n, c)
    raise ValueError('data block not found: ' + path)

def control_pressure_kpa(path):
    return read_xdt(path)[:, 2].astype(float) * 100.0

# 規則(設定値)。C# 版 Analysis/NoiseJudgeSettings.cs と合わせる
RULE = dict(lfhf_min=7.8, run_thr_kpa=0.4, run_min_samples=15, step_d_min=0.35, step_t_min=6.0, yellow_pp_min=1.4)
QUANT_MARGIN = 0.01   # 0.1 kPa 刻みのデータに対する「以上」判定の丸め余裕 [kPa]

# ---------- 特徴量 ----------
def moving_median(x, w):
    from numpy.lib.stride_tricks import sliding_window_view
    pad = w // 2
    xp = np.pad(x, (pad, w - 1 - pad), mode='edge')
    return np.median(sliding_window_view(xp, w), axis=1)

def longest_run(mask):
    best = cur = 0
    for m in mask:
        cur = cur + 1 if m else 0
        best = max(best, cur)
    return best

def features(x, dt=0.02):
    """x: 制御圧力 [kPa]。戻り値は dict。"""
    f = {}
    med = np.median(x)
    f['pp'] = float(x.max() - x.min())
    # (A) 台地: 中央値から ±thr 以上離れた生サンプルが連続する最長長さ [サンプル]
    #     データは 0.1 kPa 刻みなので「以上」を丸め余裕 QUANT_MARGIN 付きで判定する
    #     (float 丸めでちょうど ±thr のサンプルがどちらに転ぶかが実装依存にならないように)
    for thr in (0.3, 0.4, 0.5):
        f[f'run{thr}'] = max(longest_run(x - med >= thr - QUANT_MARGIN), longest_run(med - x >= thr - QUANT_MARGIN))
    # (B) 低周波/高周波パワー比(白色ノイズなら ≈1〜3、矩形変動があると大きい)
    x0 = x - x.mean()
    X = np.abs(np.fft.rfft(x0 * np.hanning(len(x0)))) ** 2
    fr = np.fft.rfftfreq(len(x0), d=dt)
    lf = X[(fr >= 0.1) & (fr < 2)].mean()
    hf = X[(fr >= 5) & (fr < 25)].mean()
    f['lfhf'] = float(lf / hf)
    # (C) 1 回きりの段差: 隣接する 2 秒区間(100 サンプル)の平均の差 d と、
    #     d を区間内ばらつきで割った t 値(段の鋭さ)。step_d = |d| の最大、
    #     step_t = |d| が RULE['step_d_min'] 以上の点の中での t の最大(そういう点がなければ 0)
    from numpy.lib.stride_tricks import sliding_window_view
    w = 100
    W = sliding_window_view(x, w); m = W.mean(axis=1); s = W.std(axis=1)
    d = m[w:] - m[:-w]; sp = np.sqrt((s[w:] ** 2 + s[:-w] ** 2) / w) + 1e-9
    t = np.abs(d / sp); big = np.abs(d) >= RULE['step_d_min']
    f['step_d'] = float(np.abs(d).max()); f['step_t'] = float(t[big].max()) if big.any() else 0.0
    # 参考: 0.18 s 移動中央値が説明する分散の割合、2サンプル自己相関、移動中央値の最大偏差
    m9 = moving_median(x, 9)
    f['r2_9'] = float(1 - np.var(x - m9) / x0.var())
    f['ac2'] = float((x0[:-2] * x0[2:]).mean() / x0.var())
    f['dev9'] = float(np.abs(m9 - med).max())
    return f

def judge(f, rule=RULE):
    red = (f['lfhf'] >= rule['lfhf_min']) \
        or (f[f"run{rule['run_thr_kpa']}"] >= rule['run_min_samples']) \
        or (f['step_t'] >= rule['step_t_min'])   # step_t は |d| >= step_d_min の点だけから計算している
    if red: return 'red'
    # LOGGER の表示分解能 0.1 kPa に丸めてから比較(人は表示値で判定していた。1.3999 を 1.4 と読む)
    return 'yellow' if round(f['pp'], 1) >= rule['yellow_pp_min'] else 'none'

# ---------- Excel ラベル ----------
ROWMAP = {
    'SN60226': 'SN60226', 'SN60231': 'SN60231', 'SN60254': 'SN60254', 'SN60255': 'SN60255',
    'SN60257': 'SN60257', 'SN60258': 'SN60258', 'SN60261': 'SN60261', 'SN60263': 'SN60263',
    'SN60256': 'SN60256/1_返却時', 'SN60256再調整後': 'SN60256/2_再調整後', 'SN60256フレキASSY交換後': 'SN60256/3_フレキASSY交換後',
    'SN60259': 'SN60259/1_返却時', 'SN60259再調整後': 'SN60259/2_再調整後', 'SN60259フレキASSY交換後': 'SN60259/3_フレキASSY交換後', 'SN60259元に戻した': 'SN60259/4_元に戻した',
    'SN60260': 'SN60260/1_返却時', 'SN60260再調整後': 'SN60260/2_再調整後', 'SN60260フレキASSY交換後': 'SN60260/3_フレキASSY交換後',
    'SN60262': 'SN60262/1_返却時', 'SN60262再調整後': 'SN60262/2_再調整後', 'SN60262フレキASSY交換後': 'SN60262/3_フレキASSY交換後',
    'SN60262ノズル交換後': 'SN60262/4_ノズル交換後', 'SN60262マグネット交換後': 'SN60262/5_マグネット交換', 'SN60262開発在庫に交換': 'SN60262/6_開発在庫に交換',
}

# Excel の色付けを本人確認で訂正したもの(データセット名, フォルダ, mA) -> 正しいラベル
HUMAN_OVERRIDES = {
    ('2026.06.12_不具合返却品', 'SN60256/3_フレキASSY交換後', 10): 'none',    # 2026-11-03 本人確認: 赤にしない
    ('2026.06.12_不具合返却品', 'SN60256/3_フレキASSY交換後', 27): 'none',
}

def excel_labels(xlsx, dataset_dir):
    """行名 -> フォルダ: ROWMAP にあればそれ、なければデータセット直下の同名フォルダ。"""
    import openpyxl
    ws = openpyxl.load_workbook(xlsx, data_only=True).worksheets[0]
    out = {}
    for r in range(3, ws.max_row + 1):
        name = ws.cell(r, 2).value
        if not name: continue
        if name in ROWMAP: folder = ROWMAP[name]
        elif os.path.isdir(os.path.join(dataset_dir, str(name))): folder = str(name)
        else: continue
        for col in range(3, 27):          # C..Z = 7..30 mA
            c = ws.cell(r, col)
            if c.value is None: continue
            red = bool(c.fill.fill_type) and c.fill.fgColor.type == 'rgb' and c.fill.fgColor.rgb == 'FFFF0000'
            lab = 'red' if red else ('yellow' if c.value > 1.39 else 'none')
            out[(folder, 7 + col - 3)] = (float(c.value), lab)
    return out

def collect(root):
    """root 直下の各データセット(xlsx を 1 つ持つフォルダ)を読む。root 自身が xlsx を持てばそれ 1 つ。"""
    datasets = [root] if glob.glob(os.path.join(root, '*.xlsx')) else sorted(
        d for d in glob.glob(os.path.join(root, '*')) if os.path.isdir(d) and glob.glob(os.path.join(d, '*.xlsx')))
    rows = []
    for ds in datasets:
        dsname = os.path.basename(ds)
        xlsx = [p for p in glob.glob(os.path.join(ds, '*.xlsx')) if not os.path.basename(p).startswith('~$')][0]
        labels = excel_labels(xlsx, ds)
        for p in glob.glob(os.path.join(ds, '**', '*.xdt'), recursive=True):
            rel = os.path.relpath(p, ds).replace('\\', '/')
            m = re.search(r'_(\d+)mA_', os.path.basename(p))
            if not m: continue
            key = (os.path.dirname(rel), int(m.group(1)))
            if key not in labels: continue
            human = HUMAN_OVERRIDES.get((dsname, key[0], key[1]), labels[key][1])
            f = features(control_pressure_kpa(p))
            rows.append(dict(dataset=dsname, folder=key[0], mA=key[1], excel_pp=labels[key][0], human=human, judged=judge(f), path=p, **f))
    rows.sort(key=lambda r: (r['dataset'], r['folder'], r['mA']))
    return rows

def main():
    argv = sys.argv[1:]
    gallery = None
    if '--gallery' in argv:
        i = argv.index('--gallery'); gallery = argv[i + 1]; del argv[i:i + 2]
    root = argv[0] if argv else os.path.join(os.path.dirname(__file__), '..', '..', '学習データ')
    rows = collect(root)
    out_csv = os.path.join(os.path.dirname(__file__), 'red_rule_scores.csv')
    with open(out_csv, 'w', newline='', encoding='utf-8-sig') as fh:
        w = csv.DictWriter(fh, [k for k in rows[0] if k != 'path']); w.writeheader()
        for r in rows: w.writerow({k: v for k, v in r.items() if k != 'path'})
    from collections import Counter
    for ds in sorted(set(r['dataset'] for r in rows)) + ['(全体)']:
        sub = [r for r in rows if ds == '(全体)' or r['dataset'] == ds]
        red_h = np.array([r['human'] == 'red' for r in sub]); red_j = np.array([r['judged'] == 'red' for r in sub])
        print(f"[{ds}] 人の判定: {dict(Counter(r['human'] for r in sub))} 件数 {len(sub)} | "
              f"赤の一致 {int((red_h == red_j).sum())}/{len(sub)} 見逃し={int((red_h & ~red_j).sum())} 過検出={int((~red_h & red_j).sum())}")
    print('人の判定と食い違う例:')
    diff = [r for r in rows if (r['human'] == 'red') != (r['judged'] == 'red')]
    for r in diff:
        print(f"  人={r['human']:6s} 規則={r['judged']:6s} {r['dataset']} {r['folder']} {r['mA']}mA pp={r['excel_pp']} lfhf={r['lfhf']:.2f} run0.4={r['run0.4']} step_d={r['step_d']:.2f} step_t={r['step_t']:.1f}")
    ydiff = [r for r in rows if r['human'] != 'red' and r['human'] != r['judged']]
    print(f"黄/無色の食い違い: {len(ydiff)} 件")
    for r in ydiff:
        print(f"  人={r['human']:6s} 規則={r['judged']:6s} {r['dataset']} {r['folder']} {r['mA']}mA Excel={r['excel_pp']} 計算pp={r['pp']:.3f}")
    print('scores ->', out_csv)
    if gallery:
        import matplotlib; matplotlib.use('Agg'); import matplotlib.pyplot as plt
        plt.rcParams['font.family'] = 'Yu Gothic'
        # 食い違い + 規則の境界に近いもの(lfhf 5〜11 または run0.4 10〜16)
        near = [r for r in rows if r not in diff and (5 <= r['lfhf'] <= 11 or 10 <= r['run0.4'] <= 16 or (r['step_d'] >= 0.28 and r['step_t'] >= 4))]
        sel = diff + near
        fig, axes = plt.subplots(len(sel), 1, figsize=(22, 2.3 * len(sel)), squeeze=False)
        for ax, r in zip(axes[:, 0], sel):
            x = control_pressure_kpa(r['path']); t = np.arange(len(x)) * 0.02; med = np.median(x)
            ax.plot(t, x - med, lw=0.6, color='k'); ax.plot(t, moving_median(x, 9) - med, lw=1.2, color='tab:red')
            ax.axhline(RULE['run_thr_kpa'], color='gray', ls='--', lw=.7); ax.axhline(-RULE['run_thr_kpa'], color='gray', ls='--', lw=.7)
            ax.set_ylim(-1.4, 1.4); ax.set_xlim(0, t[-1]); ax.grid(alpha=.3)
            tag = '【食い違い】' if r in diff else ''
            ax.set_title(f"{tag}{r['dataset']} {r['folder']} {r['mA']}mA  人={r['human']}  規則={r['judged']}  pp={r['excel_pp']}  lfhf={r['lfhf']:.1f}  run0.4={r['run0.4']}  step={r['step_d']:.2f}kPa/t={r['step_t']:.1f}",
                         color='red' if r['human'] == 'red' else ('goldenrod' if r['human'] == 'yellow' else 'gray'), fontsize=10)
        fig.tight_layout(); fig.savefig(gallery, dpi=65); print('gallery ->', gallery)

if __name__ == '__main__':
    main()
