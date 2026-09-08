namespace Markwardt.NetChannel.Tests;

public sealed class NetHandshakeRejectionTests
{
    [Fact]
    public async Task HandshakeInit_WithUnsupportedProtocolVersion_IsRejected()
    {
        await using ServerFixture fixture = await ServerFixture.Create();

        Packet init = new()
        {
            HandshakeInit = new HandshakeInit
            {
                ProtocolVersion = "not-a-supported-version",
                ClientPublicKey = ByteString.CopyFrom(fixture.ClientKey.PublicKey.Span),
            },
        };

        Packet answer = await fixture.Exchange(init);

        Assert.Equal(Packet.PayloadOneofCase.HandshakeReject, answer.PayloadCase);
        Assert.Equal("Unsupported protocol version.", answer.HandshakeReject.Reason);
        Assert.Empty(fixture.Server.ActiveConnections);
    }

    [Fact]
    public async Task HandshakeResponse_WithATamperedCookie_IsRejected()
    {
        await using ServerFixture fixture = await ServerFixture.Create();
        HandshakeChallenge challenge = await fixture.Handshake();

        byte[] tampered = challenge.Cookie.ToByteArray();
        tampered[0] ^= 0xFF;

        Packet response = new()
        {
            HandshakeResponse = new HandshakeResponse
            {
                ClientPublicKey = challenge.ClientPublicKey,
                ServerPublicKey = challenge.ServerPublicKey,
                Cookie = ByteString.CopyFrom(tampered),
            },
        };

        Packet answer = await fixture.Exchange(response);

        Assert.Equal(Packet.PayloadOneofCase.HandshakeReject, answer.PayloadCase);
        Assert.Equal("Invalid or expired cookie.", answer.HandshakeReject.Reason);
        Assert.Empty(fixture.Server.ActiveConnections);
    }

    [Fact]
    public async Task HandshakeResponse_WithACookieOfTheWrongLength_IsIgnored()
    {
        await using ServerFixture fixture = await ServerFixture.Create();
        HandshakeChallenge challenge = await fixture.Handshake();

        Packet response = new()
        {
            HandshakeResponse = new HandshakeResponse
            {
                ClientPublicKey = challenge.ClientPublicKey,
                ServerPublicKey = challenge.ServerPublicKey,
                Cookie = ByteString.CopyFrom([1, 2, 3]),
            },
        };

        await fixture.Send(response);
        await Task.Delay(300);

        Assert.Empty(fixture.Server.ActiveConnections);
    }

    [Fact]
    public async Task HandshakeResponse_ReplayedFromADifferentEndpoint_IsRejected()
    {
        await using ServerFixture fixture = await ServerFixture.Create();
        HandshakeChallenge challenge = await fixture.Handshake();

        Packet response = new()
        {
            HandshakeResponse = new HandshakeResponse
            {
                ClientPublicKey = challenge.ClientPublicKey,
                ServerPublicKey = challenge.ServerPublicKey,
                Cookie = challenge.Cookie,
            },
        };

        // The cookie binds the client's address and port, so the same one replayed from elsewhere must not
        // pass return-routability — otherwise it would be a usable amplification handle.
        using Socket elsewhere = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        elsewhere.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        byte[] buffer = fixture.Codec.Encode(response, null, out int length);
        await elsewhere.SendToAsync(buffer.AsMemory(0, length), fixture.ServerEndPoint);

        byte[] receiveBuffer = new byte[1024];
        SocketReceiveFromResult received = await TestHarness.WaitForTask(
            elsewhere.ReceiveFromAsync(receiveBuffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0)));
        Packet answer = fixture.Codec.Decode(receiveBuffer.AsSpan(0, received.ReceivedBytes))!;

