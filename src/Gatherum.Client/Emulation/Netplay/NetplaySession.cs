using System.Net.WebSockets;

namespace Gatherum.Client.Emulation.Netplay;

/// <summary>One player's end of a shared game. It holds the socket, remembers what
/// everybody has said they will be pressing, and answers the one question the player
/// component asks every frame: <em>may I run frame N yet?</em>
///
/// Nothing here knows what a button does. The rule is only that a frame runs when every
/// player's buttons for it have arrived, which is what keeps two deterministic machines
/// showing the same picture without either of them sending one.</summary>
public sealed class NetplaySession : IAsyncDisposable
{
    private readonly WebSocket socket;
    private readonly CancellationTokenSource closing = new();
    private readonly Lock gate = new();

    private readonly Dictionary<int, GamepadButtons>[] inputs;
    private readonly Dictionary<int, ulong>[] checksums;
    private (int Frame, byte[] Machine)? arrivingState;

    private NetplaySession(WebSocket socket)
    {
        this.socket = socket;
        inputs = new Dictionary<int, GamepadButtons>[PlayProtocol.MaxPlayers];
        checksums = new Dictionary<int, ulong>[PlayProtocol.MaxPlayers];
        for (var slot = 0; slot < PlayProtocol.MaxPlayers; slot++)
        {
            inputs[slot] = [];
            checksums[slot] = [];
        }
    }

    /// <summary>Which pad this player is. Slot zero is whoever arrived first, and is the
    /// one that hands its machine to anybody who joins a game already going.</summary>
    public int Slot { get; private set; }

    /// <summary>How many the room holds — the console's number of ports.</summary>
    public int Capacity { get; private set; } = 1;

    public IReadOnlyList<PlaySeat> Seats { get; private set; } = [];

    /// <summary>Why this stopped working, when it has. Set once and never cleared: a
    /// session that has faulted is over.</summary>
    public string? Fault { get; private set; }

    public bool Connected => socket.State == WebSocketState.Open && Fault is null;

    /// <summary>Whether every seat is taken. Until then there is nobody to be in step
    /// with and the game runs on its own.</summary>
    public bool Full => Seats.Count >= Capacity;

    public bool IsHost => Slot == 0;

    /// <summary>Raised when the roster changes or the session faults, so the page can
    /// say who is here.</summary>
    public event Action? Changed;

