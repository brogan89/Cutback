namespace Cutback.Analysis.Tests;

public sealed class ModelStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cutback-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
        else if (File.Exists(_dir))
        {
            File.Delete(_dir);
        }
    }

    [Fact]
    public void Paths_live_under_the_cache_directory_and_are_named_by_model()
    {
        var store = new ModelStore(_dir);

        store.CacheDirectory.Should().Be(_dir);
        store.PathFor(WhisperModel.SmallEn).Should().Be(Path.Combine(_dir, "ggml-small.en.bin"));
    }

    [Fact]
    public void IsDownloaded_reflects_the_file_on_disk()
    {
        var store = new ModelStore(_dir);
        store.IsDownloaded(WhisperModel.BaseEn).Should().BeFalse();

        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(store.PathFor(WhisperModel.BaseEn), [1, 2, 3]);

        store.IsDownloaded(WhisperModel.BaseEn).Should().BeTrue();
    }

    [Fact]
    public async Task EnsureAsync_returns_an_existing_file_without_touching_the_network()
    {
        var store = new ModelStore(_dir);
        Directory.CreateDirectory(_dir);
        var path = store.PathFor(WhisperModel.TinyEn);
        File.WriteAllBytes(path, [1, 2, 3]);

        var result = await store.EnsureAsync(WhisperModel.TinyEn, null, CancellationToken.None);

        result.Should().Be(path);
        File.ReadAllBytes(path).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task EnsureAsync_with_a_cancelled_token_throws_and_leaves_no_partial_file()
    {
        var store = new ModelStore(_dir);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => store.EnsureAsync(WhisperModel.TinyEn, null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        Directory.Exists(_dir).Should().BeTrue();
        Directory.GetFiles(_dir).Should().BeEmpty();
    }

    [Fact]
    public async Task EnsureAsync_wraps_an_uncreatable_cache_directory_in_ModelDownloadException()
    {
        // A file where the directory should be makes CreateDirectory throw IOException.
        Directory.CreateDirectory(Path.GetDirectoryName(_dir)!);
        File.WriteAllBytes(_dir, [0]);
        var store = new ModelStore(_dir);

        var act = () => store.EnsureAsync(WhisperModel.TinyEn, null, CancellationToken.None);

        (await act.Should().ThrowAsync<ModelDownloadException>())
            .Which.Message.Should().Contain("tiny.en").And.Contain(_dir);
    }

    [Fact]
    public void Delete_removes_the_cached_model_and_is_a_no_op_when_absent()
    {
        var store = new ModelStore(_dir);
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(store.PathFor(WhisperModel.BaseEn), [1, 2, 3]);

        store.Delete(WhisperModel.BaseEn);

        store.IsDownloaded(WhisperModel.BaseEn).Should().BeFalse();

        var act = () => store.Delete(WhisperModel.BaseEn);

        act.Should().NotThrow();
    }
}
