using System.Security.Cryptography;
using System.Text;

namespace Clay;

internal static class AtomicFile
{
    public static string ReadAllText(string path)
    {
        string destination = Path.GetFullPath(path);
        return WithPathLock(destination, () => File.ReadAllText(destination));
    }

    public static void WriteAllText(string path, string contents)
    {
        string destination = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        bool created = false;
        bool moved = false;

        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                using var writer = new StreamWriter(stream);
                writer.Write(contents);
            }

            WithPathLock(destination, () =>
            {
                File.Move(temporary, destination, overwrite: true);
                moved = true;
                return true;
            });
        }
        finally
        {
            if (created && !moved) File.Delete(temporary);
        }
    }

    private static T WithPathLock<T>(string destination, Func<T> action)
    {
        string key = OperatingSystem.IsWindows() ? destination.ToUpperInvariant() : destination;
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        string mutexName = (OperatingSystem.IsWindows() ? @"Global\" : "") + $"Clay.AtomicFile.{hash}";
        using var mutex = new Mutex(false, mutexName);
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.FromSeconds(5));
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }
        if (!acquired) throw new IOException($"Timed out waiting to access '{destination}'");
        try
        {
            return action();
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }
}
