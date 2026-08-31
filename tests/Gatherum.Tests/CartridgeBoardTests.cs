using Gatherum.Client.Emulation;
using Gatherum.Client.Emulation.GameBoy;
using Gatherum.Client.Emulation.Nes;

namespace Gatherum.Tests;

/// <summary>The banking hardware. A cartridge bigger than the address space is the
/// normal case on both machines, and a bank register read back one off is a game that
/// jumps into the middle of somebody else's code.</summary>
public class CartridgeBoardTests
{
    /// <summary>An iNES image whose every 16 KB bank begins with its own number.</summary>
    private static byte[] NumberedNesBanks(int banks, int mapper)
    {
        var image = new byte[16 + banks * 16 * 1024 + 8 * 1024];
        image[0] = (byte)'N';
        image[1] = (byte)'E';
        image[2] = (byte)'S';
        image[3] = 0x1A;
        image[4] = (byte)banks;
        image[5] = 1;
        image[6] = (byte)((mapper & 0x0F) << 4 | 0x02);   // battery-backed
        image[7] = (byte)(mapper & 0xF0);
        for (var bank = 0; bank < banks; bank++)
            image[16 + bank * 16 * 1024] = (byte)bank;
        return image;
    }

    /// <summary>MMC1 has no parallel bus: a value arrives one bit at a time, low bit
    /// first, and lands on the fifth write.</summary>
    private static void ShiftIntoMmc1(NesMapper mapper, ushort register, int value)
    {
        for (var bit = 0; bit < 5; bit++)
            mapper.CpuWrite(register, (byte)(value >> bit & 1));
    }

    [Fact]
    public void Mmc1_switches_the_low_window_and_leaves_the_last_bank_alone()
    {
        var console = new NesConsole(NumberedNesBanks(8, 1));
        var mapper = console.Cartridge.Mapper;

        // The register resets to the mode that fixes the last bank at $C000, which is
        // where the vectors live and why every MMC1 game can rely on them.
        Assert.Equal(0, console.CpuRead(0x8000));
        Assert.Equal(7, console.CpuRead(0xC000));

        ShiftIntoMmc1(mapper, 0xE000, 5);
        Assert.Equal(5, console.CpuRead(0x8000));
        Assert.Equal(7, console.CpuRead(0xC000));

        // A write with the high bit set throws the shift register away and restores
        // that mode, whatever was half-written into it.
        mapper.CpuWrite(0xE000, 0x03);
        mapper.CpuWrite(0x8000, 0x80);
        ShiftIntoMmc1(mapper, 0xE000, 2);
        Assert.Equal(2, console.CpuRead(0x8000));
        Assert.Equal(7, console.CpuRead(0xC000));
    }

    [Fact]
    public void Mmc1_reports_the_mirroring_its_control_register_asks_for()
    {
        var console = new NesConsole(NumberedNesBanks(2, 1));
        var mapper = console.Cartridge.Mapper;

        ShiftIntoMmc1(mapper, 0x8000, 0x0C | 0x02);
        Assert.Equal(Mirroring.Vertical, mapper.Mirroring);
        ShiftIntoMmc1(mapper, 0x8000, 0x0C | 0x03);
        Assert.Equal(Mirroring.Horizontal, mapper.Mirroring);
        ShiftIntoMmc1(mapper, 0x8000, 0x0C);
        Assert.Equal(Mirroring.SingleScreenLow, mapper.Mirroring);
    }

    [Fact]
    public void Uxrom_moves_the_low_window_only()
    {
        var console = new NesConsole(NumberedNesBanks(8, 2));
        Assert.Equal(0, console.CpuRead(0x8000));
        Assert.Equal(7, console.CpuRead(0xC000));

        console.CpuWrite(0x8000, 3);
        Assert.Equal(3, console.CpuRead(0x8000));
        Assert.Equal(7, console.CpuRead(0xC000));
    }

    [Fact]
    public void Mmc3_counts_scanlines_down_to_an_interrupt()
    {
        var console = new NesConsole(NumberedNesBanks(8, 4));
        var mapper = console.Cartridge.Mapper;

        mapper.CpuWrite(0xC000, 3);     // count three lines
        mapper.CpuWrite(0xC001, 0);     // reload on the next one
        mapper.CpuWrite(0xE001, 0);     // and let it interrupt

        for (var line = 0; line < 3; line++)
        {
            mapper.SignalScanline();
            Assert.False(mapper.IrqPending);
        }
        mapper.SignalScanline();
        Assert.True(mapper.IrqPending);

        // Acknowledging is a write to the even half of the same pair.
        mapper.CpuWrite(0xE000, 0);
        Assert.False(mapper.IrqPending);
    }

