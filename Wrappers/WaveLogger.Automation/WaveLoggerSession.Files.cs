using System.Runtime.InteropServices;

namespace PSCAutomation.Wrappers.WaveLogger.Automation;

public sealed partial class WaveLoggerSession
{
        public long OpenFile(string fileName)
        {
            using var observation = ObserveOperation?.Invoke("LOGGER.WaveLog_Open_File");
            dynamic app = GetApplication();
            _waveLoggerXStartCompleted = false;
            return Convert.ToInt64(app.OpenFile(fileName));
        }

        public long CloseActiveFile()
        {
            using var observation = ObserveOperation?.Invoke("LOGGER.WaveLog_Close_File");
            dynamic app = GetApplication();
            _waveLoggerXStartCompleted = false;
            return Convert.ToInt64(app.CloseActiveFile());
        }

        /// <summary>
        /// オートメーションで起動したLOGGERのアクティブなドキュメントを確認する。
        /// ファイルの読み込み・切替・クローズは行わない。
        /// </summary>
        public bool HasActiveFile()
        {
            using var observation = ObserveOperation?.Invoke("LOGGER.HasActiveFile");
            dynamic app = GetApplication();
            if (ActiveEdition == WaveLoggerEdition.WaveLoggerX)
            {
                // X版: 0=ファイルなし、1=NR-500、2=NR-X。
                long fileType = Convert.ToInt64(app.GetActiveFileType());
                return fileType switch
                {
                    0 => false,
                    1 or 2 => true,
                    _ => throw new InvalidOperationException(
                        $"GetActiveFileType() = {fileType}")
                };
            }

            long count = Convert.ToInt64(app.GetFileCount());
            if (count < 0)
                throw new InvalidOperationException($"GetFileCount() = {count}");
            if (count == 0) return false;

            object? document = null;
            try
            {
                document = app.GetActiveFile();
                return document is not null;
            }
            finally
            {
                // この呼出しで取得した参照だけを解放する。
                if (document is not null && Marshal.IsComObject(document))
                    Marshal.ReleaseComObject(document);
            }
        }

        public long GetDataCount()
        {
            dynamic app = GetApplication();
            dynamic waveForm = app.GetActiveFile();

            try
            {
                return Convert.ToInt64(waveForm.GetDataCount());
            }
            finally
            {
                ReleaseComObject((object?)waveForm);
            }
        }

        /// <summary>
        /// 現在アクティブなファイルをLOGGER形式で保存し、メーカーAPIの戻り値を返す。
        /// 保存対象・停止状態・パス・上書き方針・出力の検証は呼び出し元が管理する。
        /// </summary>
        public long SaveXdt(string xdtPath)
        {
            dynamic app = GetApplication();
            dynamic waveForm = app.GetActiveFile();

            try
            {
                return Convert.ToInt64(waveForm.SaveFile(xdtPath));
            }
            finally
            {
                ReleaseComObject((object?)waveForm);
            }
        }

        /// <summary>
        /// 現在アクティブなファイルをCSV出力し、メーカーAPIの戻り値を返す。
        /// Excel転送や再試行は行わない。取得したドキュメントのCOM参照は例外時も解放する。
        /// </summary>
        public long SaveCsv(string csvPath)
        {
            dynamic app = GetApplication();
            dynamic waveForm = app.GetActiveFile();

            try
            {
                return Convert.ToInt64(waveForm.SaveCsvFile(csvPath));
            }
            finally
            {
                ReleaseComObject((object?)waveForm);
            }
        }

}
