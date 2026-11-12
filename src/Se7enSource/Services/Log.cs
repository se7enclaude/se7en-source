using System.IO;

namespace Se7enSource.Services;

/// <summary>Journal des erreurs : %AppData%\Se7enSource\log.txt, gardé petit (l'ancien passe en log.old.txt).</summary>
public static class Log
{
    private const long MaxBytes = 256 * 1024;
    private static readonly object Gate = new();
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Se7enSource");
    public static readonly string FilePath = Path.Combine(Dir, "log.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes) File.Move(FilePath, Path.Combine(Dir, "log.old.txt"), true);
                string version = typeof(Log).Assembly.GetName().Version?.ToString(3) ?? "?";
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{version}] {message}{Environment.NewLine}");
            }
        }
        catch (Exception) { } // le journal ne doit jamais faire tomber l'appli
    }

    public static void Error(string where, Exception? e) => Write($"{where} : {e}");
}
