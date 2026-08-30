namespace Gatherum.Client.Emulation.Nes;

/// <summary>How the cartridge wires the console's two kilobytes of nametable memory —
/// which is what decides whether the screen scrolls sideways or up and down without
/// tearing.</summary>
public enum Mirroring
{
    Horizontal,
    Vertical,
    SingleScreenLow,
    SingleScreenHigh,
    FourScreen,
}

/// <summary>An iNES file, opened. The header is sixteen bytes and says how much program
/// ROM and pattern ROM follow and which mapper — the extra logic on the board — sits
/// between them and the console.</summary>
public sealed class NesCartridge
{
    public byte[] Program { get; }
    public byte[] Characters { get; }
    public byte[] ProgramRam { get; }
    public bool CharactersAreRam { get; }
    public bool Battery { get; }
    public Mirroring HeaderMirroring { get; }
    public int MapperNumber { get; }
    public NesMapper Mapper { get; private set; } = null!;

    private NesCartridge(byte[] program, byte[] characters, byte[] programRam,
        bool charactersAreRam, bool battery, Mirroring mirroring, int mapper)
    {
        Program = program;
        Characters = characters;
        ProgramRam = programRam;
        CharactersAreRam = charactersAreRam;
        Battery = battery;
        HeaderMirroring = mirroring;
        MapperNumber = mapper;
    }

    public static NesCartridge Load(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 16 || bytes[0] != 'N' || bytes[1] != 'E' || bytes[2] != 'S'
            || bytes[3] != 0x1A)
            throw new NotSupportedException("This is not an iNES cartridge image.");

        var flags6 = bytes[6];
        var flags7 = bytes[7];
        var nes20 = (flags7 & 0x0C) == 0x08;
        var mapper = (flags6 >> 4) | (flags7 & 0xF0) | (nes20 ? (bytes[8] & 0x0F) << 8 : 0);

        var programBanks = bytes[4] | (nes20 ? (bytes[9] & 0x0F) << 8 : 0);
        var characterBanks = bytes[5] | (nes20 ? (bytes[9] & 0xF0) << 4 : 0);
        if (programBanks == 0)
            throw new NotSupportedException("This cartridge image has no program ROM.");

        // A trainer is 512 bytes of patch code some dumps carry ahead of the program.
        var offset = 16 + ((flags6 & 0x04) != 0 ? 512 : 0);
        var programBytes = programBanks * 16 * 1024;
        var characterBytes = characterBanks * 8 * 1024;
        if (bytes.Length < offset + programBytes)
            throw new NotSupportedException("This cartridge image is truncated.");

        var program = bytes.Slice(offset, programBytes).ToArray();
        var characters = characterBytes == 0
            ? new byte[8 * 1024]
            : bytes.Slice(offset + programBytes,
                Math.Min(characterBytes, bytes.Length - offset - programBytes)).ToArray();
        if (characters.Length < characterBytes)
            Array.Resize(ref characters, characterBytes);

        var mirroring = (flags6 & 0x08) != 0 ? Mirroring.FourScreen
            : (flags6 & 0x01) != 0 ? Mirroring.Vertical
            : Mirroring.Horizontal;

        var cartridge = new NesCartridge(program, characters, new byte[8 * 1024],
            characterBanks == 0, (flags6 & 0x02) != 0, mirroring, mapper);
        cartridge.Mapper = NesMapper.Create(cartridge);
        return cartridge;
    }
}
