using Clay.UI;

namespace Clay.Tests;

public class AtomicFileTests
{
    [Fact]
    public void WritesWithoutUsingAnotherInstancesTemporaryFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"clay-atomic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, "document.txt");
        string otherTemporary = destination + ".tmp";

        try
        {
            File.WriteAllText(otherTemporary, "another instance");
            using (new FileStream(otherTemporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                AtomicFile.WriteAllText(destination, "new contents");

            File.ReadAllText(destination).ShouldBe("new contents");
            File.ReadAllText(otherTemporary).ShouldBe("another instance");
            Directory.GetFiles(directory).Length.ShouldBe(2);
        }
        finally
        {
            foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task ConcurrentWritesDoNotMixContents()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"clay-atomic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, "document.txt");
        var contents = Enumerable.Range(0, 8)
            .Select(i => $"{i}:{new string((char)('a' + i), 200_000)}")
            .ToArray();

        try
        {
            await Task.WhenAll(contents.Select(text => Task.Run(() => AtomicFile.WriteAllText(destination, text))));
            contents.ShouldContain(File.ReadAllText(destination));
            Directory.GetFiles(directory).ShouldHaveSingleItem();
        }
        finally
        {
            foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task ConcurrentReadersAndWritersSeeCompleteContents()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"clay-atomic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, "document.txt");

        try
        {
            string original = new('a', 200_000);
            string replacement = new('b', 200_000);
            AtomicFile.WriteAllText(destination, original);
            using var barrier = new Barrier(2);
            var reader = Task.Run(() =>
            {
                barrier.SignalAndWait();
                for (int i = 0; i < 50; i++)
                    new[] { original, replacement }.ShouldContain(AtomicFile.ReadAllText(destination));
            });
            var writer = Task.Run(() =>
            {
                barrier.SignalAndWait();
                for (int i = 0; i < 50; i++)
                    AtomicFile.WriteAllText(destination, i % 2 == 0 ? replacement : original);
            });
            await Task.WhenAll(reader, writer).WaitAsync(TimeSpan.FromSeconds(30));
            Directory.GetFiles(directory).ShouldHaveSingleItem();
        }
        finally
        {
            foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void RemovesItsTemporaryFileWhenMoveFails()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"clay-atomic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, "existing-directory");
        Directory.CreateDirectory(destination);

        try
        {
            var error = Record.Exception(() => AtomicFile.WriteAllText(destination, "contents"));
            (error is IOException or UnauthorizedAccessException).ShouldBeTrue();
            Directory.GetFiles(directory).ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(destination);
            foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }
}
