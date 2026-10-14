using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace SpectrumAnalyzer.Services;

public class UdpAudioReceiver
{
    // UDP packet format shared with the ESP32 firmware:
    // [sample lag: int16] [256 left samples: int16] [256 right samples: int16]
    private const int Port = 5555;
    private const int SamplesPerChannel = 256;
    private const int BytesPerSample = sizeof(short);
    private const int SampleLagBytes = sizeof(short);
    private const int ChannelBytes = SamplesPerChannel * BytesPerSample;
    private const int PacketSize = SampleLagBytes + (2 * ChannelBytes);

    private UdpClient? _udpClient;
    private CancellationTokenSource? _cancellationTokenSource;

    public event Action<short, short[], short[]>? OnDataReceived;

    public void StartListening()
    {
        if (_udpClient != null)
        {
            return;
        }

        try
        {
            _udpClient = new UdpClient();

            _udpClient.Client.SetSocketOption(
                SocketOptionLevel.Socket,
                SocketOptionName.ReuseAddress,
                true);

            _udpClient.Client.Bind(
                new IPEndPoint(IPAddress.Any, Port));

            _cancellationTokenSource = new CancellationTokenSource();

            Task.Run(
                () => ListenLoop(_cancellationTokenSource.Token),
                _cancellationTokenSource.Token);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[UDP] Failed to start receiver: {ex.Message}");

            _udpClient?.Dispose();
            _udpClient = null;

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }

    public void StopListening()
    {
        if (_udpClient == null)
        {
            return;
        }

        _cancellationTokenSource?.Cancel();

        try
        {
            _udpClient.Close();
            _udpClient.Dispose();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[UDP] Failed to stop receiver cleanly: {ex.Message}");
        }
        finally
        {
            _udpClient = null;

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }

    private void ListenLoop(CancellationToken token)
    {
        IPEndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, Port);

        try
        {
            while (!token.IsCancellationRequested)
            {
                byte[] receivedBytes = _udpClient!.Receive(ref remoteEndPoint);

                if (receivedBytes.Length == PacketSize)
                {
                    ParsePacket(receivedBytes);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[UDP] Packet discarded. Expected {PacketSize} bytes, received {receivedBytes.Length}.");
                }
            }
        }
        catch (SocketException) when (token.IsCancellationRequested)
        {
            // Expected when the UDP socket is closed during shutdown.
        }
        catch (ObjectDisposedException) when (token.IsCancellationRequested)
        {
            // Expected when the receiver is disposed during shutdown.
        }
        catch (SocketException ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[UDP] Socket error: {ex.Message}");
        }
    }

    // Decode one ESP32 audio packet into the stereo channels and the
    // sample lag calculated by the firmware for direction estimation.
    private void ParsePacket(byte[] bytes)
    {
        short sampleLag = BitConverter.ToInt16(bytes, 0);

        short[] leftChannel = new short[SamplesPerChannel];
        short[] rightChannel = new short[SamplesPerChannel];

        Buffer.BlockCopy(
            bytes,
            SampleLagBytes,
            leftChannel,
            0,
            ChannelBytes);

        Buffer.BlockCopy(
            bytes,
            SampleLagBytes + ChannelBytes,
            rightChannel,
            0,
            ChannelBytes);

        OnDataReceived?.Invoke(sampleLag, leftChannel, rightChannel);
    }
}