        Assert.Equal(Packet.PayloadOneofCase.HandshakeReject, answer.PayloadCase);
        Assert.Empty(fixture.Server.ActiveConnections);
    }

    [Fact]
    public async Task HandshakeInit_ToAManagerThatIsNotHosting_IsIgnored()
    {
        (ServiceProvider provider, INetManager manager) = TestHarness.CreateManager();
        await using ServiceProvider disposeProvider = provider;
        await using INetManager disposeManager = manager;

        INetConnection outgoing = manager.Connect("127.0.0.1", TestHarness.GetFreeUdpPort());
        int clientPort = ((NetConnection)outgoing).RemoteEndPoint!.Port;

        await Task.Delay(200);

        Assert.Empty(manager.ActiveConnections);
        Assert.NotEqual(0, clientPort);
    }

    [Fact]
    public async Task HandshakeChallenge_WithAnUnusableServerKey_FailsTheAttemptInsteadOfRetryingForever()
    {
        using Socket fakeServer = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        fakeServer.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)fakeServer.LocalEndPoint!).Port;

        (ServiceProvider provider, INetManager client) = TestHarness.CreateManager();
        await using ServiceProvider disposeProvider = provider;
        await using INetManager disposeClient = client;

        INetConnection connection = client.Connect("127.0.0.1", port);
        Task wait = connection.Wait();

        NetDatagramCodec codec = new(new NetCrypto());
        byte[] receiveBuffer = new byte[1024];
        SocketReceiveFromResult received = await TestHarness.WaitForTask(
            fakeServer.ReceiveFromAsync(receiveBuffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0)));
        Packet init = codec.Decode(receiveBuffer.AsSpan(0, received.ReceivedBytes))!;

        Packet challenge = new()
        {
            HandshakeChallenge = new HandshakeChallenge
            {
                ClientPublicKey = init.HandshakeInit.ClientPublicKey,
                ServerPublicKey = ByteString.CopyFrom(new byte[5]),
                Cookie = ByteString.CopyFrom(new byte[20]),
            },
        };

        byte[] buffer = codec.Encode(challenge, null, out int length);
        await fakeServer.SendToAsync(buffer.AsMemory(0, length), received.RemoteEndPoint);

        await Assert.ThrowsAsync<NetFailedException>(() => TestHarness.WaitForTask(wait, 3000));
        Assert.Equal(NetStatus.Disconnected, connection.Status);
    }

    private sealed class ServerFixture : IAsyncDisposable
    {
        private readonly ServiceProvider provider;

        private readonly Socket client;

        private ServerFixture(ServiceProvider provider, INetManager server, Socket client)
        {
            this.provider = provider;
            Server = server;
            this.client = client;
            ServerEndPoint = new IPEndPoint(IPAddress.Loopback, server.HostPort!.Value);
        }

        public INetManager Server { get; }

        public IPEndPoint ServerEndPoint { get; }

        public NetDatagramCodec Codec { get; } = new(new NetCrypto());

        public NetAgreementKey ClientKey { get; } = new NetCrypto().CreateAgreementKey();

        public static Task<ServerFixture> Create()
        {
            (ServiceProvider provider, INetManager server) = TestHarness.CreateManager();
            server.Host(0);

            Socket client = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return Task.FromResult(new ServerFixture(provider, server, client));
        }

        public async Task Send(Packet packet)
        {
            byte[] buffer = Codec.Encode(packet, null, out int length);
            await client.SendToAsync(buffer.AsMemory(0, length), ServerEndPoint);
        }

        public async Task<Packet> Exchange(Packet packet)
        {
            await Send(packet);

            byte[] receiveBuffer = new byte[1024];
            SocketReceiveFromResult received = await TestHarness.WaitForTask(
                client.ReceiveFromAsync(receiveBuffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0)));
            return Codec.Decode(receiveBuffer.AsSpan(0, received.ReceivedBytes))!;
        }

        public async Task<HandshakeChallenge> Handshake()
        {
            Packet init = new()
            {
                HandshakeInit = new HandshakeInit
                {
                    ProtocolVersion = INetManager.ProtocolVersion,
                    ClientPublicKey = ByteString.CopyFrom(ClientKey.PublicKey.Span),
                },
            };

            return (await Exchange(init)).HandshakeChallenge;
        }

        public async ValueTask DisposeAsync()
        {
            ClientKey.Dispose();
            client.Dispose();
            await Server.DisposeAsync();
            await provider.DisposeAsync();
        }
    }
}
