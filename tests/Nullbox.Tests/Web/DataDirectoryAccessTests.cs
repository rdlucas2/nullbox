using Nullbox.Web.Hosting;

namespace Nullbox.Tests.Web;

public sealed class DataDirectoryAccessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nullbox-datadir-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Creates_the_directory_and_leaves_no_probe_file()
    {
        var dir = DataDirectoryAccess.EnsureWritable(Path.Combine(_root, "keys"));

        Assert.True(dir.Exists);
        Assert.Empty(dir.GetFiles());
    }

    [Fact]
    public void Unwritable_path_fails_with_a_message_that_says_how_to_fix_it()
    {
        Directory.CreateDirectory(_root);
        var blocker = Path.Combine(_root, "data");
        File.WriteAllText(blocker, "a file where the data directory should be");

        var ex = Assert.Throws<DataDirectoryNotWritableException>(() => DataDirectoryAccess.EnsureWritable(Path.Combine(blocker, "keys")));

        Assert.Contains(Path.Combine(blocker, "keys"), ex.Message, StringComparison.Ordinal);
        Assert.Contains("sudo chown -R", ex.Message, StringComparison.Ordinal);
        if (OperatingSystem.IsLinux())
        {
            Assert.Contains($"running as user {DataDirectoryAccess.CurrentUserId()}", ex.Message, StringComparison.Ordinal);
        }
    }
}
