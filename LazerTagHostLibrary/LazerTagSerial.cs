using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LazerTagHostLibrary
{
    /// <summary>
    /// Handles communication with the IR relay over either USB serial or WiFi TCP.
    /// Pass a COM port name ("COM3") for serial, or "ip:port" ("192.168.1.42:8266") for WiFi.
    /// </summary>
    public class LazerTagSerial : IDisposable
    {
        private const int InterSignatureDelayMilliseconds = 50;
        private const int TcpConnectTimeoutMs = 3000;
        private const int TcpDefaultPort = 8266;

        private const int TcpConnectRetryCount = 5;
        private const int TcpConnectRetryDelayMs = 1000;
        private const int TcpReadTimeoutMs = 500;
        private const int TcpWriteTimeoutMs = 2000;
        private const int WorkerJoinTimeoutMs = 500;
        private const int HandshakeTimeoutMs = 2000;
        private const int MaxRelayLineLength = 1024;
        private const string ProtocolHello = "HELLO 1\r\n";
        private const string ProtocolReady = "READY 1";
        private const string ProtocolPong = "PONG";
        private static readonly byte[] Ping = Encoding.ASCII.GetBytes("PING\r\n");
        private static readonly TimeSpan PingAfterIdle = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan PingResponseTimeout = TimeSpan.FromSeconds(5);

        private readonly object _stateLock = new object();
        private readonly object _connectionLock = new object();
        private readonly Action<HostDiagnostic> _reportDiagnostic;
        private RelayOutbox _outbox;

        private string _portName;
        private bool _isTcp;
        private string _readBuffer = string.Empty;

        private SerialPort _serialPort;
        private TcpClient _tcpClient;
        private NetworkStream _tcpStream;

        private Thread _writeThread;
        private Thread _tcpReadThread;
        private CancellationTokenSource _workerCts;

        private bool _disposed;
        private Task<bool> _reconnectTask;

        public LazerTagSerial() : this(null) { }

        public LazerTagSerial(Action<HostDiagnostic> reportDiagnostic)
        {
            _reportDiagnostic = reportDiagnostic;
        }

        ~LazerTagSerial()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing) return;

            lock (_stateLock)
            {
                if (_disposed) return;
                _disposed = true;
            }

            try
            {
                Disconnect();
            }
            catch
            {
            }

            try
            {
                _serialPort?.Dispose();
            }
            catch
            {
            }

            try
            {
                _workerCts?.Dispose();
            }
            catch
            {
            }


        }

        public static string[] GetSerialPorts()
        {
            try
            {
                return SerialPort.GetPortNames();
            }
            catch (Exception ex)
            {
                Log.Add("GetSerialPorts() failed.", ex);
                return new string[] { };
            }
        }

        public static bool IsTcpAddress(string portName)
        {
            return !string.IsNullOrWhiteSpace(portName)
                   && (portName.Contains(".") || portName.Contains(":"))
                   && !portName.StartsWith("COM", StringComparison.OrdinalIgnoreCase);
        }

        public bool IsConnected
        {
            get
            {
                lock (_stateLock)
                {
                    if (_isTcp)
                    {
                        return _tcpClient != null && _tcpClient.Connected && _tcpStream != null;
                    }

                    return _serialPort != null && _serialPort.IsOpen;
                }
            }
        }

        public bool Connect(string portName)
        {
            lock (_connectionLock) return ConnectCore(portName);
        }

        private bool ConnectCore(string portName)
        {
            if (string.IsNullOrWhiteSpace(portName))
            {
                return false;
            }

            lock (_stateLock)
            {
                if (_disposed) return false;
            }

            try
            {
                DisconnectCore();
                lock (_stateLock)
                {
                    _portName = portName;
                    _isTcp = IsTcpAddress(portName);
                    _readBuffer = string.Empty;
                    _workerCts = new CancellationTokenSource();
                    _outbox = new RelayOutbox();
                }

                if (_isTcp)
                {
                    return ConnectTcpWithRetry(portName);
                }

                return ConnectSerial(portName);
            }
            catch (Exception ex)
            {
                if (!_isTcp) _reportDiagnostic?.Invoke(HostDiagnostic.RelayConnectFailed);
                Log.Add("Connect() failed.", ex);
                DisconnectCore();
                return false;
            }
        }

        private bool ConnectSerial(string portName)
        {
            var serialPort = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
            {
                Handshake = Handshake.None,
                DtrEnable = true,
                RtsEnable = true
            };

            serialPort.PinChanged += SerialPinChanged;
            serialPort.DataReceived += SerialDataReceived;
            serialPort.ErrorReceived += SerialErrorReceived;
            serialPort.Open();

            lock (_stateLock)
            {
                if (_disposed)
                {
                    try { serialPort.Close(); } catch { }
                    serialPort.Dispose();
                    return false;
                }

                _serialPort = serialPort;
                StartWriteThread_NoLock();
            }

            return true;
        }

        private bool ConnectTcpWithRetry(string address)
        {
            for (int attempt = 1; attempt <= TcpConnectRetryCount; attempt++)
            {
                lock (_stateLock)
                {
                    if (_disposed) return false;
                }

                _reportDiagnostic?.Invoke(HostDiagnostic.RelayConnectAttempt);
                HostDiagnostic outcome;
                try
                {
                    outcome = ConnectTcpOnce(address);
                }
                catch (Exception ex)
                {
                    outcome = ex switch
                    {
                        SocketException { SocketErrorCode: SocketError.ConnectionRefused } => HostDiagnostic.RelayConnectionRefused,
                        _ when IsSocketTimeoutException(ex) => HostDiagnostic.RelayConnectTimeout,
                        _ => HostDiagnostic.RelayConnectFailed
                    };
                    Log.Add(Log.Severity.Warning, "TCP connect attempt {0} failed: {1}", attempt, ex.Message);
                    CloseTcpObjects();
                }

                lock (_stateLock)
                {
                    if (_disposed) return false;
                }
                _reportDiagnostic?.Invoke(outcome);
                if (outcome == HostDiagnostic.RelayConnected)
                {
                    if (attempt > 1)
                        Log.Add(Log.Severity.Information, "TCP relay connected on attempt {0}.", attempt);
                    return true;
                }

                if (attempt < TcpConnectRetryCount)
                {
                    Thread.Sleep(TcpConnectRetryDelayMs);
                }
            }

            return false;
        }

        private HostDiagnostic ConnectTcpOnce(string address)
        {
            string host = address;
            int port = TcpDefaultPort;

            int colonIdx = address.LastIndexOf(':');
            if (colonIdx > 0 && int.TryParse(address.Substring(colonIdx + 1), out int parsedPort))
            {
                host = address.Substring(0, colonIdx);
                port = parsedPort;
            }

            Log.Add(Log.Severity.Information, "Connecting to TCP relay at {0}:{1}...", host, port);

            var tcpClient = new TcpClient();
            try
            {
                var connectResult = tcpClient.BeginConnect(host, port, null, null);
                var waitHandle = connectResult.AsyncWaitHandle;
                try
                {
                    if (!waitHandle.WaitOne(TcpConnectTimeoutMs))
                    {
                        try { tcpClient.Close(); } catch { }
                        Log.Add(Log.Severity.Warning, "TCP connection timed out to {0}:{1}", host, port);
                        return HostDiagnostic.RelayConnectTimeout;
                    }

                    tcpClient.EndConnect(connectResult);
                }
                finally
                {
                    waitHandle.Dispose();
                }

                tcpClient.NoDelay = true;
                tcpClient.ReceiveTimeout = TcpReadTimeoutMs;
                tcpClient.SendTimeout = TcpWriteTimeoutMs;

                var tcpStream = tcpClient.GetStream();
                tcpStream.ReadTimeout = HandshakeTimeoutMs;
                tcpStream.WriteTimeout = TcpWriteTimeoutMs;

                var lines = new RelayLines();
                HostDiagnostic handshake;
                try
                {
                    handshake = PerformHandshake(tcpStream, lines);
                }
                catch (IOException ex)
                {
                    Log.Add(Log.Severity.Warning, "TCP relay handshake I/O failed: {0}", ex.Message);
                    handshake = IsSocketTimeoutException(ex) ? HostDiagnostic.RelayHandshakeTimeout : HostDiagnostic.RelayHandshakeFailed;
                }
                if (handshake != HostDiagnostic.RelayConnected)
                {
                    try { tcpStream.Close(); } catch { }
                    try { tcpClient.Close(); } catch { }
                    Log.Add(Log.Severity.Warning, "TCP relay handshake failed at {0}:{1}", host, port);
                    return handshake;
                }

                tcpStream.ReadTimeout = TcpReadTimeoutMs;

                lock (_stateLock)
                {
                    if (_disposed)
                    {
                        try { tcpStream.Close(); } catch { }
                        try { tcpClient.Close(); } catch { }
                        return HostDiagnostic.RelayConnectFailed;
                    }

                    _tcpClient = tcpClient;
                    _tcpStream = tcpStream;

                    StartTcpReadThread_NoLock(tcpStream, lines);
                    StartWriteThread_NoLock();
                }
            }
            catch
            {
                try { tcpClient.Close(); } catch { }
                throw;
            }

            Log.Add(Log.Severity.Information, "TCP relay connected at {0}:{1}", host, port);
            return HostDiagnostic.RelayConnected;
        }

        // One instance follows a socket from HELLO through disconnect. READY may
        // share a read with complete IR lines and the start of the next line.
        private sealed class RelayLines
        {
            private readonly Queue<string> _complete = new Queue<string>();
            private readonly StringBuilder _partial = new StringBuilder();

            public void Append(byte[] bytes, int count)
            {
                foreach (var c in Encoding.ASCII.GetString(bytes, 0, count))
                {
                    if (c != '\r' && c != '\n')
                    {
                        if (_partial.Length >= MaxRelayLineLength) throw new IOException("Relay line exceeds the protocol limit.");
                        _partial.Append(c);
                    }
                    else if (_partial.Length > 0)
                    {
                        _complete.Enqueue(_partial.ToString());
                        _partial.Clear();
                    }
                }
            }

            public bool TryRead(out string line)
            {
                line = _complete.Count == 0 ? null : _complete.Dequeue();
                return line != null;
            }
        }

        private HostDiagnostic PerformHandshake(NetworkStream tcpStream, RelayLines lines)
        {
            byte[] helloBytes = Encoding.ASCII.GetBytes(ProtocolHello);
            tcpStream.Write(helloBytes, 0, helloBytes.Length);
            tcpStream.Flush();

            var handshakeTimer = Stopwatch.StartNew();
            var buf = new byte[128];

            while (handshakeTimer.ElapsedMilliseconds < HandshakeTimeoutMs)
            {
                int n;
                try
                {
                    n = tcpStream.Read(buf, 0, buf.Length);
                }
                catch (IOException ioEx)
                {
                    if (IsSocketTimeoutException(ioEx))
                    {
                        continue;
                    }

                    throw;
                }

                if (n <= 0)
                {
                    return HostDiagnostic.RelayHandshakeFailed;
                }

                lines.Append(buf, n);
                while (lines.TryRead(out var line))
                {
                    if (string.Equals(line, ProtocolReady, StringComparison.OrdinalIgnoreCase))
                    {
                        return HostDiagnostic.RelayConnected;
                    }

                    if (string.Equals(line, ProtocolPong, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (line.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Add(Log.Severity.Warning, "TCP relay handshake error: {0}", line);
                        return HostDiagnostic.RelayHandshakeFailed;
                    }

                    // Ignore any unexpected line during handshake rather than surfacing it.
                }
            }

            return HostDiagnostic.RelayHandshakeTimeout;
        }

        public void Disconnect()
        {
            lock (_connectionLock) DisconnectCore();
        }

        private void DisconnectCore()
        {
            CancellationTokenSource workerCts;
            Thread writeThread;
            Thread tcpReadThread;
            SerialPort serialPort;

            lock (_stateLock)
            {
                _outbox?.Close();
                _outbox = null;
                workerCts = _workerCts;
                _workerCts = null;

                writeThread = _writeThread;
                _writeThread = null;

                tcpReadThread = _tcpReadThread;
                _tcpReadThread = null;

                serialPort = _serialPort;
                _serialPort = null;
            }

            try
            {
                workerCts?.Cancel();
            }
            catch
            {
            }

            CloseTcpObjects();

            if (serialPort != null)
            {
                try
                {
                    serialPort.DataReceived -= SerialDataReceived;
                    serialPort.ErrorReceived -= SerialErrorReceived;
                    serialPort.PinChanged -= SerialPinChanged;
                }
                catch
                {
                }

                try
                {
                    if (serialPort.IsOpen)
                    {
                        serialPort.Close();
                    }
                }
                catch (Exception ex)
                {
                    Log.Add("LazerTagSerial.Disconnect() serial close failed.", ex);
                }

                try
                {
                    serialPort.Dispose();
                }
                catch
                {
                }
            }

            JoinThread(writeThread, WorkerJoinTimeoutMs);
            JoinThread(tcpReadThread, WorkerJoinTimeoutMs);

            try
            {
                workerCts?.Dispose();
            }
            catch
            {
            }


        }

        private void CloseTcpObjects()
        {
            NetworkStream tcpStream;
            TcpClient tcpClient;

            lock (_stateLock)
            {
                tcpStream = _tcpStream;
                _tcpStream = null;

                tcpClient = _tcpClient;
                _tcpClient = null;
            }

            if (tcpStream != null)
            {
                try { tcpStream.Close(); } catch { }
                try { tcpStream.Dispose(); } catch { }
            }

            if (tcpClient != null)
            {
                try { tcpClient.Close(); } catch { }
            }
        }

        private static void JoinThread(Thread thread, int millisecondsTimeout)
        {
            if (thread == null) return;
            if (thread == Thread.CurrentThread) return;

            try
            {
                if (thread.IsAlive)
                {
                    thread.Join(millisecondsTimeout);
                }
            }
            catch
            {
            }
        }

        public void Enqueue(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return;
            int responses = 0;
            for (int i = 0; i + 3 < bytes.Length; i++)
                if ((i == 0 || bytes[i - 1] == '\n' || bytes[i - 1] == '\r') &&
                    bytes[i] == 'C' && bytes[i + 1] == 'M' && bytes[i + 2] == 'D' && bytes[i + 3] == ' ')
                    responses++;
            TryEnqueue(bytes, responses, 2000);
        }

        internal bool TryEnqueue(byte[] bytes, int responses, int lifetimeMs, TaskCompletionSource<bool> receipt = null)
        {
            bool reconnect;
            lock (_stateLock)
            {
                reconnect = !_disposed && !IsConnected;
                if (!reconnect && !_disposed && _outbox != null && _outbox.TryQueue(bytes, responses, lifetimeMs, receipt))
                    return true;
            }
            receipt?.TrySetResult(false);
            _reportDiagnostic?.Invoke(HostDiagnostic.RelayWriteRejected);
            if (reconnect) _ = EnsureConnectedAsync();
            return false;
        }

        public Task<bool> EnsureConnectedAsync()
        {
            lock (_stateLock)
            {
                if (_disposed || string.IsNullOrWhiteSpace(_portName)) return Task.FromResult(false);
                if (IsConnected) return Task.FromResult(true);
                if (_reconnectTask != null && !_reconnectTask.IsCompleted) return _reconnectTask;

                // Writes, the operator and the reconnect timer all share this handshake.
                var portName = _portName;
                _reconnectTask = Task.Run(() =>
                {
                    lock (_connectionLock)
                    {
                        if (portName != _portName) return false;
                        return IsConnected || ConnectCore(portName);
                    }
                });
                return _reconnectTask;
            }
        }

        private void StartWriteThread_NoLock()
        {
            if (_workerCts == null) return;
            if (_writeThread != null && _writeThread.IsAlive) return;

            var token = _workerCts.Token;
            var outbox = _outbox;
            var stream = _tcpStream;
            var serialPort = _serialPort;
            _writeThread = new Thread(() => WriteThreadProc(token, outbox, stream, serialPort))
            {
                IsBackground = true,
                Name = stream != null ? "TcpWrite" : "SerialWrite"
            };
            _writeThread.Start();
        }

        private void StartTcpReadThread_NoLock(NetworkStream stream, RelayLines lines)
        {
            if (_workerCts == null) return;
            if (_tcpReadThread != null && _tcpReadThread.IsAlive) return;

            var token = _workerCts.Token;
            var outbox = _outbox;
            _tcpReadThread = new Thread(() => TcpReadThreadProc(stream, lines, outbox, token))
            {
                IsBackground = true,
                Name = "TcpRead"
            };
            _tcpReadThread.Start();
        }

        private void WriteThreadProc(CancellationToken token, RelayOutbox outbox, NetworkStream stream, SerialPort serialPort)
        {
            RelayOutbox.Write write = null;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (stream != null) outbox.CheckReplyDeadline();
                    if (!outbox.TryTake(out write, token)) continue;
                    if (!outbox.BeginWrite(write, stream != null))
                    {
                        _reportDiagnostic?.Invoke(HostDiagnostic.RelayWriteRejected);
                        continue;
                    }

                    if (stream != null)
                    {
                        stream.Write(write.Bytes, 0, write.Bytes.Length);
                        stream.Flush();
                    }
                    else
                    {
                        serialPort.Write(write.Bytes, 0, write.Bytes.Length);
                        serialPort.BaseStream.Flush();
                        write.Receipt?.TrySetResult(true);
                        Thread.Sleep(InterSignatureDelayMilliseconds);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (ThreadInterruptedException) { }
            catch (Exception ex)
            {
                write?.Receipt?.TrySetResult(false);
                HandleTransportFailure("WriteThreadProc()", ex, token);
            }
        }

        private void TcpReadThreadProc(NetworkStream stream, RelayLines lines, RelayOutbox outbox, CancellationToken token)
        {
            try
            {
                var buf = new byte[256];
                var idle = Stopwatch.StartNew();
                bool pingPending = false;

                while (!token.IsCancellationRequested)
                {
                    while (lines.TryRead(out var line))
                    {
                        if (line == "OK") outbox.Reply(true);
                        else if (line.StartsWith("ERROR", StringComparison.Ordinal))
                        {
                            outbox.Reply(false);
                            _reportDiagnostic?.Invoke(HostDiagnostic.RelayWriteRejected);
                        }
                        if (!string.Equals(line, ProtocolReady, StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(line, ProtocolPong, StringComparison.OrdinalIgnoreCase))
                            OnDataReceived(new DataReceivedEventArgs(line, token));
                    }

                    int n;
                    try
                    {
                        n = stream.Read(buf, 0, buf.Length);
                    }
                    catch (IOException ioEx) when (IsSocketTimeoutException(ioEx))
                    {
                        if (pingPending && idle.Elapsed >= PingResponseTimeout)
                            throw new IOException("The relay did not respond to PING.", ioEx);
                        if (!pingPending && idle.Elapsed >= PingAfterIdle)
                        {
                            if (!outbox.TryQueue(Ping, 0, 1000)) throw new IOException("Relay output queue is full.");
                            pingPending = true;
                            idle.Restart();
                        }
                        continue;
                    }

                    if (n <= 0)
                    {
                        throw new IOException("TCP stream closed by remote host.");
                    }

                    idle.Restart();
                    pingPending = false;
                    lines.Append(buf, n);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                HandleTransportFailure("TcpReadThread()", ex, token);
            }
        }

        private static void SerialPinChanged(object sender, SerialPinChangedEventArgs e)
        {
            Log.Add(Log.Severity.Debug, "SerialPinChanged(): {0}", e.EventType);
        }

        private void SerialDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            CancellationToken token;
            SerialPort serialPort;
            lock (_stateLock)
            {
                serialPort = _serialPort;
                if (sender != serialPort || serialPort == null) return;
                token = _workerCts.Token;
            }

            try
            {
                _readBuffer += serialPort.ReadExisting();
                var lines = SplitBufferLines(_readBuffer, out var remainingBuffer);
                if (remainingBuffer.Length > MaxRelayLineLength) throw new IOException("Serial relay line exceeds the protocol limit.");
                _readBuffer = remainingBuffer;
                foreach (var line in lines)
                {
                    if (line.Length > MaxRelayLineLength) throw new IOException("Serial relay line exceeds the protocol limit.");
                    OnDataReceived(new DataReceivedEventArgs(line, token));
                }
            }
            catch (Exception ex)
            {
                HandleTransportFailure("SerialDataReceived()", ex, token);
            }
        }

        private static void SerialErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        {
            Log.Add(Log.Severity.Debug, "SerialErrorReceived(): {0}", e.EventType);
        }

        private void HandleTransportFailure(string context, Exception ex, CancellationToken token)
        {
            if (token.IsCancellationRequested) return;
            lock (_connectionLock)
            {
                // A late failure from an old reader or writer must not close its replacement.
                if (token.IsCancellationRequested) return;
                DisconnectCore();
            }
            Log.Add(context + " failed.", ex);
            OnIoError(new IoErrorEventArgs(null, ex));
        }

        private static bool IsSocketTimeoutException(Exception ex)
        {
            var socketError = ex as SocketException ?? (ex as IOException)?.InnerException as SocketException;
            return socketError?.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock;
        }

        private static bool IsExpectedDisconnectException(Exception ex)
        {
            if (ex == null) return false;

            if (ex is ObjectDisposedException) return true;

            var ioEx = ex as IOException;
            if (ioEx != null)
            {
                var ioSocketEx = ioEx.InnerException as SocketException;
                if (ioSocketEx != null)
                {
                    switch (ioSocketEx.SocketErrorCode)
                    {
                        case SocketError.Interrupted:
                        case SocketError.OperationAborted:
                        case SocketError.ConnectionAborted:
                        case SocketError.ConnectionReset:
                        case SocketError.Shutdown:
                        case SocketError.NotConnected:
                            return true;
                    }
                }
            }

            var socketEx = ex as SocketException;
            if (socketEx != null)
            {
                switch (socketEx.SocketErrorCode)
                {
                    case SocketError.Interrupted:
                    case SocketError.OperationAborted:
                    case SocketError.ConnectionAborted:
                    case SocketError.ConnectionReset:
                    case SocketError.Shutdown:
                    case SocketError.NotConnected:
                        return true;
                }
            }

            var msg = ex.Message ?? string.Empty;
            if (msg.IndexOf("WSACancelBlockingCall", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (msg.IndexOf("blocking operation was interrupted", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (msg.IndexOf("forcibly closed by the remote host", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (msg.IndexOf("An existing connection was forcibly", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (msg.IndexOf("closed by remote host", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            return ex.InnerException != null && IsExpectedDisconnectException(ex.InnerException);
        }

        private static IEnumerable<string> SplitBufferLines(string buffer, out string remainingBuffer)
        {
            var lines = new List<string>();
            var match = Regex.Match(buffer, @"(.+?)(\r?\n|\r)");
            var lastMatchPosition = 0;

            while (match.Success)
            {
                if (match.Groups.Count >= 2)
                {
                    lines.Add(match.Groups[1].Value);
                }

                lastMatchPosition = match.Index + match.Length;
                match = match.NextMatch();
            }

            remainingBuffer = buffer.Substring(lastMatchPosition);
            return lines;
        }

        public class IoErrorEventArgs : EventArgs
        {
            public IoErrorEventArgs(string message, Exception ex)
            {
                Message = message;
                Exception = ex;
            }

            public string Message;
            public Exception Exception;
        }

        public delegate void IoErrorEventHandler(object sender, IoErrorEventArgs e);
        public event IoErrorEventHandler IoError;

        protected virtual void OnIoError(IoErrorEventArgs e)
        {
            var handler = IoError;
            if (handler != null) handler(this, e);
        }

        public class DataReceivedEventArgs : EventArgs
        {
            public DataReceivedEventArgs(string data) : this(data, CancellationToken.None) { }

            internal DataReceivedEventArgs(string data, CancellationToken connection)
            {
                Data = data;
                Connection = connection;
            }

            internal CancellationToken Connection { get; }

            public string Data { get; set; }
        }

        public delegate void DataReceivedEventHandler(object sender, DataReceivedEventArgs e);
        public event DataReceivedEventHandler DataReceived;

        protected virtual void OnDataReceived(DataReceivedEventArgs e)
        {
            var handler = DataReceived;
            if (handler != null) handler(this, e);
        }
    }
}
