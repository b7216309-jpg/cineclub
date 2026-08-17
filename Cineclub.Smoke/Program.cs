using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Cineclub.App.Models;
using Cineclub.App.Server;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

const int participantCount = RoomCoordinator.MaximumParticipants;
var temporaryDirectory = Path.Combine(
    Path.GetTempPath(),
    $"cineclub-smoke-{Guid.NewGuid():N}");
var mediaPath = Path.Combine(temporaryDirectory, "smoke.mkv");
Directory.CreateDirectory(temporaryDirectory);

// It need not be a valid movie: the server must expose the original bytes unchanged.
var originalBytes = new byte[8 * 1024];
for (var index = 0; index < originalBytes.Length; index++)
{
    originalBytes[index] = (byte)(index % 251);
}
await File.WriteAllBytesAsync(mediaPath, originalBytes);

var connections = new List<HubConnection>();
var server = new EmbeddedRoomServer(mediaPath, "Cineclub smoke test");

try
{
    await server.StartAsync();
    Ensure(server.LocalBaseUri is not null, "Kestrel did not publish a local address.");
    var localBaseUri = server.LocalBaseUri!;

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    using (var health = await http.GetAsync(new Uri(localBaseUri, "health")))
    {
        Ensure(health.StatusCode == HttpStatusCode.OK, $"Health returned {(int)health.StatusCode}.");
    }

    using (var unauthorizedMedia = await http.GetAsync(new Uri(localBaseUri, "media/invalid-token")))
    {
        Ensure(unauthorizedMedia.StatusCode == HttpStatusCode.NotFound, "An invalid media token was accepted.");
    }

    var mediaUri = server.BuildMediaUri();
    using (var headRequest = new HttpRequestMessage(HttpMethod.Head, mediaUri))
    using (var head = await http.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead))
    {
        Ensure(head.StatusCode == HttpStatusCode.OK, $"HEAD returned {(int)head.StatusCode}.");
        Ensure(head.Content.Headers.ContentLength == originalBytes.LongLength, "HEAD Content-Length is wrong.");
        Ensure(head.Content.Headers.ContentType?.MediaType == "video/x-matroska", "The MKV content type is wrong.");
        Ensure(head.Headers.AcceptRanges.Contains("bytes"), "HEAD does not advertise byte ranges.");
        Ensure(head.Headers.ETag is not null, "HEAD did not include a stable ETag.");
    }

    using (var rangeRequest = new HttpRequestMessage(HttpMethod.Get, mediaUri))
    {
        rangeRequest.Headers.Range = new RangeHeaderValue(0, 1023);
        using var range = await http.SendAsync(rangeRequest, HttpCompletionOption.ResponseHeadersRead);
        var body = await range.Content.ReadAsByteArrayAsync();
        Ensure(range.StatusCode == HttpStatusCode.PartialContent, $"Range returned {(int)range.StatusCode}, not 206.");
        Ensure(range.Content.Headers.ContentRange?.From == 0, "Range starts at the wrong byte.");
        Ensure(range.Content.Headers.ContentRange?.To == 1023, "Range ends at the wrong byte.");
        Ensure(range.Content.Headers.ContentRange?.Length == originalBytes.LongLength, "Range reports the wrong file size.");
        Ensure(body.AsSpan().SequenceEqual(originalBytes.AsSpan(0, 1024)), "Range changed the source bytes.");
    }

    using (var suffixRequest = new HttpRequestMessage(HttpMethod.Get, mediaUri))
    {
        suffixRequest.Headers.Range = new RangeHeaderValue(null, 512);
        using var suffix = await http.SendAsync(suffixRequest, HttpCompletionOption.ResponseHeadersRead);
        var body = await suffix.Content.ReadAsByteArrayAsync();
        Ensure(suffix.StatusCode == HttpStatusCode.PartialContent, "A suffix range did not return 206.");
        Ensure(body.AsSpan().SequenceEqual(originalBytes.AsSpan(originalBytes.Length - 512)), "The suffix range is wrong.");
    }

    using (var invalidRangeRequest = new HttpRequestMessage(HttpMethod.Get, mediaUri))
    {
        invalidRangeRequest.Headers.Range = new RangeHeaderValue(originalBytes.LongLength + 100, null);
        using var invalidRange = await http.SendAsync(invalidRangeRequest, HttpCompletionOption.ResponseHeadersRead);
        Ensure(invalidRange.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable, "An invalid range did not return 416.");
    }

    await using (var missingTunnel = new CloudflareTunnelService(Path.Combine(temporaryDirectory, "missing-cloudflared.exe")))
    {
        await ExpectExceptionAsync<FileNotFoundException>(
            () => missingTunnel.StartAsync(localBaseUri),
            "A missing cloudflared executable was accepted.");
        Ensure(missingTunnel.Status.State == TunnelState.Error, "A missing cloudflared executable did not set Error state.");
        await missingTunnel.StopAsync();
        await missingTunnel.StopAsync();
        Ensure(missingTunnel.Status.State == TunnelState.Stopped, "Tunnel StopAsync is not idempotent.");
    }

    for (var index = 0; index <= participantCount; index++)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(server.BuildHubUri())
            .Build();
        connection.ServerTimeout = TimeSpan.FromSeconds(10);
        connection.KeepAliveInterval = TimeSpan.FromSeconds(3);
        connections.Add(connection);
    }

    var playbackEvents = new ConcurrentQueue<ScheduledPlaybackCommand>();
    var chatReceived = new TaskCompletionSource<ChatEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
    var reactionReceived = new TaskCompletionSource<ReactionEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
    connections[1].On<ChatEntry>("ChatMessage", message => chatReceived.TrySetResult(message));
    connections[1].On<ReactionEvent>("Reaction", reaction => reactionReceived.TrySetResult(reaction));
    connections[1].On<ScheduledPlaybackCommand>("PlaybackCommand", playbackEvents.Enqueue);
    await Task.WhenAll(connections.Select(connection => connection.StartAsync()));

    var overflowConnection = connections[participantCount];
    await ExpectExceptionAsync<HubException>(
        async () => _ = await overflowConnection.InvokeAsync<RoomSnapshot>("GetSnapshot"),
        "An unauthenticated connection received the room snapshot.");
    await ExpectExceptionAsync<HubException>(
        async () => _ = await overflowConnection.InvokeAsync<JoinRoomResult>(
            "Join",
            new JoinRoomRequest("invalid-token", "Intruder")),
        "An invalid room token was accepted.");

    var joins = new JoinRoomResult[participantCount];
    joins[0] = await connections[0].InvokeAsync<JoinRoomResult>(
        "Join",
        new JoinRoomRequest(server.Room.AccessToken, "Host", HostKey: server.Room.HostKey));
    await connections[0].InvokeAsync(
        "UpdatePlayerStatus",
        new PlayerStatusUpdate(true, false, 100, 0, false));

    // A new participant arriving during the ready countdown must cancel it.
    var joinCountdown = await connections[0].InvokeAsync<ScheduledPlaybackCommand>(
        "StartWhenReady",
        new StartWhenReadyRequest(0, 5_000));
    joins[1] = await connections[1].InvokeAsync<JoinRoomResult>(
        "Join",
        new JoinRoomRequest(server.Room.AccessToken, "Guest 1"));
    var afterJoinCancellation = await connections[0].InvokeAsync<RoomSnapshot>("GetSnapshot");
    Ensure(afterJoinCancellation.Playback.Revision > joinCountdown.Revision, "Joining did not supersede the ready countdown.");
    Ensure(afterJoinCancellation.Playback.PendingCommand is null && !afterJoinCancellation.Playback.IsPlaying,
        "Joining left a ready countdown armed.");
    Ensure(await WaitForConditionAsync(() => playbackEvents.Any(command =>
            command.Kind == PlaybackCommandKind.Pause && command.Revision > joinCountdown.Revision), TimeSpan.FromSeconds(2)),
        "Joining canceled the countdown in state but did not broadcast the cancellation.");

    var remainingJoins = await Task.WhenAll(Enumerable.Range(2, participantCount - 2).Select(async index =>
        (Index: index, Result: await connections[index].InvokeAsync<JoinRoomResult>(
            "Join",
            new JoinRoomRequest(server.Room.AccessToken, $"Guest {index}")))));
    foreach (var (index, result) in remainingJoins)
    {
        joins[index] = result;
    }

    var fullSnapshot = await connections[0].InvokeAsync<RoomSnapshot>("GetSnapshot");
    Ensure(joins[0].IsHost, "The host connection was not granted the host role.");
    Ensure(!joins.Skip(1).Any(join => join.IsHost), "A guest was granted the host role.");
    Ensure(server.Room.RoomId.Length == 6, "The room code is not six characters.");
    Ensure(fullSnapshot.Participants.Count == participantCount, "The room does not contain six participants.");
    Ensure(fullSnapshot.Participants.Select(participant => participant.Seat).Distinct().Count() == participantCount,
        "Initial seats are not unique.");

    var duplicateJoin = await connections[1].InvokeAsync<JoinRoomResult>(
        "Join",
        new JoinRoomRequest(server.Room.AccessToken, "Guest 1"));
    Ensure(duplicateJoin.ParticipantId == joins[1].ParticipantId, "A duplicate Join created a new identity.");
    Ensure(duplicateJoin.Snapshot.Participants.Count == participantCount, "A duplicate Join created a ghost participant.");

    await ExpectExceptionAsync<HubException>(
        async () => _ = await overflowConnection.InvokeAsync<JoinRoomResult>(
            "Join",
            new JoinRoomRequest(server.Room.AccessToken, "Guest 6")),
        "A seventh participant entered the room.");
    await ExpectExceptionAsync<HubException>(
        async () => _ = await connections[1].InvokeAsync<ScheduledPlaybackCommand>(
            "SchedulePlayback",
            new SchedulePlaybackRequest(PlaybackCommandKind.Play, 0)),
        "A guest controlled playback.");

    var seats = fullSnapshot.Participants.ToDictionary(participant => participant.ParticipantId, participant => participant.Seat);
    await ExpectExceptionAsync<HubException>(
        () => connections[1].InvokeAsync("SetSeat", seats[joins[0].ParticipantId]),
        "Two participants occupied the same seat.");
    await Task.WhenAll(Enumerable.Range(0, participantCount).Select(async index =>
    {
        await connections[index].InvokeAsync("SetSeat", seats[joins[index].ParticipantId]);
        await connections[index].InvokeAsync(
            "UpdatePlayerStatus",
            new PlayerStatusUpdate(true, false, 100, 0, false));
    }));

    var readySnapshot = await connections[0].InvokeAsync<RoomSnapshot>("GetSnapshot");
    Ensure(readySnapshot.AllConnectedParticipantsReady, "The ready gate did not open for six clients.");

    await connections[0].InvokeAsync("SendChat", new ChatRequest("smoke-chat"));
    var chat = await chatReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Ensure(chat.Text == "smoke-chat", "The chat broadcast was not received unchanged.");
    await connections[0].InvokeAsync("SendReaction", new ReactionRequest("🔥"));
    var reaction = await reactionReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Ensure(reaction.Emoji == "🔥", "The reaction broadcast was not received unchanged.");

    // A real participant disconnecting during a countdown must cancel it.
    var disconnectCountdown = await connections[0].InvokeAsync<ScheduledPlaybackCommand>(
        "StartWhenReady",
        new StartWhenReadyRequest(0, 5_000));
    var reconnectIndex = participantCount - 1;
    var disconnectedParticipantId = joins[reconnectIndex].ParticipantId;
    var disconnected = connections[reconnectIndex];
    await disconnected.StopAsync();
    await disconnected.DisposeAsync();
    var afterDisconnect = await WaitForSnapshotAsync(
        connections[0],
        snapshot => snapshot.Participants.Single(value => value.ParticipantId == disconnectedParticipantId).IsConnected == false,
        TimeSpan.FromSeconds(2));
    Ensure(afterDisconnect.Playback.Revision > disconnectCountdown.Revision, "Disconnecting did not supersede the ready countdown.");
    Ensure(afterDisconnect.Playback.PendingCommand is null && !afterDisconnect.Playback.IsPlaying,
        "Disconnecting left a ready countdown armed.");
    Ensure(await WaitForConditionAsync(() => playbackEvents.Any(command =>
            command.Kind == PlaybackCommandKind.Pause && command.Revision > disconnectCountdown.Revision), TimeSpan.FromSeconds(2)),
        "Disconnecting canceled the countdown in state but did not broadcast the cancellation.");
    await ExpectExceptionAsync<HubException>(
        async () => _ = await overflowConnection.InvokeAsync<JoinRoomResult>(
            "Join",
            new JoinRoomRequest(server.Room.AccessToken, "Guest 6")),
        "An abnormal disconnect did not reserve its seat for resume.");

    var replacement = new HubConnectionBuilder()
        .WithUrl(server.BuildHubUri())
        .Build();
    connections[reconnectIndex] = replacement;
    await replacement.StartAsync();
    var resumed = await replacement.InvokeAsync<JoinRoomResult>(
        "Join",
        new JoinRoomRequest(
            server.Room.AccessToken,
            $"Guest {reconnectIndex}",
            ResumeToken: joins[reconnectIndex].ResumeToken));
    Ensure(resumed.ParticipantId == disconnectedParticipantId, "ResumeToken created a new participant.");
    Ensure(resumed.Snapshot.Participants.Count == participantCount, "Reconnection duplicated or lost a participant.");
    Ensure(resumed.Snapshot.Participants.Single(value => value.ParticipantId == resumed.ParticipantId).IsConnected,
        "The resumed participant is still marked disconnected.");
    await replacement.InvokeAsync(
        "UpdatePlayerStatus",
        new PlayerStatusUpdate(true, false, 100, 0, false));

    var play = await connections[0].InvokeAsync<ScheduledPlaybackCommand>(
        "SchedulePlayback",
        new SchedulePlaybackRequest(PlaybackCommandKind.Play, 1_000, 150));
    await Task.Delay(220);
    var playingSnapshot = await connections[0].InvokeAsync<RoomSnapshot>("GetSnapshot");
    Ensure(play.ResultingIsPlaying && playingSnapshot.Playback.IsPlaying, "Play never became authoritative.");

    // Concurrent host commands are serialized; the highest revision is the sole pending command.
    var racedCommands = await Task.WhenAll(
        connections[0].InvokeAsync<ScheduledPlaybackCommand>(
            "SchedulePlayback",
            new SchedulePlaybackRequest(PlaybackCommandKind.Play, 10_000, 5_000)),
        connections[0].InvokeAsync<ScheduledPlaybackCommand>(
            "SchedulePlayback",
            new SchedulePlaybackRequest(PlaybackCommandKind.Pause, 20_000, 5_000)),
        connections[0].InvokeAsync<ScheduledPlaybackCommand>(
            "SchedulePlayback",
            new SchedulePlaybackRequest(PlaybackCommandKind.Seek, 42_000, 5_000)));
    Ensure(racedCommands.Select(command => command.Revision).Distinct().Count() == racedCommands.Length,
        "Concurrent playback commands reused a revision.");
    var newestCommand = racedCommands.MaxBy(command => command.Revision)!;
    var raceSnapshot = await connections[0].InvokeAsync<RoomSnapshot>("GetSnapshot");
    Ensure(raceSnapshot.Playback.PendingCommand?.Revision == newestCommand.Revision,
        "A stale concurrent command remained authoritative.");

    var finalPause = await connections[0].InvokeAsync<ScheduledPlaybackCommand>(
        "SchedulePlayback",
        new SchedulePlaybackRequest(PlaybackCommandKind.Pause, 42_000, 150));
    await Task.Delay(220);
    var pausedSnapshot = await connections[0].InvokeAsync<RoomSnapshot>("GetSnapshot");
    Ensure(!pausedSnapshot.Playback.IsPlaying && pausedSnapshot.Playback.Revision == finalPause.Revision,
        "Pause never became authoritative.");

    // Explicit Leave releases a reserved seat immediately; abnormal disconnects retain it for resume.
    var leaveIndex = participantCount - 2;
    await connections[leaveIndex].InvokeAsync("Leave");
    var afterLeave = await connections[0].InvokeAsync<RoomSnapshot>("GetSnapshot");
    Ensure(afterLeave.Participants.Count == participantCount - 1, "Explicit Leave kept a participant reservation.");
    var replacementJoin = await overflowConnection.InvokeAsync<JoinRoomResult>(
        "Join",
        new JoinRoomRequest(server.Room.AccessToken, "Replacement guest"));
    Ensure(replacementJoin.Snapshot.Participants.Count == participantCount,
        "A released seat could not be occupied immediately.");

    foreach (var connection in connections)
    {
        await connection.DisposeAsync();
    }
    connections.Clear();

    await server.StopAsync();
    Ensure(!server.IsRunning && server.LocalBaseUri is null, "EmbeddedRoomServer did not stop cleanly.");
    await server.StopAsync();

    Console.WriteLine(
        $"PASS health=200 range=206/416 clients={participantCount} auth=ok countdown=join+disconnect chat=ok playback={joinCountdown.Revision}-{finalPause.Revision} resume=ok leave=ok stop=ok");
}
finally
{
    foreach (var connection in connections)
    {
        try
        {
            await connection.DisposeAsync();
        }
        catch
        {
            // Cleanup must not hide the first smoke-test failure.
        }
    }

    await server.DisposeAsync();
    if (Directory.Exists(temporaryDirectory))
    {
        Directory.Delete(temporaryDirectory, recursive: true);
    }
}

static async Task<RoomSnapshot> WaitForSnapshotAsync(
    HubConnection connection,
    Func<RoomSnapshot, bool> predicate,
    TimeSpan timeout)
{
    var deadline = DateTimeOffset.UtcNow + timeout;
    do
    {
        var snapshot = await connection.InvokeAsync<RoomSnapshot>("GetSnapshot");
        if (predicate(snapshot))
        {
            return snapshot;
        }

        await Task.Delay(25);
    }
    while (DateTimeOffset.UtcNow < deadline);

    throw new TimeoutException("The expected room snapshot was not observed.");
}

static async Task<bool> WaitForConditionAsync(Func<bool> predicate, TimeSpan timeout)
{
    var deadline = DateTimeOffset.UtcNow + timeout;
    do
    {
        if (predicate())
        {
            return true;
        }

        await Task.Delay(25);
    }
    while (DateTimeOffset.UtcNow < deadline);

    return predicate();
}

static async Task ExpectExceptionAsync<TException>(Func<Task> action, string message)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

static void Ensure(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
