namespace Clay.Tests;

public class DocumentOperationQueueTests
{
    [Fact]
    public async Task OpenWaitsForSaveAndNextSaveUsesOpenedDocument()
    {
        var queue = new DocumentOperationQueue();
        var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var files = new Dictionary<string, string>();
        string currentFile = "A";
        string text = "contents of A";

        Task firstSave = queue.RunAsync(async () =>
        {
            string file = currentFile;
            string contents = text;
            saveStarted.SetResult();
            await finishSave.Task;
            files[file] = contents;
            currentFile = file;
        });
        await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task open = queue.RunAsync(() =>
        {
            text = "contents of B";
            currentFile = "B";
            return Task.CompletedTask;
        });

        Assert.False(open.IsCompleted);
        finishSave.SetResult();
        await Task.WhenAll(firstSave, open).WaitAsync(TimeSpan.FromSeconds(5));
        await queue.RunAsync(() =>
        {
            files[currentFile] = text;
            return Task.CompletedTask;
        }).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("contents of A", files["A"]);
        Assert.Equal("contents of B", files["B"]);
    }

    [Fact]
    public async Task FailedOperationReleasesQueue()
    {
        var queue = new DocumentOperationQueue();
        await Assert.ThrowsAsync<IOException>(() => queue.RunAsync(() => throw new IOException("failed")));

        bool ran = false;
        await queue.RunAsync(() =>
        {
            ran = true;
            return Task.CompletedTask;
        }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(ran);
    }
}
