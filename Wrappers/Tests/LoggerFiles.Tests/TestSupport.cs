internal static class TestSupport
{
    public static void Ensure(bool ok, string message) { if (!ok) throw new Exception(message); }

    public static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception($"{name}: {typeof(T).Name} が出るべき操作が成功した");
    }

    // テスト用の一時フォルダー。照合用データ(Fixtures)は書き換えない。
    public static string CreateTempFolder(string prefix)
    {
        string path = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static int CountDifferentBytes(byte[] a, byte[] b)
    {
        Ensure(a.Length == b.Length, "ファイル長が変わった");
        int count = 0;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) count++;
        return count;
    }
}
