namespace Gatherum.Client.Emulation.Sega;

/// <summary>A Master System cartridge and the paging hardware on it.
///
/// Sega's own board is the common one: three sixteen-kilobyte windows into the ROM, and
/// registers for them at the very top of the address space — where they sit *inside*
/// work memory, so a write to $FFFD both stores a byte and moves a bank. The first
/// kilobyte never pages, because that is where the interrupt vectors live and a program
/// that banked them away could not return from anything.
///
/// Codemasters built their own, cheaper, board where the banks are chosen by writing to
/// the windows themselves. Nothing in the file declares which one is fitted, so the
/// checksum their tools wrote is what gives it away.</summary>
public sealed class SegaCartridge
{
    private const int BankSize = 0x4000;

    private readonly byte[] rom;
    private readonly int bankCount;

    /// <summary>Two eight-kilobyte banks, which is as much as any cartridge carried.</summary>
    private readonly byte[] cartridgeRam = new byte[2 * 0x2000];

    private readonly int[] banks = [0, 1, 2];
    private bool ramEnabled;
    private int ramBank;

    public bool Codemasters { get; }
    public bool Battery { get; private set; }
    public bool SaveDirty { get; private set; }

    public SegaCartridge(ReadOnlySpan<byte> image)
    {
        // Some dumps carry a 512-byte header from the copier that made them, which is
        // not part of the cartridge and would push every bank half a kilobyte along.
        if (image.Length % BankSize == 512)
            image = image[512..];

        if (image.Length == 0)
            throw new InvalidDataException("This cartridge image is empty.");

        // Banking is done by masking, so the ROM has to be a whole number of banks and
        // a power of two of them.
        var banksNeeded = Math.Max(1, (image.Length + BankSize - 1) / BankSize);
        var rounded = 1;
        while (rounded < banksNeeded)
            rounded <<= 1;

        bankCount = rounded;
        rom = new byte[rounded * BankSize];
        image.CopyTo(rom);
        // A short last bank reads as the open bus the hardware would have shown.
        rom.AsSpan(image.Length).Fill(0xFF);

        Codemasters = LooksLikeCodemasters(rom);
        banks[2] = Codemasters ? 0 : 2 % bankCount;
    }

    /// <summary>Codemasters' tools wrote a checksum and its complement where Sega's
    /// header would have been. Nothing else in the file distinguishes the two boards,
    /// and paging one as the other hangs on the title screen.</summary>
    private static bool LooksLikeCodemasters(byte[] rom)
    {
        if (rom.Length < 0x8000)
            return false;
        var checksum = rom[0x7FE6] | rom[0x7FE7] << 8;
        var complement = rom[0x7FE8] | rom[0x7FE9] << 8;
        return checksum != 0 && (checksum + complement & 0xFFFF) == 0;
    }

    public void Reset()
    {
        banks[0] = 0;
        banks[1] = 1 % bankCount;
        banks[2] = Codemasters ? 0 : 2 % bankCount;
        ramEnabled = false;
        ramBank = 0;
    }

    public byte Read(ushort address)
    {
        switch (address)
        {
            // The vectors, and the handler the interrupt reaches, are never paged out.
            case < 0x0400 when !Codemasters:
                return rom[address];
            case < 0x4000:
                return rom[banks[0] * BankSize + address];
            case < 0x8000:
                return rom[banks[1] * BankSize + (address - 0x4000)];
            default:
                if (ramEnabled)
                    return cartridgeRam[ramBank * 0x2000 + (address - 0x8000 & 0x1FFF)];
                return rom[banks[2] * BankSize + (address - 0x8000)];
        }
    }

    public void Write(ushort address, byte value)
    {
        if (Codemasters)
        {
            switch (address)
            {
                case 0x0000: banks[0] = value % bankCount; return;
                case 0x4000: banks[1] = value % bankCount; return;
                case 0x8000: banks[2] = value % bankCount; return;
            }
        }

        if (address >= 0x8000 && address < 0xC000 && ramEnabled)
        {
            cartridgeRam[ramBank * 0x2000 + (address - 0x8000 & 0x1FFF)] = value;
            SaveDirty = true;
        }
    }

    /// <summary>The four registers at the top of memory. The console passes these on
    /// after storing the byte in work memory, because on the hardware both happen.</summary>
    public void WriteControl(ushort address, byte value)
    {
        if (Codemasters)
            return;
        switch (address)
        {
            case 0xFFFC:
                ramEnabled = (value & 0x08) != 0;
                ramBank = value >> 2 & 1;
                // A cartridge with memory to page in is a cartridge with a battery;
                // nothing else would have put it there.
                if (ramEnabled)
                    Battery = true;
                return;
            case 0xFFFD: banks[0] = value % bankCount; return;
            case 0xFFFE: banks[1] = value % bankCount; return;
            case 0xFFFF: banks[2] = value % bankCount; return;
        }
    }

    public byte[] SaveRam() => Battery ? cartridgeRam.ToArray() : [];

    public void LoadSaveRam(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            return;
        data[..Math.Min(data.Length, cartridgeRam.Length)].CopyTo(cartridgeRam);
        Battery = true;
        SaveDirty = false;
    }

    public void MarkSaved() => SaveDirty = false;

    internal void Save(ref StateWriter state)
    {
        state.Write(cartridgeRam);
        state.Write(banks);
        state.Write(ramEnabled);
        state.Write(ramBank);
        state.Write(Battery);
    }

    internal void Load(ref StateReader state)
    {
        state.Read(cartridgeRam);
        state.Read(banks);
        ramEnabled = state.ReadBool();
        ramBank = state.ReadInt32();
        Battery = state.ReadBool();
        for (var slot = 0; slot < banks.Length; slot++)
            banks[slot] = ((banks[slot] % bankCount) + bankCount) % bankCount;
    }
}
