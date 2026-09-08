namespace Markwardt.NetChannel.Tests;

public sealed class NetManagerConnectionTests
{
    [Fact]
    public async Task Connect_WithTag_IsRetrievableViaGetConnection()
    {
        (ServiceProvider serverProvider, INetManager server) = TestHarness.CreateManager();
        (ServiceProvider clientProvider, INetManager client) = TestHarness.CreateManager();
        await using ServiceProvider disposeServerProvider = serverProvider;
        await using ServiceProvider disposeClientProvider = clientProvider;
        await using INetManager disposeServer = server;
        await using INetManager disposeClient = client;

        server.Host(0);
        int port = server.HostPort!.Value;

        INetConnection connection = client.Connect("127.0.0.1", port, tag: "my-tag");
        Assert.Same(connection, client.GetConnection("my-tag"));

        await TestHarness.WaitUntil(() => connection.Status == NetStatus.Connected);
    }

    [Fact]
    public async Task Wait_OnPendingConnection_CompletesOnceConnected()
    {
        (ServiceProvider serverProvider, INetManager server) = TestHarness.CreateManager();
        (ServiceProvider clientProvider, INetManager client) = TestHarness.CreateManager();
        await using ServiceProvider disposeServerProvider = serverProvider;
        await using ServiceProvider disposeClientProvider = clientProvider;
        await using INetManager disposeServer = server;
        await using INetManager disposeClient = client;

        server.Host(0);
        int port = server.HostPort!.Value;

        INetConnection connection = client.Connect("127.0.0.1", port);
        await TestHarness.WaitForTask(connection.Wait());

        Assert.Equal(NetStatus.Connected, connection.Status);
    }

    [Fact]
    public async Task Wait_OnPendingConnection_FaultsWhenRejected()
    {
        (ServiceProvider serverProvider, INetManager server) = TestHarness.CreateManager();
        (ServiceProvider clientProvider, INetManager client) = TestHarness.CreateManager();
        await using ServiceProvider disposeServerProvider = serverProvider;
        await using ServiceProvider disposeClientProvider = clientProvider;
        await using INetManager disposeServer = server;
        await using INetManager disposeClient = client;

        server.Host(0);
        int port = server.HostPort!.Value;

        INetConnection connection = client.Connect("127.0.0.1", port, expectedIdentity: new byte[32]);
        await Assert.ThrowsAnyAsync<Exception>(() => connection.Wait());
    }

    [Fact]
    public async Task Wait_OnAlreadyConnectedConnection_CompletesImmediately()
    {
        (ServiceProvider serverProvider, INetManager server) = TestHarness.CreateManager();
        (ServiceProvider clientProvider, INetManager client) = TestHarness.CreateManager();
        await using ServiceProvider disposeServerProvider = serverProvider;
        await using ServiceProvider disposeClientProvider = clientProvider;
        await using INetManager disposeServer = server;
        await using INetManager disposeClient = client;

        server.Host(0);
        int port = server.HostPort!.Value;

        INetConnection connection = client.Connect("127.0.0.1", port);
        await TestHarness.WaitUntil(() => connection.Status == NetStatus.Connected);

        Task waitTask = connection.Wait();
        Assert.True(waitTask.IsCompletedSuccessfully);
        await waitTask;
    }

    [Fact]
    public async Task Connect_ToNothingListening_RetriesHandshake()
    {
        (ServiceProvider provider, INetManager client) = TestHarness.CreateManager();
        await using ServiceProvider disposeProvider = provider;
        await using INetManager disposeClient = client;

        int port = TestHarness.GetFreeUdpPort();
        INetConnection connection = client.Connect("127.0.0.1", port);

        await Task.Delay(700);

        Assert.Equal(NetStatus.Connecting, connection.Status);
    }

