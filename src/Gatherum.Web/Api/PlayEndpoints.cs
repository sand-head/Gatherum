using System.Net.WebSockets;
using Gatherum.Client.Emulation.Netplay;
using Gatherum.Core.Services;
using Gatherum.Web.Auth;
using Gatherum.Web.Services;

namespace Gatherum.Web.Api;

/// <summary>The one socket in the app. Two people playing the same cartridge exchange
/// their buttons through here, and nothing else: the machines are in their browsers and
/// stay in step because both are deterministic, so what crosses the wire is a handful of
/// bytes a frame rather than a picture.
///
/// It is not a REST endpoint and does not pretend to be, but it obeys the same rules —
/// a room is a node, so who may enter one is the question
/// <see cref="Core.Abstractions.INodeAuthorizer"/> already answers, and there is no
/// anonymous door: sending your buttons into somebody else's game is not reading.</summary>
public static class PlayEndpoints
{
    /// <summary>A player's messages are tiny; a state handed to somebody joining a game
    /// in progress is not. This bounds the largest of them.</summary>
    private const int MaxMessageBytes = 4 * 1024 * 1024;

    public static void MapPlayEndpoint(this RouteGroupBuilder api)
    {
        api.Map("/nodes/{id:guid}/play", async (HttpContext http, NodeService nodes,
            PlaySessions sessions, Guid id) =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
                return Results.BadRequest("This endpoint speaks WebSocket.");

            var userId = http.User.GetUserId();
            // Throws NotFound when this reader may not see the node, which is the same
            // answer they would get asking for it any other way.
            var node = await nodes.GetWithBodyAsync(userId, id);
            var head = node.File?.Versions.MaxBy(v => v.Number);
            if (head is null)
                return Results.NotFound();

            using var socket = await http.WebSockets.AcceptWebSocketAsync();
            await PlayAsync(sessions, socket, id, userId,
                http.User.Identity?.Name ?? "Someone", head.Hash, http.RequestAborted);
            return Results.Empty;
        });
    }

    private static async Task PlayAsync(PlaySessions sessions, WebSocket socket, Guid nodeId,
        Guid userId, string name, string romHash, CancellationToken ct)
    {
        var buffer = new byte[MaxMessageBytes];
        var opening = await ReadMessageAsync(socket, buffer, ct);
        if (opening is null || buffer[0] != (byte)PlayMessage.Join)
            return;

        var (claimedHash, playerCount) = PlayProtocol.ReadJoin(buffer.AsSpan(0, opening.Value));
        if (!string.Equals(claimedHash, romHash, StringComparison.OrdinalIgnoreCase))
        {
            await Refuse(socket, "That is not the cartridge on this page — reload and try again.", ct);
            return;
        }

        var (result, seat) = sessions.Join(nodeId, socket, userId, name, romHash, playerCount);
        if (seat is null)
        {
            await Refuse(socket, result == PlaySessions.JoinResult.RoomFull
                ? "This game is already full."
                : "Somebody in this game has a different copy of the cartridge.", ct);
            return;
        }

        try
        {
            await PlaySessions.SendAsync(seat,
                PlayProtocol.Welcome(seat.Slot, sessions.Capacity(nodeId)), ct);
            await sessions.BroadcastRosterAsync(nodeId, ct);

            while (true)
            {
                var length = await ReadMessageAsync(socket, buffer, ct);
                if (length is null)
                    break;
                // The server reads exactly one byte of every message — which kind it
                // is — and forwards the rest untouched.
                if (buffer[0] is (byte)PlayMessage.Input or (byte)PlayMessage.Checksum
                    or (byte)PlayMessage.State)
                    await sessions.RelayAsync(nodeId, seat, buffer[..length.Value], ct);
            }
        }
        catch (Exception)
        {
            // A closed tab, a dropped connection, a transport with opinions about which
            // exception says so. However it ends, the seat is cleared below.
        }
        finally
        {
            sessions.Leave(nodeId, seat);
            await sessions.BroadcastRosterAsync(nodeId, CancellationToken.None);
            // Answer a departing player's close, so a client that does wait for one is
            // not left holding the line.
            if (socket.State is WebSocketState.CloseReceived or WebSocketState.Open)
            {
                try
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "",
                        CancellationToken.None);
                }
                catch (Exception)
                {
                    // They have already gone.
                }
            }
        }
    }

    private static async Task Refuse(WebSocket socket, string reason, CancellationToken ct)
    {
        await socket.SendAsync(PlayProtocol.Fault(reason), WebSocketMessageType.Binary, true, ct);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", ct);
    }

    /// <summary>Reads one whole message, however many frames it arrives in. Returns null
    /// when the other end has gone, or has sent more than a game ever needs to.</summary>
    private static async Task<int?> ReadMessageAsync(WebSocket socket, byte[] buffer,
        CancellationToken ct)
    {
        var total = 0;
        while (true)
        {
            var received = await socket.ReceiveAsync(buffer.AsMemory(total), ct);
            if (received.MessageType == WebSocketMessageType.Close)
                return null;
            total += received.Count;
            if (received.EndOfMessage)
                return total == 0 ? null : total;
            if (total >= buffer.Length)
                return null;
        }
    }
}
