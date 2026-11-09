namespace Clay.Tests;

public class AsyncOperationQueueTests
{
    [Fact]
    public async Task OpenWaitsForSaveAndNextSaveUsesOpenedDocument()
    {
        var queue = new AsyncOperationQueue();
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
        var queue = new AsyncOperationQueue();
        await Assert.ThrowsAsync<IOException>(() => queue.RunAsync(() => throw new IOException("failed")));

        bool ran = false;
        await queue.RunAsync(() =>
        {
            ran = true;
            return Task.CompletedTask;
        }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(ran);
    }

    [Fact]
    public async Task ErrorDialogsAndHelpDoNotOverlap()
    {
        var documents = new AsyncOperationQueue();
        var dialogs = new AsyncOperationQueue();
        var firstDialogStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeFirstDialog = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int shown = 0;

        async Task FailedSaveAsync(bool first)
        {
            try
            {
                await documents.RunAsync(() => throw new IOException("save failed"));
            }
            catch (IOException)
            {
                await dialogs.RunAsync(async () =>
                {
                    Interlocked.Increment(ref shown);
                    if (first)
                    {
                        firstDialogStarted.SetResult();
                        await closeFirstDialog.Task;
                    }
                });
            }
        }

        Task firstSave = FailedSaveAsync(first: true);
        await firstDialogStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task secondSave = FailedSaveAsync(first: false);
        Task help = dialogs.RunAsync(() =>
        {
            Interlocked.Increment(ref shown);
            return Task.CompletedTask;
        });

        Assert.False(secondSave.IsCompleted);
        Assert.False(help.IsCompleted);
        Assert.Equal(1, Volatile.Read(ref shown));
        closeFirstDialog.SetResult();
        await Task.WhenAll(firstSave, secondSave, help).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, shown);
    }
}