    [Fact]
    public async Task Reconnect_AfterGracefulDisconnect_Succeeds()
    {
        (ServiceProvider serverProvider, INetManager server) = TestHarness.CreateManager();
        (ServiceProvider clientProvider, INetManager client) = TestHarness.CreateManager();
        await using ServiceProvider disposeServerProvider = serverProvider;
        await using ServiceProvider disposeClientProvider = clientProvider;
        await using INetManager disposeServer = server;
        await using INetManager disposeClient = client;

        server.Host(0);
        int port = server.HostPort!.Value;

        INetConnection first = client.Connect("127.0.0.1", port);
        await TestHarness.WaitForTask(first.Wait());
        await first.Disconnect();

        INetConnection second = client.Connect("127.0.0.1", port);
        await TestHarness.WaitForTask(second.Wait());

        Assert.Equal(NetStatus.Connected, second.Status);
    }

    [Fact]
    public async Task Connect_PinnedMode_RejectsWhenServerHasNoIdentity()
    {
        (ServiceProvider serverProvider, INetManager server) = TestHarness.CreateManager();
        (ServiceProvider clientProvider, INetManager client) = TestHarness.CreateManager();
        await using ServiceProvider disposeServer = serverProvider;
        await using ServiceProvider disposeClient = clientProvider;
        await using INetManager disposeServerManager = server;
        await using INetManager disposeClientManager = client;

        server.Host(0);
        int port = server.HostPort!.Value;

        List<NetConnectionFailure> rejected = [];
        using IDisposable subscription = client.Rejected.Subscribe(rejected.Add);

        client.Connect("127.0.0.1", port, expectedIdentity: new byte[32]);

        await TestHarness.WaitUntil(() => rejected.Count > 0);
    }

    [Fact]
    public async Task Connect_PinnedMode_SucceedsWithMatchingIdentity()
    {
        using NSec.Cryptography.Key identityKey = NSec.Cryptography.Key.Create(NSec.Cryptography.SignatureAlgorithm.Ed25519, new NSec.Cryptography.KeyCreationParameters { ExportPolicy = NSec.Cryptography.KeyExportPolicies.AllowPlaintextExport });
        byte[] privateKey = identityKey.Export(NSec.Cryptography.KeyBlobFormat.RawPrivateKey);
        byte[] publicKey = identityKey.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey);

        (ServiceProvider serverProvider, INetManager server) = TestHarness.CreateManager();
        (ServiceProvider clientProvider, INetManager client) = TestHarness.CreateManager();
        await using ServiceProvider disposeServer = serverProvider;
        await using ServiceProvider disposeClient = clientProvider;
        await using INetManager disposeServerManager = server;
        await using INetManager disposeClientManager = client;

        server.Host(0, identity: privateKey);
        int port = server.HostPort!.Value;

