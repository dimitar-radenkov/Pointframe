using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"recording-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal static class FragmentedMp4Fixture
{
    public static void Write(string path, bool withFragment, bool truncateLastBox = false)
    {
        using var stream = File.Create(path);
        WriteBox(stream, "ftyp", new byte[8]);
        WriteBox(stream, "moov", new byte[16]);
        if (!withFragment)
        {
            return;
        }

        WriteBox(stream, "moof", new byte[16]);
        if (truncateLastBox)
        {
            Span<byte> header = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(header, 8 + 64);
            Encoding.ASCII.GetBytes("mdat", header[4..]);
            stream.Write(header);
            stream.Write(new byte[10]);
            return;
        }

        WriteBox(stream, "mdat", new byte[64]);
    }

    private static void WriteBox(Stream stream, string type, byte[] payload)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(8 + payload.Length));
        Encoding.ASCII.GetBytes(type, header[4..]);
        stream.Write(header);
        stream.Write(payload);
    }
}

internal sealed class FakeRemuxer : IRecordingRemuxer
{
    public TimeSpan? RemuxResult { get; set; } = TimeSpan.FromSeconds(83);
    public TimeSpan? ReadResult { get; set; }
    public List<(string Intermediate, string Output)> Calls { get; } = [];

    public TimeSpan? Remux(string intermediatePath, string outputPath)
    {
        Calls.Add((intermediatePath, outputPath));
        if (RemuxResult is null)
        {
            return null;
        }

        File.Copy(intermediatePath, outputPath);
        return RemuxResult;
    }

    public TimeSpan? ReadDuration(string path) => ReadResult;
}

[AttributeUsage(AttributeTargets.Method)]
internal sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        if (!IsFfmpegAvailable())
        {
            Skip = "ffmpeg is not installed or configured.";
        }
    }

    private static bool IsFfmpegAvailable()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = FfmpegResolver.Resolve(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("-version");
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            return process.WaitForExit(10_000) && process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
