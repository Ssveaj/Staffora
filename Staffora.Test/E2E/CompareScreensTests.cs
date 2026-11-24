using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using Staffora.E2ESetup;

namespace Staffora.Test.E2E;

[Trait("Category", "E2E")]
public class CompareScreensTests : IDisposable
{
    private const int Pad = 16;
    private const int Gap = 16;
    private const int Caption = 36;

    private readonly string folder;

    public CompareScreensTests()
    {
        this.folder = Path.Combine(Path.GetTempPath(), $"staffora-compare-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.folder);
    }

    [Fact]
    public async Task Two_screenshots_become_one_side_by_side_image_sized_by_both()
    {
        var left = Path.Combine(this.folder, "ours.png");
        var right = Path.Combine(this.folder, "reference.png");
        var output = Path.Combine(this.folder, "compare", "pair.png");
        await File.WriteAllBytesAsync(left, SolidPng(40, 30, 0x0f, 0x76, 0x6e), TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(right, SolidPng(50, 70, 0xe2, 0xe8, 0xf0), TestContext.Current.CancellationToken);

        var (exitCode, text) = await RunAsync(TestContext.Current.CancellationToken, left, right, output, "--left-label", "Ours", "--right-label", "Theirs");

        Assert.True(exitCode == 0, text);
        var (width, height) = PngSize(await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
        Assert.Equal(Pad + 40 + Gap + 50 + Pad, width);
        Assert.Equal(Pad + Caption + 70 + Pad, height);
    }

    [Fact]
    public async Task A_file_that_is_not_a_PNG_is_refused_and_nothing_is_written()
    {
        var left = Path.Combine(this.folder, "ours.png");
        var right = Path.Combine(this.folder, "reference.txt");
        var output = Path.Combine(this.folder, "refused.png");
        await File.WriteAllBytesAsync(left, SolidPng(4, 4, 0, 0, 0), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(right, "not an image", TestContext.Current.CancellationToken);

        var (exitCode, text) = await RunAsync(TestContext.Current.CancellationToken, left, right, output);

        Assert.Equal(1, exitCode);
        Assert.Contains("is not a PNG file", text);
        Assert.False(File.Exists(output));
    }

    public void Dispose()
    {
        Directory.Delete(this.folder, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        var root = FoundationStateFile.FindRepositoryRoot();
        var start = new ProcessStartInfo("node")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(root, "scripts", "compare-screens.mjs"));
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await output + await error);
    }

    private static (int Width, int Height) PngSize(byte[] png) =>
        (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));

    private static byte[] SolidPng(int width, int height, byte red, byte green, byte blue)
    {
        var rows = new byte[height * (1 + width * 3)];
        for (var row = 0; row < height; row++)
        {
            var offset = row * (1 + width * 3);
            for (var column = 0; column < width; column++)
            {
                rows[offset + 1 + column * 3] = red;
                rows[offset + 2 + column * 3] = green;
                rows[offset + 3 + column * 3] = blue;
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(rows);
        }

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), height);
        header[8] = 8;
        header[9] = 2;

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream png, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        png.Write(number);
        var typed = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type, typed);
        data.CopyTo(typed, 4);
        png.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc32(typed));
        png.Write(number);
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xffffffffu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
