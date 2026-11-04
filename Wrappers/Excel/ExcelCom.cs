using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PSCAutomation.Wrappers.Excel;

// Excel COM(late binding)の小道具。ExcelApplication / ExcelWorkbook / ExcelSheet / ExcelViewer で共有する。
[SupportedOSPlatform("windows")]
internal static class ExcelCom
{
    internal const int XlCalculationAutomatic = -4105;
    internal const int XlCalculating = 1;
    internal const int XlUp = -4162;
    internal const int XlMinimized = -4140;
    internal const int XlNormal = -4143;
    internal const int XlTypePdf = 0;
    internal const int MsoAutoShape = 1;
    internal const int MsoTextBox = 17;

    // 参照を 1 つだけ返す。同じシート・ブックを別の経路で取ると .NET 側では同じ参照(RCW)になるので、
    // FinalReleaseComObject で全部返すと、アプリが持っている ExcelSheet などまで使えなくなる。
    // 残った参照は ExcelApplication.Dispose の GC で返す。
    internal static void Release(object? value)
    {
        if (value is null || !Marshal.IsComObject(value)) return;
        try { Marshal.ReleaseComObject(value); } catch { }
    }

    // 自分だけが持つと分かっている参照をすべて返す(ExcelViewer。元の ExcelPresenter と同じ)。
    internal static void ReleaseAll(object? value)
    {
        if (value is null || !Marshal.IsComObject(value)) return;
        try { Marshal.FinalReleaseComObject(value); } catch { }
    }

    // Excel が落ちた・切断されたと確定できる例外だけ。編集中・応答待ち(呼び出し拒否)は含めない。
    // 元の AllCheck は上の 3 つ。0x800706BE は Excel が落ちた直後の最初の呼び出しで出るので足した(Excel.Tests で確認)。
    internal static bool IsDisconnected(Exception ex) =>
        ex is InvalidComObjectException
        || ex is COMException com && com.ErrorCode is unchecked((int)0x80010108)   // RPC_E_DISCONNECTED
            or unchecked((int)0x800401FD)                                           // CO_E_OBJNOTCONNECTED
            or unchecked((int)0x800706BA)                                           // RPC_S_SERVER_UNAVAILABLE
            or unchecked((int)0x800706BE);                                          // RPC_S_CALL_FAILED

    // Excel.Application の窓からプロセス ID を求める。
    internal static int ProcessIdOf(object application)
    {
        var handle = new IntPtr((int)((dynamic)application).Hwnd);
        GetWindowThreadProcessId(handle, out uint id);
        return (int)id;
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    internal static string ColumnName(int column)
    {
        if (column < 1 || column > 16384) throw new ArgumentOutOfRangeException(nameof(column), "列は 1〜16384 で指定してください。");
        string name = "";
        for (int c = column; c > 0; c = (c - 1) / 26) name = (char)('A' + (c - 1) % 26) + name;
        return name;
    }

    internal static string Address(int row, int column)
    {
        if (row < 1 || row > 1048576) throw new ArgumentOutOfRangeException(nameof(row), "行は 1〜1048576 で指定してください。");
        return ColumnName(column) + row.ToString(CultureInfo.InvariantCulture);
    }

    // Range.Value2 は 1 セルなら値、複数セルなら 1 始まりの 2 次元配列を返す。どちらも 0 始まりの配列にそろえる。
    internal static object?[,] ToZeroBased(object? value2)
    {
        if (value2 is object[,] array)
        {
            int rows = array.GetLength(0), columns = array.GetLength(1);
            int r0 = array.GetLowerBound(0), c0 = array.GetLowerBound(1);
            var result = new object?[rows, columns];
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < columns; c++)
                    result[r, c] = array[r0 + r, c0 + c];
            return result;
        }
        return new object?[,] { { value2 } };
    }
}
