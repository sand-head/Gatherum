using Gatherum.Core;
using Gatherum.Core.Services;
using Gatherum.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace Gatherum.Tests;

public class FirmwareServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"gatherum-test-{Guid.NewGuid():N}");
    private readonly FirmwareService firmware;

    public FirmwareServiceTests() => firmware = new FirmwareService(new FileSystemStorage(Options.Create(
        new GatherumOptions { Storage = new StorageOptions { Root = root } })));

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task A_file_of_the_right_size_is_kept_in_the_instance_sidecar_and_listed()
    {
        var spec = FirmwareService.Spec("gamecube", "ipl.bin");
        var bytes = new byte[spec.Bytes];
        bytes[0] = 0x2A;

        var stored = await firmware.StoreAsync("gamecube", "ipl.bin", new MemoryStream(bytes));

        Assert.Equal(spec.Bytes, stored.SizeBytes);
        // Where a person looking at the directories would expect the instance's own
        // things: beside the users' roots, not inside any of them.
        Assert.True(File.Exists(Path.Combine(root, ".gatherum", "firmware", "gamecube", "ipl.bin")));
        Assert.Empty(new FileSystemStorage(Options.Create(
            new GatherumOptions { Storage = new StorageOptions { Root = root } })).Roots());

        var listed = Assert.Single(await firmware.ListAsync(), f => f.Spec == spec);
        Assert.Equal(stored, listed.Stored);
        await using var opened = await firmware.OpenAsync("gamecube", "ipl.bin");
        Assert.NotNull(opened);
        Assert.Equal(0x2A, opened.ReadByte());
    }

    [Fact]
    public async Task A_file_of_another_size_is_refused()
    {
        var spec = FirmwareService.Spec("gamecube", "ipl.bin");

        var problem = await Assert.ThrowsAsync<ValidationException>(() =>
            firmware.StoreAsync("gamecube", "ipl.bin", new MemoryStream(new byte[100])));
        Assert.Contains("2,097,152", problem.Message);

        // A file longer than the chip is refused without reading all of it, so an
        // enormous one costs nothing to say no to.
        await Assert.ThrowsAsync<ValidationException>(() =>
            firmware.StoreAsync("gamecube", "ipl.bin", new MemoryStream(new byte[spec.Bytes + 1])));

        Assert.Null(Assert.Single(await firmware.ListAsync(), f => f.Spec == spec).Stored);
    }

    [Fact]
    public async Task Only_catalogued_files_exist_and_a_removed_one_is_gone()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => firmware.OpenAsync("gamecube", "dsp_rom.bin"));
        await Assert.ThrowsAsync<NotFoundException>(() => firmware.OpenAsync("../gamecube", "ipl.bin"));

        await firmware.StoreAsync("gamecube", "ipl.bin", new MemoryStream(new byte[2 * 1024 * 1024]));
        await firmware.RemoveAsync("gamecube", "ipl.bin");

        Assert.Null(await firmware.OpenAsync("gamecube", "ipl.bin"));
        Assert.False(File.Exists(Path.Combine(root, ".gatherum", "firmware", "gamecube", "ipl.bin")));
    }
}
