using Cutback.Core.Projects;

namespace Cutback.Core.Tests;

public sealed class SourceHashTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"cutback-hash-{Guid.NewGuid():N}");

    public SourceHashTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Pattern(int length, byte seed = 0)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)(i * 31 + seed);
        }

        return bytes;
    }

    [Fact]
    public async Task Same_content_gives_the_same_hash()
    {
        var a = Write("a.bin", Pattern(1000));
        var b = Write("b.bin", Pattern(1000));

        (await SourceHash.ComputeAsync(a, CancellationToken.None))
            .Should().Be(await SourceHash.ComputeAsync(b, CancellationToken.None));
    }

    [Fact]
    public async Task Hash_is_lower_case_hex_sha256_length()
    {
        var a = Write("a.bin", Pattern(10));

        var hash = await SourceHash.ComputeAsync(a, CancellationToken.None);

        hash.Should().HaveLength(64).And.MatchRegex("^[0-9a-f]+$");
    }

    [Fact]
    public async Task Different_content_gives_a_different_hash()
    {
        var a = Write("a.bin", Pattern(1000, seed: 1));
        var b = Write("b.bin", Pattern(1000, seed: 2));

        (await SourceHash.ComputeAsync(a, CancellationToken.None))
            .Should().NotBe(await SourceHash.ComputeAsync(b, CancellationToken.None));
    }

    [Fact]
    public async Task Only_the_first_8MB_are_read_but_the_length_still_counts()
    {
        var head = Pattern(SourceHash.PrefixLength);
        var sameHeadDifferentTail1 = head.Concat(Pattern(100, seed: 1)).ToArray();
        var sameHeadDifferentTail2 = head.Concat(Pattern(100, seed: 2)).ToArray();
        var sameHeadLongerTail = head.Concat(Pattern(200, seed: 1)).ToArray();

        var a = Write("a.bin", sameHeadDifferentTail1);
        var b = Write("b.bin", sameHeadDifferentTail2);
        var c = Write("c.bin", sameHeadLongerTail);

        var hashA = await SourceHash.ComputeAsync(a, CancellationToken.None);
        var hashB = await SourceHash.ComputeAsync(b, CancellationToken.None);
        var hashC = await SourceHash.ComputeAsync(c, CancellationToken.None);

        hashA.Should().Be(hashB, "bytes past the prefix are not read");
        hashA.Should().NotBe(hashC, "the file length is part of the hash");
    }

    [Fact]
    public async Task Empty_file_hashes_without_error()
    {
        var a = Write("empty.bin", []);

        var hash = await SourceHash.ComputeAsync(a, CancellationToken.None);

        hash.Should().HaveLength(64);
    }

    [Fact]
    public async Task Missing_file_throws_FileNotFound()
    {
        var act = () => SourceHash.ComputeAsync(Path.Combine(_dir, "missing.bin"), CancellationToken.None);

        await act.Should().ThrowAsync<FileNotFoundException>();
    }
}
