using Cineclub.App.Models;
using Microsoft.AspNetCore.SignalR;

namespace Cineclub.App.Server;

public sealed class RoomHub(RoomCoordinator room) : Hub<IRoomClient>
{
    public async Task<JoinRoomResult> Join(JoinRoomRequest request)
    {
        await room.EventGate.WaitAsync();
        try
        {
            var result = room.Join(Context.ConnectionId, request);
            await Groups.AddToGroupAsync(Context.ConnectionId, RoomCoordinator.SignalRGroupName);
            var cancellation = room.CancelReadyCountdownIfNeeded(force: true);
            var snapshot = room.GetSnapshotForConnection(Context.ConnectionId);
            await BroadcastCancellationAsync(cancellation);
            await Clients.Group(RoomCoordinator.SignalRGroupName).RoomSnapshot(snapshot);
            return result with { Snapshot = snapshot };
        }
        finally
        {
            room.EventGate.Release();
        }
    }

    public Task<RoomSnapshot> GetSnapshot()
    {
        return Task.FromResult(room.GetSnapshotForConnection(Context.ConnectionId));
    }

    public async Task SetSeat(int seat)
    {
        await room.EventGate.WaitAsync();
        try
        {
            var snapshot = room.SetSeat(Context.ConnectionId, seat);
            await Clients.Group(RoomCoordinator.SignalRGroupName).RoomSnapshot(snapshot);
        }
        finally
        {
            room.EventGate.Release();
        }
    }

    public async Task UpdatePlayerStatus(PlayerStatusUpdate update)
    {
        await room.EventGate.WaitAsync();
        try
        {
            room.UpdatePlayerStatus(Context.ConnectionId, update);
            var cancellation = room.CancelReadyCountdownIfNeeded();
            var snapshot = room.GetSnapshotForConnection(Context.ConnectionId);
            await BroadcastCancellationAsync(cancellation);
            await Clients.Group(RoomCoordinator.SignalRGroupName).RoomSnapshot(snapshot);
        }
        finally
        {
            room.EventGate.Release();
        }
    }

    public async Task SendChat(ChatRequest request)
    {
        await room.EventGate.WaitAsync();
        try
        {
            var message = room.AddChatMessage(Context.ConnectionId, request);
            await Clients.Group(RoomCoordinator.SignalRGroupName).ChatMessage(message);
        }
        finally
        {
            room.EventGate.Release();
        }
    }

    public async Task SendReaction(ReactionRequest request)
    {
        await room.EventGate.WaitAsync();
        try
        {
            var reaction = room.CreateReaction(Context.ConnectionId, request);
            await Clients.Group(RoomCoordinator.SignalRGroupName).Reaction(reaction);
        }
        finally
        {
            room.EventGate.Release();
        }
    }

    public async Task Leave()
    {
        await room.EventGate.WaitAsync();
        try
        {
            room.Leave(Context.ConnectionId, out var participantRemoved);
            if (!participantRemoved)
            {
                return;
            }

            var cancellation = room.CancelReadyCountdownIfNeeded(force: true);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, RoomCoordinator.SignalRGroupName);
            await BroadcastCancellationAsync(cancellation);
            await Clients.Group(RoomCoordinator.SignalRGroupName).RoomSnapshot(room.GetSnapshot());
        }
        finally
        {
            room.EventGate.Release();
        }
    }

    public async Task<ScheduledPlaybackCommand> SchedulePlayback(SchedulePlaybackRequest request)
    {
        await room.EventGate.WaitAsync();
        try
        {
            var command = room.SchedulePlayback(Context.ConnectionId, request);
            await Clients.Group(RoomCoordinator.SignalRGroupName).PlaybackCommand(command);
            await Clients.Group(RoomCoordinator.SignalRGroupName).RoomSnapshot(room.GetSnapshot());
            return command;
        }
        finally
        {
            room.EventGate.Release();
        }
    }

    public async Task<ScheduledPlaybackCommand> StartWhenReady(StartWhenReadyRequest request)
    {
        await room.EventGate.WaitAsync();
        try
        {
            var command = room.StartWhenReady(Context.ConnectionId, request);
            await Clients.Group(RoomCoordinator.SignalRGroupName).PlaybackCommand(command);
            await Clients.Group(RoomCoordinator.SignalRGroupName).RoomSnapshot(room.GetSnapshot());
            return command;
        }
        finally
        {
            room.EventGate.Release();
        }
    }

    /// <summary>One round trip used by clients to estimate their clock offset.</summary>
    public DateTimeOffset Ping(DateTimeOffset clientSentAtUtc) => DateTimeOffset.UtcNow;

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await room.EventGate.WaitAsync();
        try
        {
            room.MarkDisconnected(Context.ConnectionId, out var participantDisconnected);
            var cancellation = room.CancelReadyCountdownIfNeeded(force: participantDisconnected);
            await BroadcastCancellationAsync(cancellation);
            await Clients.Group(RoomCoordinator.SignalRGroupName).RoomSnapshot(room.GetSnapshot());
        }
        finally
        {
            room.EventGate.Release();
        }

        await base.OnDisconnectedAsync(exception);
    }

    private Task BroadcastCancellationAsync(ScheduledPlaybackCommand? cancellation) =>
        cancellation is null
            ? Task.CompletedTask
            : Clients.Group(RoomCoordinator.SignalRGroupName).PlaybackCommand(cancellation);
}
