using PSCAutomation.Wrappers.LoggerFiles;
using static TestSupport;

// XdtScalingEditor(元: WaveLogger.Automation 内、AS110AllCheck 版)の検査。モノレポで追加。
// XdtFile(別実装の読み出し)と同じ値を読むことで、書き換え位置が正しいことを確かめる。
internal static class ScalingEditorChecks
{
    public static void Run(string fixture)
    {
        string temp = CreateTempFolder("LoggerFilesScaling");
        try
        {
            foreach (string name in new[] { "as110-motion-legacy.xdt", "PC.xdt", "loggerX.xdt" })
            {
                string path = Path.Combine(fixture, name);
                var file = XdtFile.Load(path);
                var editor = new XdtScalingEditor(path);
                int? target = null;
                foreach (var channel in file.Channels)
                {
                    var info = channel.Info;
                    if (info.ScalingEnabled)
                    {
                        var p = editor.GetScaling(info.Number);
                        var q = info.ScalingPoints;
                        Ensure(p.Input1 == (float)q.Input1 && p.Output1 == (float)q.Output1
                            && p.Input2 == (float)q.Input2 && p.Output2 == (float)q.Output2,
                            $"{name} CH{info.Number}: XdtFile と XdtScalingEditor の読み値が違う");
                        target ??= info.Number;
                    }
                    else
                    {
                        Throws<InvalidDataException>(() => editor.GetScaling(info.Number), $"{name} CH{info.Number} スケーリング無効");
                    }
                }
                Ensure(target is not null, name + ": スケーリング有効の CH がない(照合用データの想定違い)");
                int ch = target!.Value;
                Throws<ArgumentException>(() => editor.SetScaling(ch, 1, 2, 1, 3), name + " 同じ入力 2 点");
                Throws<ArgumentOutOfRangeException>(() => editor.SetScaling(0, 0, 0, 1, 1), name + " CH0");
                editor.SetScaling(ch, 0.5f, 1.5f, 2.5f, 7.5f);
                string output = Path.Combine(temp, name);
                editor.Save(output);
                var changed = XdtFile.Load(output).GetChannel(ch).Info.ScalingPoints;
                Ensure(changed == new XdtScalingPoints(0.5f, 1.5f, 2.5f, 7.5f), name + ": 書き換えた値を XdtFile で読めない");
                Ensure(CountDifferentBytes(File.ReadAllBytes(path), File.ReadAllBytes(output)) <= 16, name + ": スケーリング以外のバイトが変わった");
                Ensure(new XdtScalingEditor(output).GetScaling(ch) == new ScalingPoints(0.5f, 1.5f, 2.5f, 7.5f), name + ": 再読込値");
                Console.WriteLine($"PASS XdtScalingEditor {name}: 全 CH の読み値が XdtFile と一致、CH{ch} の書き換えは 16 バイト以内");
            }

            string ydt = Path.Combine(fixture, "as110-motion-nrx100.ydt");
            var ydtEditor = new XdtScalingEditor(ydt);
            var before = ydtEditor.GetScaling(1);
            Ensure(float.IsFinite(before.Input1) && before.Input1 != before.Input2, "ydt CH1 の読み値");
            Throws<NotSupportedException>(() => ydtEditor.GetScaling(2), "ydt CH2 読み出し");
            Throws<NotSupportedException>(() => ydtEditor.SetScaling(2, 0, 0, 1, 1), "ydt CH2 書き換え");
            ydtEditor.SetScaling(1, -0.002f, 0.01f, 0.998f, 30.02f);
            string ydtOut = Path.Combine(temp, "out.ydt");
            ydtEditor.Save(ydtOut);
            Ensure(new XdtScalingEditor(ydtOut).GetScaling(1) == new ScalingPoints(-0.002f, 0.01f, 0.998f, 30.02f), "ydt 再読込値");
            Ensure(CountDifferentBytes(File.ReadAllBytes(ydt), File.ReadAllBytes(ydtOut)) <= 16, "ydt: スケーリング以外のバイトが変わった");
            Throws<InvalidDataException>(() => XdtFile.Load(ydt), "XdtFile は ydt 未対応");
            string fakeYdt = Path.Combine(temp, "fake.ydt");
            File.Copy(Path.Combine(fixture, "PC.xdt"), fakeYdt);
            Throws<InvalidDataException>(() => new XdtScalingEditor(fakeYdt), "xdt の中身で拡張子だけ ydt");
            Console.WriteLine("PASS XdtScalingEditor NR-X100 ydt: CH1 のみ読み書き、CH2 以降と拡張子違いは例外");
        }
        finally { Directory.Delete(temp, true); }
    }
}
