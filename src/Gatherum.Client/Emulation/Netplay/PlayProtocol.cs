using System.Text;

namespace Gatherum.Client.Emulation.Netplay;

/// <summary>What the two halves of a shared game say to each other. The server relays
/// these and understands only the envelope — which player sent it, and whether the room
/// has room — so the rules of the game stay in the one place that has ever known them:
/// the console, running in each player's browser.
///
/// It lives in this project rather than in the web one because the client is the half
/// that has to encode it, and the server already reads this project's types the same
/// way it implements <see cref="IAppData"/>.
///
/// Binary, because sixty of these a second per player is not the place for JSON.</summary>
public enum PlayMessage : byte
{
    /// <summary>Client to server, once: which cartridge I have, by its hash, and how
    /// many pads the console it is for has.</summary>
    Join = 1,

    /// <summary>Server to client: which pad you are, and how many the console has.</summary>
    Welcome = 2,

    /// <summary>Server to client: who is in the room now.</summary>
    Roster = 3,

    /// <summary>Either way: the buttons one player will be holding on one frame. The
    /// server stamps the slot itself — a client says what it pressed, never who it
    /// is.</summary>
    Input = 4,

    /// <summary>Either way: a fingerprint of one player's machine at one frame. If two
    /// disagree the game has diverged, and saying so beats playing on.</summary>
    Checksum = 5,

    /// <summary>Either way: a whole machine, so somebody arriving mid-game starts
    /// inside it rather than at the title screen.</summary>
    State = 6,

    /// <summary>Server to client: why this is not going to work.</summary>
    Fault = 7,
}

/// <summary>One player as the room sees them.</summary>
public readonly record struct PlaySeat(int Slot, string Name);

public static class PlayProtocol
{
    /// <summary>How many frames ahead a player commits their buttons. The other machine
    /// needs them before it can run that frame, so this is the round trip the network is
    /// allowed to take — three frames is about fifty milliseconds, which is enough for a
    /// relay on the same continent and small enough to be hard to feel.</summary>
    public const int InputDelay = 3;

    /// <summary>How often the two machines compare notes. Every frame would be
    /// wasteful; a divergence that takes a second to notice is still noticed.</summary>
    public const int ChecksumInterval = 60;

    /// <summary>Rooms are small on purpose: what the console has ports for.</summary>
    public const int MaxPlayers = 4;

    public static byte[] Join(string romHash, int playerCount)
    {
        var hash = Encoding.ASCII.GetBytes(romHash);
        var message = new byte[2 + hash.Length];
        message[0] = (byte)PlayMessage.Join;
        message[1] = (byte)Math.Clamp(playerCount, 1, MaxPlayers);
        hash.CopyTo(message, 2);
        return message;
    }

    /// <summary>How many the room holds is the console's answer, not the server's: the
    /// server has never known which machine a file is for and should not start now. The
    /// first player through the door sets it, and anyone who lies about it only spoils
    /// their own game.</summary>
    public static (string RomHash, int PlayerCount) ReadJoin(ReadOnlySpan<byte> message) =>
        message.Length <= 2
            ? ("", 1)
            : (Encoding.ASCII.GetString(message[2..]), Math.Clamp((int)message[1], 1, MaxPlayers));

    public static byte[] Welcome(int slot, int playerCount) =>
        [(byte)PlayMessage.Welcome, (byte)slot, (byte)playerCount];

    public static (int Slot, int PlayerCount) ReadWelcome(ReadOnlySpan<byte> message) =>
        message.Length < 3 ? (0, 1) : (message[1], message[2]);

    public static byte[] Roster(IReadOnlyList<PlaySeat> seats)
    {
        var body = new List<byte> { (byte)PlayMessage.Roster, (byte)seats.Count };
        foreach (var seat in seats)
        {
            var name = Encoding.UTF8.GetBytes(seat.Name);
            body.Add((byte)seat.Slot);
            body.Add((byte)Math.Min(name.Length, 255));
            body.AddRange(name.AsSpan(0, Math.Min(name.Length, 255)).ToArray());
        }
        return [.. body];
    }

