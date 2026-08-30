using System.Collections.Concurrent;
using System.Net.WebSockets;
using Gatherum.Client.Emulation.Netplay;

namespace Gatherum.Web.Services;

/// <summary>Who is playing what, right now. A room is a node — the cartridge everyone
/// in it opened — and the server's whole job inside one is to pass messages along: it
/// stamps who sent each one and forwards it, and never learns what a button does.
///
/// That is deliberate. The console runs in each player's browser, both copies are
/// deterministic, and they stay in step by exchanging inputs rather than pictures. A
/// server that understood the game would be a server that could disagree with it.
///
/// In-process, like presence: one instance serves one room.</summary>
public sealed class PlaySessions
{
    private readonly ConcurrentDictionary<Guid, Room> rooms = new();

    public sealed class Seat(WebSocket socket, Guid userId, string name, int slot)
    {
        public WebSocket Socket { get; } = socket;
        public Guid UserId { get; } = userId;
        public string Name { get; } = name;
        public int Slot { get; } = slot;

        /// <summary>A socket takes one writer at a time, and a relayed input can arrive
        /// while a roster is still going out.</summary>
        public SemaphoreSlim Sending { get; } = new(1, 1);
    }

    private sealed class Room
    {
        public readonly Lock Gate = new();
        public readonly List<Seat> Seats = [];

        /// <summary>Set by whoever arrives first and held until the room empties, so a
        /// second player cannot quietly change the shape of a game in progress.</summary>
        public int Capacity;
        public string RomHash = "";
    }

    public enum JoinResult
    {
        Seated,
        RoomFull,
        DifferentCartridge,
    }

    public (JoinResult Result, Seat? Seat) Join(Guid nodeId, WebSocket socket, Guid userId,
        string name, string romHash, int playerCount)
    {
        var room = rooms.GetOrAdd(nodeId, _ => new Room());
        lock (room.Gate)
        {
            if (room.Seats.Count == 0)
            {
                room.Capacity = playerCount;
                room.RomHash = romHash;
            }
            else if (!string.Equals(room.RomHash, romHash, StringComparison.OrdinalIgnoreCase))
            {
                return (JoinResult.DifferentCartridge, null);
            }

            var slot = 0;
            while (room.Seats.Any(s => s.Slot == slot))
                slot++;
            if (slot >= room.Capacity)
                return (JoinResult.RoomFull, null);

            var seat = new Seat(socket, userId, name, slot);
            room.Seats.Add(seat);
            return (JoinResult.Seated, seat);
        }
    }

    public void Leave(Guid nodeId, Seat seat)
    {
        if (!rooms.TryGetValue(nodeId, out var room))
            return;
        lock (room.Gate)
        {
            room.Seats.Remove(seat);
            if (room.Seats.Count == 0)
                rooms.TryRemove(nodeId, out _);
        }
    }

    public IReadOnlyList<Seat> Occupants(Guid nodeId)
    {
        if (!rooms.TryGetValue(nodeId, out var room))
            return [];
        lock (room.Gate)
            return room.Seats.ToList();
    }

    public int Capacity(Guid nodeId)
    {
        if (!rooms.TryGetValue(nodeId, out var room))
            return 0;
        lock (room.Gate)
            return room.Capacity;
    }

    /// <summary>Passes a message to everyone in the room but its sender, with the
    /// sender's seat stamped on by the server — a client says what it pressed, never
    /// who pressed it.</summary>
    public async Task RelayAsync(Guid nodeId, Seat from, byte[] message, CancellationToken ct)
    {
        PlayProtocol.StampSlot(message, from.Slot);
        foreach (var seat in Occupants(nodeId))
        {
            if (seat == from)
                continue;
            await SendAsync(seat, message, ct);
        }
    }

    public async Task BroadcastRosterAsync(Guid nodeId, CancellationToken ct)
    {
        var seats = Occupants(nodeId);
        var roster = PlayProtocol.Roster(
            seats.Select(s => new PlaySeat(s.Slot, s.Name)).ToList());
        foreach (var seat in seats)
            await SendAsync(seat, roster, ct);
    }

    public static async Task SendAsync(Seat seat, byte[] message, CancellationToken ct)
    {
        if (seat.Socket.State != WebSocketState.Open)
            return;
        await seat.Sending.WaitAsync(ct);
        try
        {
            await seat.Socket.SendAsync(message, WebSocketMessageType.Binary, true, ct);
        }
        catch (Exception)
        {
            // A player closed their tab mid-broadcast. The read loop on their own
            // connection is what notices and clears the seat.
        }
        finally
        {
            seat.Sending.Release();
        }
    }
}
