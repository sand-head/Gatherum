namespace Gatherum.Core.Roms;

/// <summary>What a console needs before any cartridge: the boot ROMs and coprocessor
/// firmware the hardware carried and an emulator cannot ship. Every entry is optional —
/// each console plays without them, on a free replacement or a high-level stand-in —
/// and each one, present, makes a machine more like the real one.
///
/// <para>These belong to the instance rather than to anybody: one copy, uploaded by an
/// admin, kept under the storage root's own <c>.gatherum/system</c> beside nothing of
/// anyone's, and handed to every signed-in player's console. The names are the ones the
/// emulators look for, so a person who has the files already has them spelled right.</para></summary>
public static class SystemFiles
{
    public static readonly IReadOnlyList<SystemConsole> Consoles =
    [
        new("gamecube", "GameCube",
        [
            new("IPL.bin", 0x20_0000,
                "The boot ROM of an NTSC console, as dumped (encoded) or decoded. With it a " +
                "disc boots the way the hardware boots one, and a game can read the " +
                "console's font and settings; without it a free stand-in boots the disc."),
            new("PAL_IPL.bin", 0x20_0000,
                "The same for a PAL console. A disc is booted with the ROM of its own region."),
            new("dsp_rom.bin", 0x2000,
                "The sound processor's boot ROM. Dolphin's free replacement is built in " +
                "and used until this is uploaded."),
            new("dsp_coef.bin", 0x1000,
                "The sound processor's coefficient table, which goes with the ROM above."),
        ]),
        new("gba", "Game Boy Advance",
        [
            new("gba_bios.bin", 0x4000,
                "The console's BIOS. Without it the emulator stands in for it, which a few " +
                "games notice; with it the boot logo plays and every game sees what it " +
                "expects."),
        ]),
        new("snes", "Super Nintendo",
        [
            new("dsp1.program.rom", 0x1800, "DSP-1 coprocessor program (Pilotwings)."),
            new("dsp1.data.rom", 0x800, "DSP-1 coprocessor data."),
            new("dsp1b.program.rom", 0x1800,
                "DSP-1B coprocessor program (Super Mario Kart and most other DSP-1 games)."),
            new("dsp1b.data.rom", 0x800, "DSP-1B coprocessor data."),
            new("dsp2.program.rom", 0x1800, "DSP-2 coprocessor program (Dungeon Master)."),
            new("dsp2.data.rom", 0x800, "DSP-2 coprocessor data."),
            new("dsp3.program.rom", 0x1800, "DSP-3 coprocessor program (SD Gundam GX)."),
            new("dsp3.data.rom", 0x800, "DSP-3 coprocessor data."),
            new("dsp4.program.rom", 0x1800, "DSP-4 coprocessor program (Top Gear 3000)."),
            new("dsp4.data.rom", 0x800, "DSP-4 coprocessor data."),
            new("st010.program.rom", 0xC000, "ST010 coprocessor program (F1 ROC II)."),
            new("st010.data.rom", 0x1000, "ST010 coprocessor data."),
            new("st011.program.rom", 0xC000,
                "ST011 coprocessor program (Hayazashi Nidan Morita Shougi)."),
            new("st011.data.rom", 0x1000, "ST011 coprocessor data."),
            new("st018.program.rom", 0x2_0000,
                "ST018 coprocessor program (Hayazashi Nidan Morita Shougi 2)."),
            new("st018.data.rom", 0x8000, "ST018 coprocessor data."),
            new("cx4.data.rom", 0xC00,
                "Cx4 coprocessor data (Mega Man X2 and X3). A cartridge dump with the " +
                "firmware appended, as many are, needs none of these."),
        ]),
    ];

    /// <summary>The console with this key, or null: keys are what the API and the
    /// player spell, and an unknown one is a 404 rather than a directory.</summary>
    public static SystemConsole? FindConsole(string key) =>
        Consoles.FirstOrDefault(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The slot a file goes in, or null for a name no console here asks for —
    /// which is the whole of the validation a filename gets: a name is either in this
    /// table, exactly, or it is not stored.</summary>
    public static SystemFileSlot? FindSlot(string console, string name) =>
        FindConsole(console)?.Files.FirstOrDefault(f => f.Name == name);
}

/// <param name="Key">How the API and the player name the console — short, lowercase,
/// and the directory the files are kept in.</param>
public sealed record SystemConsole(string Key, string Name, IReadOnlyList<SystemFileSlot> Files);

/// <param name="Name">The filename the emulator looks for, spelled as it looks.</param>
/// <param name="Bytes">Exactly how long the file is. A ROM is a fixed piece of silicon,
/// so a file of another length is not it, and refusing one says so before an emulator
/// silently ignores it.</param>
public sealed record SystemFileSlot(string Name, long Bytes, string Purpose);
