using Backend.Routes;
using FluentAssertions;
using Xunit;

namespace Backend.Test
{
    public class StorageSizeTests
    {
        // The admin stats page measures the storage folder while other requests add and delete files in it
        // (parallel tests do the same). A file or folder vanishing between listing and measuring used to
        // make the whole figure come back null ("unavailable").
        [Fact]
        public async Task StorageBytesOnDisk_IsNotLost_WhenFilesComeAndGoWhileItIsMeasured()
        {
            var root = Path.Combine(Path.GetTempPath(), "fv-size-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                File.WriteAllBytes(Path.Combine(root, "stays.bin"), new byte[1000]);
                var stop = false;
                var churn = Task.Run(() =>
                {
                    var n = 0;
                    while (!Volatile.Read(ref stop))
                    {
                        var dir = Path.Combine(root, "d" + (n++ % 50));
                        Directory.CreateDirectory(dir);
                        for (var i = 0; i < 20; i++)
                            File.WriteAllBytes(Path.Combine(dir, i + ".bin"), new byte[10]);
                        Directory.Delete(dir, true);
                    }
                });

                var nulls = 0;
                for (var i = 0; i < 3000; i++)
                {
                    var size = AdminRoutes.StorageBytesOnDisk(root);
                    if (size == null || size < 1000)
                        nulls++;
                }
                stop = true;
                await churn;

                nulls.Should().Be(0);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }
    }
}
