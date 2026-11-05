using System.Reflection;
using System.Runtime.InteropServices;

// 事前起動(WaveLoggerSession.Prestart.cs)の確認。LOGGER 本体ソフトは使わない。
// - 事前起動していないセッションでは、待ち・破棄が何もせずに済むこと
// - 別 STA スレッドで作った COM オブジェクトの参照を、受け取る側のスレッドで 1 回だけ受け取れること(Windows 標準の Scripting.Dictionary で代用)
// - 受け取らずに破棄しても止まらないこと、作成に失敗しても例外にならず理由が分かること
// - 別プロセスの COM サーバー(LOGGER と同じ形)を受け取った後に実際に呼べること(Excel がある PC だけ。無ければ SKIP)
internal static class PrestartTests
{
    public static async Task RunAsync(Type control)
    {
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); }

        // 事前起動していないセッション: 待ちは即完了、破棄は何もしない、HasPendingServerStartup は false。
        var session = Activator.CreateInstance(control)!;
        Check(!(bool)control.GetProperty("HasPendingServerStartup")!.GetValue(session)!, "pending without prestart");
        var wait = (Task)control.GetMethod("WaitForServerStartupAsync")!.Invoke(session, null)!;
        Check(wait.IsCompletedSuccessfully, "wait without prestart did not complete at once");
        control.GetMethod("CancelServerStartup")!.Invoke(session, null);
        control.GetMethod("Release")!.Invoke(session, null);
        Console.WriteLine("PASS no prestart: wait completes at once, cancel and release are harmless");

        var prestartType = control.GetNestedType("ServerPrestart", BindingFlags.NonPublic)!;
        var editionType = control.Assembly.GetType("PSCAutomation.Wrappers.WaveLogger.Automation.WaveLoggerEdition")!;
        object legacy = Enum.Parse(editionType, "Legacy");
        object Create(Type comType) => Activator.CreateInstance(prestartType, comType, legacy, null)!;
        Task Completion(object p) => (Task)prestartType.GetProperty("Completion")!.GetValue(p)!;
        object? Error(object p) => prestartType.GetProperty("Error")!.GetValue(p);
        void Start(object p) => prestartType.GetMethod("Start")!.Invoke(p, null);
        (bool ok, object? instance, string? failure) Take(object p)
        {
            var args = new object?[] { null, null };
            bool ok = (bool)prestartType.GetMethod("TryTake")!.Invoke(p, args)!;
            return (ok, args[0], (string?)args[1]);
        }
        // 受け取りは、アプリと同じく STA スレッドで行う。
        void OnSta(Action action)
        {
            Exception? error = null;
            var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (error is not null) throw new Exception(error.Message, error);
        }

        // 別 STA スレッドで作った COM オブジェクトの参照を 1 回だけ受け取れる(プロセス内の部品で仕組みだけ確認)。
        Type? dictionaryType = Type.GetTypeFromProgID("Scripting.Dictionary");
        if (dictionaryType is null)
        {
            Console.WriteLine("SKIP prestart handoff: Scripting.Dictionary is not registered");
        }
        else
        {
            var handoff = Create(dictionaryType);
            Start(handoff);
            await Completion(handoff);
            Check(Error(handoff) is null, "prestart failed: " + Error(handoff));
            OnSta(() =>
            {
                var (ok, instance, failure) = Take(handoff);
                if (!ok) throw new Exception("TryTake failed: " + failure);
                if (instance is null || !Marshal.IsComObject(instance)) throw new Exception("taken object is not a COM object");
                Marshal.FinalReleaseComObject(instance);
                var (again, _, againFailure) = Take(handoff);
                if (again) throw new Exception("TryTake succeeded twice");
                if (againFailure is null) throw new Exception("second TryTake gave no reason");
            });
            Console.WriteLine("PASS prestart handoff: reference created on another STA thread is taken once, second take fails with a reason");

            // 受け取らずに破棄する。起動スレッドが止まらないこと(Abandon 後の TryTake は失敗する)。
            var abandoned = Create(dictionaryType);
            Start(abandoned);
            await Completion(abandoned);
            prestartType.GetMethod("Abandon")!.Invoke(abandoned, null);
            var (taken, _, reason) = Take(abandoned);
            Check(!taken && reason is not null, "TryTake after Abandon should fail with a reason");
            Console.WriteLine("PASS prestart abandon: released without being taken");
        }

        // 作成に失敗する型でも Completion は完了し、TryTake は理由付きで失敗する(COM オブジェクトが作れない場合の代用)。
        var failing = Create(typeof(PrestartFailure));
        Start(failing);
        await Completion(failing);
        Check(Error(failing) is not null, "failure was not recorded");
        var (failedTake, _, failedReason) = Take(failing);
        Check(!failedTake && failedReason is not null, "TryTake after failure should fail with a reason");
        Console.WriteLine("PASS prestart failure: completes without throwing and reports the reason");

        // 別プロセスの COM サーバー(LOGGER と同じ形)。受け取った後、起動スレッドが抜けても呼べることを確かめる。
        Type? excelType = Type.GetTypeFromProgID("Excel.Application");
        if (excelType is null)
        {
            Console.WriteLine("SKIP prestart out-of-process call: Excel.Application is not registered");
            return;
        }
        var server = Create(excelType);
        Start(server);
        await Completion(server);
        Check(Error(server) is null, "Excel prestart failed: " + Error(server));
        string version = "";
        OnSta(() =>
        {
            var (ok, instance, failure) = Take(server);
            if (!ok) throw new Exception("TryTake(Excel) failed: " + failure);
            try
            {
                dynamic excel = instance!;
                // 起動スレッドが参照を手放して抜けるのを待ってから呼ぶ(アプリでは受け取り後に Initialize・Visible を呼ぶ)。
                Thread.Sleep(500);
                version = (string)excel.Version;
                excel.Quit();
            }
            finally
            {
                Marshal.FinalReleaseComObject(instance!);
            }
        });
        Check(version.Length > 0, "Excel did not answer after the handoff");
        Console.WriteLine("PASS prestart out-of-process call: Excel " + version + " answered through the taken reference");
    }

    // Activator.CreateInstance で例外になる型(COM オブジェクトが作れない場合の代用)。
    private sealed class PrestartFailure
    {
        public PrestartFailure() => throw new InvalidOperationException("startup failed on purpose");
    }
}
