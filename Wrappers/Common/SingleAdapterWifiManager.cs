using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PSCAutomation.Wrappers.Common;

// PCの1台のWi-Fiアダプターで、接続先の切り替えを担当する。
// Windowsに保存済みのWi-Fiプロファイルを使ってNR-Xへ接続する。
// Windowsに登録済みのWi-Fiプロファイルを、PC内の1台のWi-Fiアダプターで切り替える。
// パスワードは扱わず、Windowsが保存しているプロファイルだけを使用する。
// 切り替え前の接続先を記憶し、復帰要求があれば元へ戻す。
// 切り替え前が未接続なら、復帰時も未接続の状態へ戻す。
// 接続完了の確認とタイムアウト処理も担当する。
public sealed class SingleAdapterWifiManager : IWifiConnectionManager
{
    private string? _previousProfileName;
    private bool _mustRestore;

    static SingleAdapterWifiManager()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public bool MustRestore => _mustRestore;

    public async Task ConnectAsync(
        string targetProfileName,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetProfileName))
        {
            throw new ArgumentException(
                "NR-XのWi-Fiプロファイル名を入力してください。",
                nameof(targetProfileName));
        }

        string profileName = targetProfileName.Trim();
        await EnsureProfileExistsAsync(profileName, cancellationToken);

        string? currentProfile = await GetCurrentProfileAsync(cancellationToken);
        if (string.Equals(currentProfile, profileName, StringComparison.Ordinal))
        {
            return;
        }

        // 最初の切替時だけ復帰先を記憶する。nullは切替前が未接続だったことを表す。
        if (!_mustRestore)
        {
            _previousProfileName = currentProfile;
            _mustRestore = true;
        }

        NetshResult connectResult = await RunNetshAsync(
            ["wlan", "connect", $"name={profileName}"],
            cancellationToken);
        if (connectResult.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Wi-Fiプロファイル「{profileName}」への接続指示に失敗しました。" +
                FormatNetshError(connectResult));
        }

        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(500, cancellationToken);

            currentProfile = await GetCurrentProfileAsync(cancellationToken);
            if (string.Equals(currentProfile, profileName, StringComparison.Ordinal))
            {
                return;
            }
        }

        throw new TimeoutException(
            $"Wi-Fiプロファイル「{profileName}」へ{timeout.TotalSeconds:0}秒以内に接続できませんでした。");
    }

    public async Task RestoreAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (!_mustRestore)
        {
            return;
        }

        string? restoreProfile = _previousProfileName;

        if (string.IsNullOrWhiteSpace(restoreProfile))
        {
            NetshResult disconnectResult = await RunNetshAsync(
                ["wlan", "disconnect"], cancellationToken);
            if (disconnectResult.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "Wi-Fiの切断に失敗しました。" + FormatNetshError(disconnectResult));
            }

            ClearRestoreState();
            return;
        }

        NetshResult connectResult = await RunNetshAsync(
            ["wlan", "connect", $"name={restoreProfile}"],
            cancellationToken);
        if (connectResult.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"元のWi-Fi「{restoreProfile}」への復帰指示に失敗しました。" +
                FormatNetshError(connectResult));
        }

        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(500, cancellationToken);

            string? currentProfile = await GetCurrentProfileAsync(cancellationToken);
            if (string.Equals(currentProfile, restoreProfile, StringComparison.Ordinal))
            {
                ClearRestoreState();
                return;
            }
        }

        throw new TimeoutException(
            $"元のWi-Fi「{restoreProfile}」へ{timeout.TotalSeconds:0}秒以内に復帰できませんでした。");
    }

    private async Task EnsureProfileExistsAsync(
        string profileName,
        CancellationToken cancellationToken)
    {
        NetshResult result = await RunNetshAsync(
            ["wlan", "show", "profile", $"name={profileName}"],
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"WindowsにWi-Fiプロファイル「{profileName}」が登録されていません。" +
                "最初の1回だけWindowsのWi-Fi画面からNR-Xへ接続し、プロファイルを保存してください。");
        }
    }

    private async Task<string?> GetCurrentProfileAsync(
        CancellationToken cancellationToken)
    {
        NetshResult result = await RunNetshAsync(
            ["wlan", "show", "interfaces"], cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "現在のWi-Fi接続を取得できません。" + FormatNetshError(result));
        }

        string? ssid = null;
        foreach (string rawLine in result.StandardOutput.Split('\n'))
        {
            string line = rawLine.Trim();
            int separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            string key = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();

            if (key.Equals("Profile", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("プロファイル", StringComparison.Ordinal))
            {
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }

            if (key.Equals("SSID", StringComparison.OrdinalIgnoreCase))
            {
                ssid = string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        // 通常、Windowsが自動作成したプロファイル名はSSIDと同じ。
        return ssid;
    }

    private static async Task<NetshResult> RunNetshAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        Encoding consoleEncoding = Encoding.GetEncoding(
            CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        var startInfo = new ProcessStartInfo
        {
            FileName = "netsh.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = consoleEncoding,
            StandardErrorEncoding = consoleEncoding
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return new NetshResult(
            process.ExitCode,
            await outputTask,
            await errorTask);
    }

    private static string FormatNetshError(NetshResult result)
    {
        string detail = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput.Trim()
            : result.StandardError.Trim();
        return string.IsNullOrWhiteSpace(detail) ? string.Empty : $" / {detail}";
    }

    private void ClearRestoreState()
    {
        _previousProfileName = null;
        _mustRestore = false;
    }

    private sealed record NetshResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
