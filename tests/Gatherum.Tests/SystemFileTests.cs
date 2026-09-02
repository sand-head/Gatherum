using Gatherum.Core;
using Gatherum.Core.Roms;
using Gatherum.Core.Services;

namespace Gatherum.Tests;

/// <summary>The consoles' system files: a catalog of what each can take, a directory
/// under the storage root's own <c>.gatherum</c> holding what this instance has, and
/// an admin's hand on the only writes.</summary>
[Collection("postgres")]
public class SystemFileTests(PostgresFixture postgres) : IAsyncLifetime
{
    private ServiceHarness harness = null!;
    private Guid admin;
    private Guid member;

    public async Task InitializeAsync()
    {
        harness = new ServiceHarness(await postgres.CreateDatabaseAsync());
        admin = await harness.AddUserAsync("admin");
        member = await harness.AddUserAsync("member");
        (await harness.Db.Users.FindAsync(admin))!.IsAdmin = true;
        await harness.Db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await harness.DisposeAsync();

    [Fact]
    public void The_catalog_names_each_file_once_and_sizes_it_as_the_hardware_does()
    {
        Assert.Equal(SystemFiles.Consoles.Count, SystemFiles.Consoles.Select(c => c.Key).Distinct().Count());
        foreach (var console in SystemFiles.Consoles)
        {
            Assert.Equal(console.Files.Count, console.Files.Select(f => f.Name).Distinct().Count());
            Assert.All(console.Files, f => Assert.True(f.Bytes > 0, $"{f.Name} has no size."));
        }
        Assert.Equal(0x20_0000, SystemFiles.FindSlot("gamecube", "IPL.bin")!.Bytes);
        Assert.Equal(0x4000, SystemFiles.FindSlot("gba", "gba_bios.bin")!.Bytes);
        Assert.Null(SystemFiles.FindSlot("gba", "GBA_BIOS.BIN"));
        Assert.Null(SystemFiles.FindSlot("n64", "pifdata.bin"));
    }

    [Fact]
    public async Task An_admin_stores_a_file_and_everybody_lists_and_reads_it()
    {
        var bios = Bytes(0x4000);
        var stored = await harness.SystemFiles.PutAsync(admin, "gba", "gba_bios.bin",
            new MemoryStream(bios));
        Assert.True(stored.Present);
        Assert.Equal(0x4000, stored.SizeBytes);

        var gba = (await harness.SystemFiles.ListAsync()).Single(c => c.Key == "gba");
        var slot = Assert.Single(gba.Files);
        Assert.True(slot.Present);
        Assert.Equal(stored.Sha256, slot.Sha256);
        Assert.Equal(["gba_bios.bin"], await harness.SystemFiles.PresentAsync("gba"));
        Assert.Empty(await harness.SystemFiles.PresentAsync("snes"));

        await using var read = await harness.SystemFiles.OpenAsync("gba", "gba_bios.bin");
        var back = new MemoryStream();
        await read.CopyToAsync(back);
        Assert.Equal(bios, back.ToArray());

        // On disk where the manual says, under the root's own bookkeeping directory —
        // and so outside every owner's root and every scan.
        Assert.True(File.Exists(Path.Combine(harness.StorageRoot, ".gatherum", "system", "gba",
            "gba_bios.bin")));
        Assert.DoesNotContain(".gatherum", harness.Storage.Roots());
    }

    [Fact]
    public async Task A_member_may_read_but_neither_store_nor_remove()
    {
        await harness.SystemFiles.PutAsync(admin, "gba", "gba_bios.bin",
            new MemoryStream(Bytes(0x4000)));

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            harness.SystemFiles.PutAsync(member, "gba", "gba_bios.bin",
                new MemoryStream(Bytes(0x4000))));
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            harness.SystemFiles.DeleteAsync(member, "gba", "gba_bios.bin"));

        await using var read = await harness.SystemFiles.OpenAsync("gba", "gba_bios.bin");
        Assert.True(read.CanRead);
        Assert.Equal(["gba_bios.bin"], await harness.SystemFiles.PresentAsync("gba"));
    }

    [Fact]
    public async Task A_file_of_the_wrong_length_is_refused_with_both_lengths()
    {
        var refused = await Assert.ThrowsAsync<ValidationException>(() =>
            harness.SystemFiles.PutAsync(admin, "gba", "gba_bios.bin",
                new MemoryStream(Bytes(0x4001))));
        Assert.Contains("16,384", refused.Message);
        Assert.Contains("16,385", refused.Message);
        Assert.Empty(await harness.SystemFiles.PresentAsync("gba"));
    }

    [Fact]
    public async Task A_name_no_console_asks_for_is_not_a_slot()
    {
        await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.SystemFiles.PutAsync(admin, "gba", "../../evil.bin", new MemoryStream(Bytes(1))));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.SystemFiles.PutAsync(admin, "playstation", "scph1001.bin", new MemoryStream(Bytes(1))));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.SystemFiles.OpenAsync("gba", "gba_bios.bin"));
    }

    [Fact]
    public async Task Removing_a_file_leaves_the_slot_empty_and_replacing_one_replaces_it()
    {
        await harness.SystemFiles.PutAsync(admin, "snes", "cx4.data.rom",
            new MemoryStream(Bytes(0xC00, 1)));
        var replaced = await harness.SystemFiles.PutAsync(admin, "snes", "cx4.data.rom",
            new MemoryStream(Bytes(0xC00, 2)));
        await using (var read = await harness.SystemFiles.OpenAsync("snes", "cx4.data.rom"))
        {
            Assert.Equal(2, read.ReadByte());
        }
        Assert.NotNull(replaced.Sha256);

        await harness.SystemFiles.DeleteAsync(admin, "snes", "cx4.data.rom");
        Assert.Empty(await harness.SystemFiles.PresentAsync("snes"));
        // Removing what is already gone is not an error: the slot is empty either way.
        await harness.SystemFiles.DeleteAsync(admin, "snes", "cx4.data.rom");
    }

    private static byte[] Bytes(int length, byte fill = 0x5A)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, fill);
        return bytes;
    }
}
