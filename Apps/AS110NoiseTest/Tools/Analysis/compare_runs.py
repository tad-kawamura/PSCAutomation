# -*- coding: utf-8 -*-
"""
同じ製品を人が測った xdt とアプリ(Claude)が測った xdt を電流ごとに並べて比較するスクリプト。

使い方(リポジトリ直下で):
  python -X utf8 -I Tools/Analysis/compare_runs.py <人のフォルダ> <人の SN> <アプリのフォルダ> <アプリの備忘録_SN> [出力png]
  例: python -X utf8 -I Tools/Analysis/compare_runs.py 試験データ/人間が実施/SN71363 SN71363 試験データ/claudeが実施/AS110Noise/SN71363 実機確認_SN60226 docs/実測比較_SN71363_波形.png

- ファイル名は AS110_035_<備忘録>_<SN>_<mA>mA_20ms_440kPa[_n].xdt を前提にし、<備忘録>_<SN> の部分を引数で与える
- 全 CH の平均・p-p、制御圧力の判定(無/黄/赤)と赤の特徴量(lfhf / run0.4 / step_t)、測定時刻を表にし、波形(中央値基準)を png に出す
- 判定は red_rule_eval.py の規則(C# 版 Analysis/NoiseJudge.cs と同じ)
結果の読み方は docs/実測比較_SN71363.md
"""
import sys, os, re, glob, struct, datetime
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from red_rule_eval import read_xdt, features, judge

if len(sys.argv) < 5:
    print(__doc__); sys.exit(1)
HUM, HUM_KEY, CLA, CLA_KEY = sys.argv[1:5]
OUT_PNG = sys.argv[5] if len(sys.argv) > 5 else 'compare_runs.png'
SCALE = [6.0, 100.0, 100.0, 1.0]   # CH1 mA, CH2 kPa, CH3 kPa, CH4 V
NAMES = ['電流mA', '供給kPa', '制御kPa', 'CH4V']

def filetime(path):
    b = open(path, 'rb').read()
    ft = struct.unpack_from('<Q', b, 0x58)[0]
    return datetime.datetime(1601, 1, 1) + datetime.timedelta(microseconds=ft / 10)

def collect(folder, pattern):
    out = {}
    for p in glob.glob(os.path.join(folder, '*.xdt')):
        m = re.search(pattern, os.path.basename(p))
        if not m: continue
        ma = int(m.group(1)); suffix = m.group(2) or ''
        d = read_xdt(p)
        rec = dict(path=p, t=filetime(p), n=d.shape[0], c=d.shape[1])
        for i in range(d.shape[1]):
            x = d[:, i].astype(float) * SCALE[i]
            rec[f'mean{i}'] = x.mean(); rec[f'pp{i}'] = x.max() - x.min()
            rec[f'min{i}'] = x.min(); rec[f'max{i}'] = x.max(); rec[f'std{i}'] = x.std()
        f = features(d[:, 2].astype(float) * 100.0)
        rec['f'] = f; rec['judge'] = judge(f)
        out[(ma, suffix)] = rec
    return out

hum = collect(HUM, '_' + re.escape(HUM_KEY) + r'_(\d+)mA_20ms_440kPa(_\d+)?\.xdt$')
cla = collect(CLA, '_' + re.escape(CLA_KEY) + r'_(\d+)mA_20ms_440kPa(_\d+)?\.xdt$')
print('人間:', len(hum), '本', min(r['t'] for r in hum.values()), '〜', max(r['t'] for r in hum.values()))
print('Claude:', len(cla), '本', min(r['t'] for r in cla.values()), '〜', max(r['t'] for r in cla.values()))
print('サンプル数/CH数 人間:', set((r['n'], r['c']) for r in hum.values()), 'Claude:', set((r['n'], r['c']) for r in cla.values()))

