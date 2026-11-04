# -*- coding: utf-8 -*-
"""
C# 版判定(NoiseJudgeCheck --all → noise_judge_cs.csv)と Python 版(red_rule_eval.py → red_rule_scores.csv)の照合。
指標ごとの最大差と、判定(none/yellow/red)の一致数を出す。
"""
import csv, os, sys
here = os.path.dirname(os.path.abspath(__file__))
py = {(r['dataset'], r['folder'], int(float(r['mA']))): r for r in csv.DictReader(open(os.path.join(here, 'red_rule_scores.csv'), encoding='utf-8-sig'))}
cs = {(r['dataset'], r['folder'], int(r['mA'])): r for r in csv.DictReader(open(os.path.join(here, 'noise_judge_cs.csv'), encoding='utf-8-sig'))}
common = sorted(set(py) & set(cs))
print(f"Python {len(py)} 件 / C# {len(cs)} 件 / 共通 {len(common)} 件")
for k in sorted(set(py) ^ set(cs)): print("  片方だけ:", k)
for col in ('pp', 'run0.4', 'lfhf', 'step_d', 'step_t'):
    worst = max(common, key=lambda k: abs(float(py[k][col]) - float(cs[k][col])))
    d = abs(float(py[worst][col]) - float(cs[worst][col]))
    print(f"{col:8s} 最大差 {d:.3e}  ({worst[1]} {worst[2]}mA: py={py[worst][col]} cs={cs[worst][col]})")
mismatch = [k for k in common if py[k]['judged'] != cs[k]['judged']]
print(f"判定の一致: {len(common) - len(mismatch)}/{len(common)}")
for k in mismatch: print(f"  不一致 {k}: py={py[k]['judged']} cs={cs[k]['judged']}")
human_mismatch = [k for k in common if (py[k]['human'] == 'red') != (cs[k]['judged'] == 'red')]
print(f"C# の赤判定と人の判定の一致: {len(common) - len(human_mismatch)}/{len(common)}")
for k in human_mismatch: print(f"  不一致 {k}: 人={py[k]['human']} cs={cs[k]['judged']}")
sys.exit(1 if mismatch else 0)