    public static async Task<NetplaySession> ConnectAsync(Uri endpoint, string romHash,
        int playerCount, CancellationToken cancellationToken = default)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(endpoint, cancellationToken);
        return await AttachAsync(socket, romHash, playerCount, cancellationToken);
    }

    /// <summary>Takes over a socket somebody else opened — which is how the tests reach
    /// a server that is not listening on a port.</summary>
    public static async Task<NetplaySession> AttachAsync(WebSocket socket, string romHash,
        int playerCount, CancellationToken cancellationToken = default)
    {
        var session = new NetplaySession(socket);
        await socket.SendAsync(PlayProtocol.Join(romHash, playerCount),
            WebSocketMessageType.Binary, true, cancellationToken);
        _ = session.ListenAsync();
        return session;
    }

    /// <summary>Commits this player's buttons for a frame that has not happened yet.
    /// The delay is the whole trick: by the time frame N comes round, the other machine
    /// has had three frames' worth of time to hear about it.</summary>
    public void SendInput(int frame, GamepadButtons buttons)
    {
        lock (gate)
        {
            if (!inputs[Slot].TryAdd(frame, buttons))
                return;
        }
        Post(PlayProtocol.Input(Slot, frame, buttons));
    }

    /// <summary>Whether every player's buttons for this frame are in hand, and if so
    /// what they are. False means wait — somebody's connection is behind.</summary>
    public bool TryCollect(int frame, Span<GamepadButtons> into)
    {
        lock (gate)
        {
            for (var slot = 0; slot < Capacity; slot++)
            {
                if (!inputs[slot].TryGetValue(frame, out var buttons))
                    return false;
                if (slot < into.Length)
                    into[slot] = buttons;
            }
            return true;
        }
    }

    /// <summary>Which other player everybody is waiting on, for the page to say so.
    /// Never this one: a player's own buttons are in hand the moment they press them,
    /// so a stall is always somebody else's connection.</summary>
    public string? WaitingFor(int frame)
    {
        lock (gate)
        {
            for (var slot = 0; slot < Capacity; slot++)
            {
                if (slot == Slot || inputs[slot].ContainsKey(frame))
                    continue;
                var seat = Seats.FirstOrDefault(s => s.Slot == slot);
                return seat.Name is { Length: > 0 } name ? name : "the other player";
            }
        }
        return null;
    }

    public void SendChecksum(int frame, ulong fingerprint)
    {
        lock (gate)
            checksums[Slot][frame] = fingerprint;
        Post(PlayProtocol.Checksum(Slot, frame, fingerprint));
    }

    /// <summary>Whether anybody's machine has drifted from this one. Only answers for
    /// frames both have reported, and only once — a divergence is reported and the
    /// session is done.</summary>
    public bool HasDiverged(int frame)
    {
        lock (gate)
        {
            if (!checksums[Slot].TryGetValue(frame, out var mine))
                return false;
            for (var slot = 0; slot < Capacity; slot++)
            {
                if (slot == Slot || !checksums[slot].TryGetValue(frame, out var theirs))
                    continue;
                if (theirs == mine)
                    continue;
                Fail("The two games have drifted apart, so this one has stopped. " +
                    "Starting again will put you both back in step.");
                return true;
            }
        }
        return false;
    }

    public void SendState(int frame, ReadOnlySpan<byte> machine) =>
        Post(PlayProtocol.State(frame, machine));

    /// <summary>The host's machine, when one has arrived — this is what lets somebody
    /// join a game that is already going rather than at the title screen.</summary>
    public (int Frame, byte[] Machine)? TakeArrivingState()
    {
        lock (gate)
        {
            var state = arrivingState;
            arrivingState = null;
            return state;
        }
    }

    /// <summary>Drops what everyone said about frames already played. Without this a
    /// long game keeps every button either of them ever pressed.</summary>
    public void Forget(int beforeFrame)
    {
        lock (gate)
        {
            foreach (var slot in inputs)
            {
                foreach (var frame in slot.Keys.Where(f => f < beforeFrame).ToList())
                    slot.Remove(frame);
            }
            foreach (var slot in checksums)
            {
                foreach (var frame in slot.Keys.Where(f => f < beforeFrame).ToList())
                    slot.Remove(frame);
            }
        }
    }

    private void Post(byte[] message)
    {
        if (socket.State != WebSocketState.Open)
            return;
        _ = SendAsync(message);
    }

    private async Task SendAsync(byte[] message)
    {
        try
        {
            await socket.SendAsync(message, WebSocketMessageType.Binary, true, closing.Token);
        }
        catch (Exception)
        {
            // Nothing is waiting on this task, so an exception escaping it would go
            // unobserved rather than anywhere useful. A send that failed means the
            // connection is gone, whatever the transport called it.
            Fail("The connection to the other player dropped.");
        }
    }

    private async Task ListenAsync()
    {
        var buffer = new byte[1024 * 1024];
        try
        {
            while (socket.State == WebSocketState.Open && !closing.IsCancellationRequested)
            {
                var total = 0;
                ValueWebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(buffer.AsMemory(total), closing.Token);
                    if (received.MessageType == WebSocketMessageType.Close)
                        return;
                    total += received.Count;
                } while (!received.EndOfMessage && total < buffer.Length);

                if (total > 0)
                    Receive(buffer.AsMemory(0, total));
            }
        }
        catch (Exception)
        {
            // Likewise: this loop runs unattended for the life of the session, so it
            // reports a broken connection rather than throwing into nowhere.
            if (!closing.IsCancellationRequested)
                Fail("The connection to the other player dropped.");
        }
    }

    private void Receive(ReadOnlyMemory<byte> message)
    {
        switch ((PlayMessage)message.Span[0])
        {
            case PlayMessage.Welcome:
                (Slot, Capacity) = PlayProtocol.ReadWelcome(message.Span);
                Changed?.Invoke();
                break;

            case PlayMessage.Roster:
                Seats = PlayProtocol.ReadRoster(message.Span);
                Changed?.Invoke();
                break;

            case PlayMessage.Input:
            {
                var (slot, frame, buttons) = PlayProtocol.ReadInput(message.Span);
                if (frame >= 0 && slot < PlayProtocol.MaxPlayers)
                {
                    lock (gate)
                        inputs[slot][frame] = buttons;
                }
                break;
            }

            case PlayMessage.Checksum:
            {
                var (slot, frame, hash) = PlayProtocol.ReadChecksum(message.Span);
                if (frame >= 0 && slot < PlayProtocol.MaxPlayers)
                {
                    lock (gate)
                        checksums[slot][frame] = hash;
                }
                break;
            }

            case PlayMessage.State:
            {
                var (frame, machine) = PlayProtocol.ReadState(message);
                if (frame >= 0)
                {
                    lock (gate)
                        arrivingState = (frame, machine.ToArray());
                }
                break;
            }

            case PlayMessage.Fault:
                Fail(PlayProtocol.ReadFault(message.Span));
                break;
        }
    }

    private void Fail(string reason)
    {
        Fault ??= reason;
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        await closing.CancelAsync();
        try
        {
            // Output only: a full close waits for the other end to answer, and the one
            // thing a player leaving a game should never do is block on the server
            // being ready to say goodbye.
            if (socket.State == WebSocketState.Open)
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "",
                    CancellationToken.None);
        }
        catch (Exception)
        {
            // Leaving is leaving.
        }
        socket.Dispose();
        closing.Dispose();
    }
}
