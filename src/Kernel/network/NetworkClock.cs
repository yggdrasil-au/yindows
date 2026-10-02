using System;
using System.Net;
using System.Net.Sockets;
using Cosmos.Kernel.System.Timer;

namespace yggdrasilKernel.network;

public static class NetworkClock {
    private const int NtpPort = 123;
    private const int LocalPort = 49152;
    private const int NtpPacketLength = 48;
    private const int ReceiveTimeoutMilliseconds = 5000;
    private const int PollIntervalMilliseconds = 100;
    private const string NtpServerAddress = "168.61.215.74";
    private static readonly long NtpEpochTicks = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

    public static DateTime FetchNetworkTime() {
        using UdpClient client = new UdpClient(LocalPort);
        byte[] request = new byte[NtpPacketLength];
        request[0] = 0x23;
        IPEndPoint server = new IPEndPoint(IPAddress.Parse(NtpServerAddress), NtpPort);
        client.Send(request, request.Length, server);

        IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        int waitedMilliseconds = 0;
        while (client.Available == 0 && waitedMilliseconds < ReceiveTimeoutMilliseconds) {
            TimerManager.Wait(PollIntervalMilliseconds);
            waitedMilliseconds += PollIntervalMilliseconds;
        }

        if (client.Available == 0) {
            throw new TimeoutException("The NTP server did not respond within the timeout.");
        }

        byte[] response = client.Receive(ref remote);
        if (response.Length < NtpPacketLength || (response[0] & 0x07) != 4 || response[1] == 0 || response[1] > 15) {
            throw new InvalidOperationException("The NTP server returned an invalid response.");
        }

        uint seconds = ((uint)response[40] << 24)
            | ((uint)response[41] << 16)
            | ((uint)response[42] << 8)
            | response[43];
        uint fraction = ((uint)response[44] << 24)
            | ((uint)response[45] << 16)
            | ((uint)response[46] << 8)
            | response[47];
        long fractionTicks = (long)(((ulong)fraction * (ulong)TimeSpan.TicksPerSecond) >> 32);
        long timestampTicks = NtpEpochTicks + ((long)seconds * TimeSpan.TicksPerSecond) + fractionTicks;

        return new DateTime(timestampTicks, DateTimeKind.Utc);
    }
}