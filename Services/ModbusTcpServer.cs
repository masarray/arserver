using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Ari61850Bridge.Services;

/// <summary>
/// Lightweight read-only Modbus TCP slave/server engine for FUXA or any Modbus TCP master.
/// Supports FC01/FC02/FC03/FC04. Write functions are intentionally rejected for read-only IEC61850 monitoring safety.
/// </summary>
public sealed class ModbusTcpServer : IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly ushort[] _holdingRegisters = new ushort[10000];
    private readonly ushort[] _inputRegisters = new ushort[10000];
    private readonly bool[] _coils = new bool[10000];
    private readonly bool[] _discreteInputs = new bool[10000];
    private readonly List<TcpClient> _clients = new();
    private CancellationTokenSource? _cts;
    private TcpListener? _listener;

    public event Action<string, string>? Log;
    public bool IsRunning { get; private set; }
    public int ClientCount { get { lock (_lock) return _clients.Count(c => c.Connected); } }
    public long ReadRequestCount => Interlocked.Read(ref _readRequestCount);
    public int Port { get; private set; }
    public string BindAddress { get; private set; } = "0.0.0.0";
    public byte UnitId { get; private set; } = 1;
    public string LastClientEndpoint { get; private set; } = "-";

    private long _readRequestCount;

    public Task StartAsync(string bindAddress, int port, byte unitId, CancellationToken cancellationToken)
    {
        if (IsRunning) return Task.CompletedTask;

        var ipAddress = ParseBindAddress(bindAddress);
        BindAddress = bindAddress;
        Port = port;
        UnitId = unitId == 0 ? (byte)1 : unitId;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(ipAddress, port);
        _listener.Start();
        IsRunning = true;

        Log?.Invoke("INFO", $"Modbus TCP slave/server started on {ipAddress}:{port}, Unit ID {UnitId}.");
        _ = AcceptLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }

        List<TcpClient> clients;
        lock (_lock)
        {
            clients = _clients.ToList();
            _clients.Clear();
        }

        foreach (var client in clients)
        {
            try { client.Close(); } catch { }
            await Task.Yield();
        }

        IsRunning = false;
        LastClientEndpoint = "-";
        Log?.Invoke("INFO", "Modbus TCP slave/server stopped.");
    }

    public void WriteHoldingRegister(int displayAddress, ushort value)
    {
        var index = NormalizeHolding(displayAddress);
        if ((uint)index >= _holdingRegisters.Length) return;
        lock (_lock) _holdingRegisters[index] = value;
    }

    public void WriteInputRegister(int displayAddress, ushort value)
    {
        var index = NormalizeInputRegister(displayAddress);
        if ((uint)index >= _inputRegisters.Length) return;
        lock (_lock) _inputRegisters[index] = value;
    }

    public void WriteCoil(int displayAddress, bool value)
    {
        var index = NormalizeCoil(displayAddress);
        if ((uint)index >= _coils.Length) return;
        lock (_lock) _coils[index] = value;
    }

    public void WriteDiscreteInput(int displayAddress, bool value)
    {
        var index = NormalizeDiscreteInput(displayAddress);
        if ((uint)index >= _discreteInputs.Length) return;
        lock (_lock) _discreteInputs[index] = value;
    }

    public void WriteFloat32ToHolding(int displayAddress, float value, string wordOrder = "ABCD")
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes); // ABCD network byte order

        ushort high = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(0, 2));
        ushort low = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(2, 2));

        if (wordOrder.Equals("CDAB", StringComparison.OrdinalIgnoreCase) || wordOrder.Contains("Swap", StringComparison.OrdinalIgnoreCase))
            (high, low) = (low, high);

        WriteHoldingRegister(displayAddress, high);
        WriteHoldingRegister(displayAddress + 1, low);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(cancellationToken);
                client.NoDelay = true;
                LastClientEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
                lock (_lock) _clients.Add(client);
                Log?.Invoke("INFO", $"Modbus master connected: {LastClientEndpoint}");
                _ = HandleClientAsync(client, cancellationToken);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                Log?.Invoke("ERROR", $"Modbus accept error: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = client.GetStream();
            var header = new byte[7];
            while (!cancellationToken.IsCancellationRequested && client.Connected)
            {
                if (!await ReadExactAsync(stream, header, cancellationToken)) break;

                var transactionId = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2));
                var protocolId = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2));
                var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
                var requestUnitId = header[6];

                if (protocolId != 0 || length == 0 || length > 260)
                {
                    Log?.Invoke("WARN", "Invalid Modbus TCP header received.");
                    break;
                }

                var pdu = new byte[length - 1];
                if (!await ReadExactAsync(stream, pdu, cancellationToken)) break;

                byte[] responsePdu;
                if (requestUnitId != UnitId)
                {
                    responsePdu = pdu.Length > 0 ? ExceptionResponse(pdu[0], 0x0B) : ExceptionResponse(0, 0x0B);
                    Log?.Invoke("WARN", $"Ignored Modbus request for Unit ID {requestUnitId}; configured Unit ID is {UnitId}.");
                }
                else
                {
                    responsePdu = BuildResponse(pdu);
                }

                var response = new byte[7 + responsePdu.Length];
                BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(0, 2), transactionId);
                BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2, 2), 0);
                BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(4, 2), (ushort)(responsePdu.Length + 1));
                response[6] = requestUnitId;
                responsePdu.CopyTo(response.AsSpan(7));
                await stream.WriteAsync(response, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log?.Invoke("WARN", $"Modbus master disconnected/error: {ex.Message}");
        }
        finally
        {
            lock (_lock) _clients.Remove(client);
            try { client.Close(); } catch { }
            LastClientEndpoint = ClientCount > 0 ? LastClientEndpoint : "-";
        }
    }

    private byte[] BuildResponse(byte[] pdu)
    {
        if (pdu.Length < 5) return ExceptionResponse(0, 3);
        var function = pdu[0];
        var rawStart = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1, 2));
        var quantity = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3, 2));
        var start = NormalizeRequestStart(function, rawStart);

        if (quantity < 1 || start < 0) return ExceptionResponse(function, 3);

        return function switch
        {
            1 => quantity > 2000 ? ExceptionResponse(function, 3) : ReadBits(function, start, quantity, _coils),
            2 => quantity > 2000 ? ExceptionResponse(function, 3) : ReadBits(function, start, quantity, _discreteInputs),
            3 => quantity > 125 ? ExceptionResponse(function, 3) : ReadRegisters(function, start, quantity, _holdingRegisters),
            4 => quantity > 125 ? ExceptionResponse(function, 3) : ReadRegisters(function, start, quantity, _inputRegisters),
            5 or 6 or 15 or 16 => ExceptionResponse(function, 1), // read-only safety
            _ => ExceptionResponse(function, 1)
        };
    }

    private byte[] ReadRegisters(byte function, int start, ushort quantity, ushort[] source)
    {
        if (start + quantity > source.Length) return ExceptionResponse(function, 2);
        var response = new byte[2 + quantity * 2];
        response[0] = function;
        response[1] = (byte)(quantity * 2);
        lock (_lock)
        {
            for (var i = 0; i < quantity; i++)
                BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2 + i * 2, 2), source[start + i]);
        }
        Interlocked.Increment(ref _readRequestCount);
        return response;
    }

    private byte[] ReadBits(byte function, int start, ushort quantity, bool[] source)
    {
        if (start + quantity > source.Length) return ExceptionResponse(function, 2);
        var byteCount = (quantity + 7) / 8;
        var response = new byte[2 + byteCount];
        response[0] = function;
        response[1] = (byte)byteCount;
        lock (_lock)
        {
            for (var i = 0; i < quantity; i++)
            {
                if (source[start + i]) response[2 + i / 8] |= (byte)(1 << (i % 8));
            }
        }
        Interlocked.Increment(ref _readRequestCount);
        return response;
    }

    private static int NormalizeRequestStart(byte function, ushort rawStart)
    {
        // Accept both normal Modbus zero-based addressing and human display addressing.
        // Example: holding register 40001 can be requested as PDU offset 0 or raw address 40001.
        return function switch
        {
            1 => rawStart,
            2 => rawStart >= 10001 ? rawStart - 10001 : rawStart,
            3 => rawStart >= 40001 ? rawStart - 40001 : rawStart,
            4 => rawStart >= 30001 ? rawStart - 30001 : rawStart,
            _ => rawStart
        };
    }

    private static IPAddress ParseBindAddress(string bindAddress)
    {
        if (string.IsNullOrWhiteSpace(bindAddress) || bindAddress.Equals("Any", StringComparison.OrdinalIgnoreCase) || bindAddress == "0.0.0.0")
            return IPAddress.Any;
        return IPAddress.TryParse(bindAddress, out var ip) ? ip : IPAddress.Any;
    }

    private static byte[] ExceptionResponse(byte function, byte code) => new[] { (byte)(function | 0x80), code };

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    private static int NormalizeHolding(int displayAddress) => displayAddress >= 40001 ? displayAddress - 40001 : displayAddress;
    private static int NormalizeInputRegister(int displayAddress) => displayAddress >= 30001 ? displayAddress - 30001 : displayAddress;
    private static int NormalizeCoil(int displayAddress) => displayAddress >= 1 ? displayAddress - 1 : displayAddress;
    private static int NormalizeDiscreteInput(int displayAddress) => displayAddress >= 10001 ? displayAddress - 10001 : displayAddress;

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts?.Dispose();
    }
}