    [Fact]
    public void A_battery_backed_cartridge_hands_its_memory_out_and_takes_it_back()
    {
        var console = new NesConsole(NumberedNesBanks(2, 1));
        Assert.True(console.BatteryBacked);
        Assert.False(console.SaveDirty);

        console.CpuWrite(0x6000, 0x42);
        Assert.True(console.SaveDirty);

        var save = console.SaveRam();
        Assert.Equal(0x42, save[0]);

        var reopened = new NesConsole(NumberedNesBanks(2, 1));
        Assert.Equal(0, reopened.CpuRead(0x6000));
        reopened.LoadSaveRam(save);
        Assert.Equal(0x42, reopened.CpuRead(0x6000));
        Assert.False(reopened.SaveDirty);
    }

    [Fact]
    public void A_cartridge_with_no_battery_has_nothing_to_save()
    {
        var console = new NesConsole(RomFixtures.Nes([0xEA]));
        Assert.False(console.BatteryBacked);
        Assert.Empty(console.SaveRam());
    }

    /// <summary>A Game Boy image whose every 16 KB bank begins with its own number.</summary>
    private static byte[] NumberedGameBoyBanks(int banks, byte cartridgeType)
    {
        var image = RomFixtures.GameBoy([0x00], cartridgeType: cartridgeType);
        Array.Resize(ref image, banks * 0x4000);
        // Bank zero's first byte is the entry point, so the marker goes one past it.
        for (var bank = 1; bank < banks; bank++)
            image[bank * 0x4000] = (byte)bank;
        image[0x148] = (byte)(banks / 2);
        return image;
    }

    [Fact]
    public void Mbc1_switches_the_upper_window_and_never_selects_bank_zero()
    {
        var console = new GameBoyConsole(NumberedGameBoyBanks(8, 0x01));

        // The window starts on bank one, and asking for bank zero gets bank one —
        // which is why a 128 KB cartridge has one bank it can never reach twice.
        Assert.Equal(1, console.ReadByte(0x4000));
        console.WriteByte(0x2000, 0x00);
        Assert.Equal(1, console.ReadByte(0x4000));

        console.WriteByte(0x2000, 0x05);
        Assert.Equal(5, console.ReadByte(0x4000));

        // The low window does not move in the simple mode.
        Assert.Equal(0x00, console.ReadByte(0x0000));
    }

    [Fact]
    public void Mbc5_takes_a_ninth_bank_bit_from_its_own_register()
    {
        var console = new GameBoyConsole(NumberedGameBoyBanks(4, 0x19));
        console.WriteByte(0x2000, 0x02);
        Assert.Equal(2, console.ReadByte(0x4000));
        // MBC5 is the one board where bank zero is selectable.
        console.WriteByte(0x2000, 0x00);
        Assert.Equal(0x00, console.ReadByte(0x4000));
    }

    [Fact]
    public void Cartridge_memory_answers_only_once_it_has_been_switched_on()
    {
        var console = new GameBoyConsole(NumberedGameBoyBanks(4, 0x13));
        Assert.True(console.BatteryBacked);

        // Disabled memory reads as ones and swallows writes, which is what protects a
        // save from a program losing its way on the way to a reset.
        console.WriteByte(0xA000, 0x37);
        Assert.Equal(0xFF, console.ReadByte(0xA000));

        console.WriteByte(0x0000, 0x0A);
        console.WriteByte(0xA000, 0x37);
        Assert.Equal(0x37, console.ReadByte(0xA000));
        Assert.True(console.SaveDirty);

        var save = console.SaveRam();
        var reopened = new GameBoyConsole(NumberedGameBoyBanks(4, 0x13));
        reopened.LoadSaveRam(save);
        reopened.WriteByte(0x0000, 0x0A);
        Assert.Equal(0x37, reopened.ReadByte(0xA000));
    }

    [Fact]
    public void The_pad_never_reports_two_opposite_directions()
    {
        // The plastic made it impossible and a few games crash on it, so the console
        // resolves it rather than passing it on.
        var console = new GameBoyConsole(RomFixtures.GameBoy([0x00]));
        // Both halves of the pad share four lines, and a select line is active low:
        // clearing bit four is what asks for the directions.
        console.WriteByte(0xFF00, 0x20);
        console.SetButtons(0, GamepadButtons.Left | GamepadButtons.Right);

        var lines = console.ReadByte(0xFF00);
        Assert.Equal(0, lines & 0x02);      // left is held
        Assert.Equal(0x01, lines & 0x01);   // right is not
    }
}
