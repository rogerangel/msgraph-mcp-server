using Microsoft.AspNetCore.DataProtection;

namespace GraphMcp.Auth;

// The only on-disk representation is Data Protection ciphertext. Never retry as plaintext.
internal static class ProtectedFile
{
    private const int MaximumBytes = 4 * 1024 * 1024;

    public static byte[]? Read(string path, IDataProtector protector)
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > MaximumBytes)
            throw new InvalidDataException("Protected authentication state exceeds its size limit.");
        return protector.Unprotect(File.ReadAllBytes(path));
    }

    public static void Write(string path, byte[] data, IDataProtector protector)
    {
        var protectedBytes = protector.Protect(data);
        if (protectedBytes.Length > MaximumBytes)
            throw new InvalidDataException("Protected authentication state exceeds its size limit.");
        CreatePrivateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temporary, options))
            {
                file.Write(protectedBytes);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static void CreatePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