JP = {'none': '無', 'yellow': '黄', 'red': '赤'}
hdr = f"{'mA':>3} | {'電流 人':>7} {'電流 C':>7} | {'供給 人':>7} {'供給 C':>7} | {'制御平均 人':>9} {'制御平均 C':>9} | {'制御pp 人':>7} {'制御pp C':>7} | {'判定 人':>5} {'判定 C':>5} | {'lfhf 人':>7} {'lfhf C':>7} | {'run0.4 人':>6} {'run0.4 C':>6} | {'step_t 人':>7} {'step_t C':>7} | {'CH4 人':>7} {'CH4 C':>7}"
print(hdr); print('-' * len(hdr))
keys = sorted(set(k for k in hum) | set(k for k in cla))
rows = []
for k in keys:
    h = hum.get(k); c = cla.get(k)
    def g(r, key, fmt):
        return (fmt % r[key]) if r else '-'
    def gf(r, key, fmt):
        return (fmt % r['f'][key]) if r else '-'
    def gj(r):
        return JP[r['judge']] if r else '-'
    label = f"{k[0]}{k[1]}"
    print(f"{label:>3} | {g(h,'mean0','%7.3f')} {g(c,'mean0','%7.3f')} | {g(h,'mean1','%7.1f')} {g(c,'mean1','%7.1f')} | {g(h,'mean2','%9.1f')} {g(c,'mean2','%9.1f')} | {g(h,'pp2','%7.1f')} {g(c,'pp2','%7.1f')} | {gj(h):>5} {gj(c):>5} | {gf(h,'lfhf','%7.2f')} {gf(c,'lfhf','%7.2f')} | {gf(h,'run0.4','%6.0f')} {gf(c,'run0.4','%6.0f')} | {gf(h,'step_t','%7.2f')} {gf(c,'step_t','%7.2f')} | {g(h,'mean3','%7.3f')} {g(c,'mean3','%7.3f')}")
    rows.append((k, h, c))

print()
print('--- 供給圧力 p-p / 電流 p-p ---')
for k, h, c in rows:
    print(f"{k[0]}{k[1]:>2} | 供給pp 人 {h['pp1'] if h else float('nan'):5.1f} C {c['pp1'] if c else float('nan'):5.1f} | 電流pp 人 {h['pp0'] if h else float('nan'):6.3f} C {c['pp0'] if c else float('nan'):6.3f} | 電流std 人 {h['std0'] if h else float('nan'):6.3f} C {c['std0'] if c else float('nan'):6.3f}")

print()
print('--- 測定時刻 ---')
for k, h, c in rows:
    print(f"{k[0]}{k[1]:>2} | 人 {h['t'] if h else '-'} | C {c['t'] if c else '-'}")

# 波形ギャラリー
try:
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    from matplotlib import font_manager
    for fn in ['Yu Gothic', 'Meiryo', 'MS Gothic']:
        if any(f.name == fn for f in font_manager.fontManager.ttflist):
            plt.rcParams['font.family'] = fn; break
    common = [k for k in keys if k in hum and k in cla and k[1] == '']
    ncol = 4; nrow = (len(common) + ncol - 1) // ncol
    fig, axes = plt.subplots(nrow, ncol, figsize=(20, 3.0 * nrow), sharex=True)
    t = np.arange(2000) * 0.02
    for ax, k in zip(axes.flat, common):
        for r, lab, col in ((hum[k], '人', 'tab:blue'), (cla[k], 'Claude', 'tab:red')):
            x = read_xdt(r['path'])[:, 2].astype(float) * 100.0
            ax.plot(t, x - np.median(x), lw=0.6, color=col, alpha=0.8, label=f"{lab} pp={r['pp2']:.1f} {JP[r['judge']]}")
        ax.set_title(f"{k[0]} mA  制御圧力(中央値基準)", fontsize=10)
        ax.set_ylim(-2.5, 2.5); ax.grid(alpha=0.3); ax.legend(fontsize=8, loc='upper right')
    for ax in axes.flat[len(common):]: ax.axis('off')
    fig.tight_layout(); fig.savefig(OUT_PNG, dpi=90)
    print('gallery:', OUT_PNG)
except Exception as e:
    print('gallery skipped:', e)