    public static IReadOnlyList<PlaySeat> ReadRoster(ReadOnlySpan<byte> message)
    {
        if (message.Length < 2)
            return [];
        var seats = new List<PlaySeat>();
        var at = 2;
        for (var index = 0; index < message[1] && at + 1 < message.Length; index++)
        {
            var slot = message[at];
            var length = message[at + 1];
            at += 2;
            if (at + length > message.Length)
                break;
            seats.Add(new PlaySeat(slot, Encoding.UTF8.GetString(message.Slice(at, length))));
            at += length;
        }
        return seats;
    }

    public static byte[] Input(int slot, int frame, GamepadButtons buttons) =>
    [
        (byte)PlayMessage.Input, (byte)slot,
        (byte)frame, (byte)(frame >> 8), (byte)(frame >> 16), (byte)(frame >> 24),
        // Two bytes, not one: a console with shoulder buttons does not fit in eight bits.
        (byte)buttons, (byte)((int)buttons >> 8),
    ];

    public static (int Slot, int Frame, GamepadButtons Buttons) ReadInput(ReadOnlySpan<byte> message) =>
        message.Length < 8
            ? (0, -1, GamepadButtons.None)
            : (message[1], ReadInt(message[2..]), (GamepadButtons)(message[6] | message[7] << 8));

    public static byte[] Checksum(int slot, int frame, ulong hash)
    {
        var message = new byte[14];
        message[0] = (byte)PlayMessage.Checksum;
        message[1] = (byte)slot;
        WriteInt(message.AsSpan(2), frame);
        for (var index = 0; index < 8; index++)
            message[6 + index] = (byte)(hash >> (index * 8));
        return message;
    }

    public static (int Slot, int Frame, ulong Hash) ReadChecksum(ReadOnlySpan<byte> message)
    {
        if (message.Length < 14)
            return (0, -1, 0);
        ulong hash = 0;
        for (var index = 0; index < 8; index++)
            hash |= (ulong)message[6 + index] << (index * 8);
        return (message[1], ReadInt(message[2..]), hash);
    }

    public static byte[] State(int frame, ReadOnlySpan<byte> machine)
    {
        var message = new byte[6 + machine.Length];
        message[0] = (byte)PlayMessage.State;
        WriteInt(message.AsSpan(2), frame);
        machine.CopyTo(message.AsSpan(6));
        return message;
    }

    public static (int Frame, ReadOnlyMemory<byte> Machine) ReadState(ReadOnlyMemory<byte> message) =>
        message.Length < 6 ? (-1, default) : (ReadInt(message.Span[2..]), message[6..]);

    public static byte[] Fault(string reason)
    {
        var text = Encoding.UTF8.GetBytes(reason);
        var message = new byte[1 + text.Length];
        message[0] = (byte)PlayMessage.Fault;
        text.CopyTo(message, 1);
        return message;
    }

    public static string ReadFault(ReadOnlySpan<byte> message) =>
        message.Length <= 1 ? "The session ended." : Encoding.UTF8.GetString(message[1..]);

    /// <summary>Whichever slot a relayed message claims, the server's word replaces it:
    /// a client may say what it pressed and never who pressed it.</summary>
    public static void StampSlot(Span<byte> message, int slot)
    {
        if (message.Length >= 2 && message[0] is (byte)PlayMessage.Input
            or (byte)PlayMessage.Checksum)
            message[1] = (byte)slot;
    }

    /// <summary>FNV-1a over a save state. Not a cryptographic hash and does not need to
    /// be — it is two machines checking they still agree, not defending against one
    /// that lies. A player who wants to cheat has an emulator of their own.</summary>
    public static ulong Fingerprint(ReadOnlySpan<byte> state)
    {
        var hash = 14695981039346656037UL;
        foreach (var value in state)
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }
        return hash;
    }

    private static int ReadInt(ReadOnlySpan<byte> span) =>
        span.Length < 4 ? -1 : span[0] | span[1] << 8 | span[2] << 16 | span[3] << 24;

    private static void WriteInt(Span<byte> span, int value)
    {
        span[0] = (byte)value;
        span[1] = (byte)(value >> 8);
        span[2] = (byte)(value >> 16);
        span[3] = (byte)(value >> 24);
    }
}
