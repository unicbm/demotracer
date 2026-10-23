/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DemoTracer.Tests;

public sealed class DtrArchiveTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("dtr-v13-");
    private string ArchivePath => Path.Combine(_root.FullName,"replay.dtr");
    private static byte[] Golden()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"dtr-v13-golden.json")));
        return Convert.FromHexString(fixture.RootElement.GetProperty("containerHex").GetString()!);
    }

    [Fact]
    public void ReadsSharedRustGoldenWithoutExtractingResources()
    {
        File.WriteAllBytes(ArchivePath,Golden());
        using (var archive = new DtrArchive(ArchivePath))
        {
            Assert.Equal("independent voice block",Encoding.UTF8.GetString(archive.Read("voice/round00.dtv")));
            Assert.Equal("independent replay block",Encoding.UTF8.GetString(archive.Read("round00/t/1.dtr")));
        }
        var address = DtrResources.Address(ArchivePath,"voice/round00.dtv");
        Assert.True(DtrResources.Exists(address));
        Assert.True(ReplayFileStamp.TryRead(address,out var stamp));
        Assert.Equal(new FileInfo(ArchivePath).Length,stamp.Length);
        Assert.Equal("independent voice block",Encoding.UTF8.GetString(DtrResources.ReadAllBytes(address)));
        Assert.Single(_root.GetFiles());
        Assert.Empty(_root.GetDirectories());
    }

    [Fact]
    public void DamageIsDetectedWhenTheAffectedBlockIsRead()
    {
        var bytes = Golden(); bytes[64] ^= 1; File.WriteAllBytes(ArchivePath,bytes);
        using var archive = new DtrArchive(ArchivePath);
        Assert.Throws<InvalidDataException>(()=>archive.Read("round00/t/1.dtr"));
        Assert.Equal("independent voice block",Encoding.UTF8.GetString(archive.Read("voice/round00.dtv")));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("overlap")]
    [InlineData("overflow")]
    [InlineData("escape")]
    public void RejectsInvalidIndexEvenWithAValidChecksum(string kind)
    {
        var rows = new[] {
            new DtrArchive.Entry("a",64,1,new string('0',64)),
            new DtrArchive.Entry("b",65,1,new string('0',64)),
        };
        rows[1] = kind switch {
            "duplicate" => rows[1] with { Path="a" },
            "overlap" => rows[1] with { Offset=64 },
            "overflow" => rows[1] with { Offset=ulong.MaxValue, Length=2 },
            _ => rows[1] with { Path="../outside" },
        };
        var index = JsonSerializer.SerializeToUtf8Bytes(rows);
        var bytes = new byte[66+index.Length];
        "CSDTRPAK"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8),13);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12),64);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16),66);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24),(ulong)index.Length);
        SHA256.HashData(index).CopyTo(bytes,32); index.CopyTo(bytes,66);
        File.WriteAllBytes(ArchivePath,bytes);
        Assert.Throws<InvalidDataException>(()=>new DtrArchive(ArchivePath));
    }

    [Fact]
    public void RejectsTruncatedFilesAndIndexCorruption()
    {
        var bytes = Golden(); File.WriteAllBytes(ArchivePath,bytes[..^1]);
        Assert.Throws<InvalidDataException>(()=>new DtrArchive(ArchivePath));
        bytes[^1] ^= 1; File.WriteAllBytes(ArchivePath,bytes);
        Assert.Throws<InvalidDataException>(()=>new DtrArchive(ArchivePath));
    }

    public void Dispose() => _root.Delete(recursive:true);
}