        INetConnection connection = client.Connect("127.0.0.1", port, expectedIdentity: publicKey);
        await TestHarness.WaitUntil(() => connection.Status == NetStatus.Connected);
    }

    [Fact]
    public async Task Connect_ReceivingHandshakeReject_FiresRejectedAndDisconnects()
    {
        using Socket fakeServer = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        fakeServer.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)fakeServer.LocalEndPoint!).Port;

        (ServiceProvider provider, INetManager client) = TestHarness.CreateManager();
        await using ServiceProvider disposeProvider = provider;
        await using INetManager disposeClient = client;

        List<NetConnectionFailure> rejected = [];
        using IDisposable subscription = client.Rejected.Subscribe(rejected.Add);

        INetConnection connection = client.Connect("127.0.0.1", port);

        byte[] receiveBuffer = new byte[512];
        Task<SocketReceiveFromResult> receiveTask = fakeServer.ReceiveFromAsync(receiveBuffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0));
        SocketReceiveFromResult received = await TestHarness.WaitForTask(receiveTask);

        NetDatagramCodec codec = new(new NetCrypto());
        Packet? initPacket = codec.Decode(receiveBuffer.AsSpan(0, received.ReceivedBytes));
        Assert.Equal(Packet.PayloadOneofCase.HandshakeInit, initPacket?.PayloadCase);

        Packet rejectPacket = new() { HandshakeReject = new HandshakeReject { Reason = "test rejection" } };
        byte[] rejectBuffer = codec.Encode(rejectPacket, null, out int rejectLength);
        await fakeServer.SendToAsync(rejectBuffer.AsMemory(0, rejectLength), received.RemoteEndPoint);

        await TestHarness.WaitUntil(() => rejected.Count > 0);
        Assert.Equal("test rejection", rejected[0].Exception.Message);
        Assert.Equal(NetStatus.Disconnected, connection.Status);
    }

    [Fact]
    public async Task Connect_ReceivingHandshakeReject_AlsoReportsTheStatusChange()
    {
        using Socket fakeServer = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        fakeServer.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)fakeServer.LocalEndPoint!).Port;

        (ServiceProvider provider, INetManager client) = TestHarness.CreateManager();
        await using ServiceProvider disposeProvider = provider;
        await using INetManager disposeClient = client;

        List<NetStatusChange> changes = [];
        using IDisposable subscription = client.StatusChanged.Subscribe(changes.Add);

        INetConnection connection = client.Connect("127.0.0.1", port);

        byte[] receiveBuffer = new byte[512];
        SocketReceiveFromResult received = await TestHarness.WaitForTask(
            fakeServer.ReceiveFromAsync(receiveBuffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0)));

        NetDatagramCodec codec = new(new NetCrypto());
        byte[] rejectBuffer = codec.Encode(new Packet { HandshakeReject = new HandshakeReject { Reason = "denied" } }, null, out int rejectLength);
        await fakeServer.SendToAsync(rejectBuffer.AsMemory(0, rejectLength), received.RemoteEndPoint);

        await TestHarness.WaitUntil(() => changes.Count > 0);

        Assert.Equal(NetStatus.Disconnected, Assert.Single(changes).Status);
        Assert.Same(connection, changes[0].Connection);
    }

    [Fact]
    public async Task Connect_ReceivingHandshakeReject_FaultsWaitWithTheGivenReason()
    {
        using Socket fakeServer = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        fakeServer.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)fakeServer.LocalEndPoint!).Port;

        (ServiceProvider provider, INetManager client) = TestHarness.CreateManager();
        await using ServiceProvider disposeProvider = provider;
        await using INetManager disposeClient = client;

        INetConnection connection = client.Connect("127.0.0.1", port);
        Task wait = connection.Wait();

        byte[] receiveBuffer = new byte[512];
        SocketReceiveFromResult received = await TestHarness.WaitForTask(
            fakeServer.ReceiveFromAsync(receiveBuffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0)));

        NetDatagramCodec codec = new(new NetCrypto());
        byte[] rejectBuffer = codec.Encode(new Packet { HandshakeReject = new HandshakeReject { Reason = "denied" } }, null, out int rejectLength);
        await fakeServer.SendToAsync(rejectBuffer.AsMemory(0, rejectLength), received.RemoteEndPoint);

        NetFailedException exception = await Assert.ThrowsAsync<NetFailedException>(() => TestHarness.WaitForTask(wait));
        Assert.Equal("denied", exception.Message);
    }

    [Fact]
    public async Task Wait_OnPendingConnectionDroppedLocally_Faults()
    {
        (ServiceProvider provider, INetManager client) = TestHarness.CreateManager();
        await using ServiceProvider disposeProvider = provider;
        await using INetManager disposeClient = client;

        INetConnection connection = client.Connect("127.0.0.1", 1);
        Task wait = connection.Wait();

        connection.Drop();

        await Assert.ThrowsAsync<NetFailedException>(() => TestHarness.WaitForTask(wait, 2000));
    }

    [Fact]
    public async Task Wait_OnPendingConnectionEndedByDisposal_Faults()
    {
        (ServiceProvider provider, INetManager client) = TestHarness.CreateManager();
        await using ServiceProvider disposeProvider = provider;

        INetConnection connection = client.Connect("127.0.0.1", 1);
        Task wait = connection.Wait();

        await client.DisposeAsync();

        await Assert.ThrowsAsync<NetFailedException>(() => TestHarness.WaitForTask(wait, 2000));
    }

    [Fact]
    public async Task HandshakeResponse_Retransmitted_ReusesTheSameConnection()
    {
        (ServiceProvider provider, INetManager server) = TestHarness.CreateManager();
        await using ServiceProvider disposeProvider = provider;
        await using INetManager disposeServer = server;

        server.Host(0);
        IPEndPoint serverEndPoint = new(IPAddress.Loopback, server.HostPort!.Value);

        using Socket client = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        client.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        NetCrypto crypto = new();
        NetDatagramCodec codec = new(crypto);
        using NetAgreementKey clientKey = crypto.CreateAgreementKey();

        Packet init = new()
        {
            HandshakeInit = new HandshakeInit
            {
                ProtocolVersion = INetManager.ProtocolVersion,
                ClientPublicKey = ByteString.CopyFrom(clientKey.PublicKey.Span),
            },
        };

        byte[] initBuffer = codec.Encode(init, null, out int initLength);
        await client.SendToAsync(initBuffer.AsMemory(0, initLength), serverEndPoint);

        byte[] receiveBuffer = new byte[1024];
        SocketReceiveFromResult received = await TestHarness.WaitForTask(
            client.ReceiveFromAsync(receiveBuffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0)));
        HandshakeChallenge challenge = codec.Decode(receiveBuffer.AsSpan(0, received.ReceivedBytes))!.HandshakeChallenge;

        Packet response = new()
        {
            HandshakeResponse = new HandshakeResponse
            {
                ClientPublicKey = challenge.ClientPublicKey,
                ServerPublicKey = challenge.ServerPublicKey,
                Cookie = challenge.Cookie,
            },
        };

        byte[] responseBuffer = codec.Encode(response, null, out int responseLength);
        await client.SendToAsync(responseBuffer.AsMemory(0, responseLength), serverEndPoint);
        await TestHarness.WaitUntil(() => server.ActiveConnections.Count == 1);
        INetConnection accepted = server.ActiveConnections.Single();

        await client.SendToAsync(responseBuffer.AsMemory(0, responseLength), serverEndPoint);
        await Task.Delay(300);

        Assert.Same(accepted, Assert.Single(server.ActiveConnections));
    }

    [Fact]
    public async Task PendingConnections_ListsAConnectingConnectionUntilItConnects()
    {
        (ServiceProvider serverProvider, INetManager server) = TestHarness.CreateManager();
        (ServiceProvider clientProvider, INetManager client) = TestHarness.CreateManager();
        await using ServiceProvider disposeServerProvider = serverProvider;
        await using ServiceProvider disposeClientProvider = clientProvider;
        await using INetManager disposeServer = server;
        await using INetManager disposeClient = client;

        server.Host(0);
        INetConnection connection = client.Connect("127.0.0.1", server.HostPort!.Value);

        // No assertion on it being pending here — over loopback the handshake can already have completed by
        // the time this line runs. PendingConnections_DropsAConnectionThatNeverConnects covers that state
        // against an endpoint that never answers, where it can't race.
        await TestHarness.WaitForTask(connection.Wait());

        Assert.DoesNotContain(connection, client.PendingConnections);
        Assert.Contains(connection, client.ActiveConnections);
    }

    [Fact]
    public async Task PendingConnections_DropsAConnectionThatNeverConnects()
    {
        (ServiceProvider provider, INetManager client) = TestHarness.CreateManager();
        await using ServiceProvider disposeProvider = provider;
        await using INetManager disposeClient = client;

        INetConnection connection = client.Connect("127.0.0.1", 1);
        Assert.Contains(connection, client.PendingConnections);

        connection.Drop();

        Assert.Empty(client.PendingConnections);
    }
}
