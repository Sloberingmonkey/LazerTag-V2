using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

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
        private const string ProtocolHello = "HELLO 1\r\n";
        private const string ProtocolReady = "READY 1";
        private const string ProtocolPong = "PONG";

        private readonly object _stateLock = new object();
        private readonly BlockingCollection<byte[]> _writeQueue;

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

        public LazerTagSerial()
        {
            _writeQueue = new BlockingCollection<byte[]>();
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

            try
            {
                _writeQueue?.Dispose();
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
                Disconnect();

                lock (_stateLock)
                {
                    _portName = portName;
                    _isTcp = IsTcpAddress(portName);
                    _readBuffer = string.Empty;
                    _workerCts = new CancellationTokenSource();
                }

                if (_isTcp)
                {
                    return ConnectTcpWithRetry(portName);
                }

                return ConnectSerial(portName);
            }
            catch (Exception ex)
            {
                Log.Add("Connect() failed.", ex);
                Disconnect();
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

                try
                {
                    if (ConnectTcpOnce(address))
                    {
                        if (attempt > 1)
                        {
                            Log.Add(Log.Severity.Information, "TCP relay connected on attempt {0}.", attempt);
                        }

                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Log.Add(Log.Severity.Warning, "TCP connect attempt {0} failed: {1}", attempt, ex.Message);
                    CloseTcpObjects();
                }

                if (attempt < TcpConnectRetryCount)
                {
                    Thread.Sleep(TcpConnectRetryDelayMs);
                }
            }

            return false;
        }

        private bool ConnectTcpOnce(string address)
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
                        return false;
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

                if (!PerformHandshake(tcpStream))
                {
                    try { tcpStream.Close(); } catch { }
                    try { tcpClient.Close(); } catch { }
                    Log.Add(Log.Severity.Warning, "TCP relay handshake failed at {0}:{1}", host, port);
                    return false;
                }

                tcpStream.ReadTimeout = TcpReadTimeoutMs;

                lock (_stateLock)
                {
                    if (_disposed)
                    {
                        try { tcpStream.Close(); } catch { }
                        try { tcpClient.Close(); } catch { }
                        return false;
                    }

                    _tcpClient = tcpClient;
                    _tcpStream = tcpStream;

                    StartTcpReadThread_NoLock();
                    StartWriteThread_NoLock();
                }
            }
            catch
            {
                try { tcpClient.Close(); } catch { }
                throw;
            }

            Log.Add(Log.Severity.Information, "TCP relay connected at {0}:{1}", host, port);
            return true;
        }

        private bool PerformHandshake(NetworkStream tcpStream)
        {
            byte[] helloBytes = Encoding.ASCII.GetBytes(ProtocolHello);
            tcpStream.Write(helloBytes, 0, helloBytes.Length);
            tcpStream.Flush();

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(HandshakeTimeoutMs);
            string handshakeBuffer = string.Empty;
            var buf = new byte[128];

            while (DateTime.UtcNow < deadline)
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
                    return false;
                }

                handshakeBuffer += Encoding.ASCII.GetString(buf, 0, n);
                string remainingBuffer;
                foreach (var line in SplitBufferLines(handshakeBuffer, out remainingBuffer))
                {
                    handshakeBuffer = remainingBuffer;
                    if (string.Equals(line, ProtocolReady, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    if (string.Equals(line, ProtocolPong, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (line.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Add(Log.Severity.Warning, "TCP relay handshake error: {0}", line);
                        return false;
                    }

                    // Ignore any unexpected line during handshake rather than surfacing it.
                }
            }

            return false;
        }

        public void Disconnect()
        {
            CancellationTokenSource workerCts;
            Thread writeThread;
            Thread tcpReadThread;
            SerialPort serialPort;

            lock (_stateLock)
            {
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

            DrainWriteQueue();
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

        private void DrainWriteQueue()
        {
            byte[] ignored;
            while (_writeQueue.TryTake(out ignored, 0))
            {
            }
        }

        public void Enqueue(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return;
            }

            lock (_stateLock)
            {
                if (_disposed) return;
            }

            if (!IsConnected)
            {
                if (string.IsNullOrWhiteSpace(_portName))
                {
                    return;
                }

                if (!Connect(_portName))
                {
                    return;
                }
            }

            try
            {
                _writeQueue.Add(bytes);
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void StartWriteThread_NoLock()
        {
            if (_workerCts == null) return;
            if (_writeThread != null && _writeThread.IsAlive) return;

            _writeThread = new Thread(WriteThreadProc)
            {
                IsBackground = true,
                Name = _isTcp ? "TcpWrite" : "SerialWrite"
            };
            _writeThread.Start();
        }

        private void StartTcpReadThread_NoLock()
        {
            if (_workerCts == null) return;
            if (_tcpReadThread != null && _tcpReadThread.IsAlive) return;

            _tcpReadThread = new Thread(TcpReadThreadProc)
            {
                IsBackground = true,
                Name = "TcpRead"
            };
            _tcpReadThread.Start();
        }

        private void WriteThreadProc()
        {
            CancellationToken token;
            bool isTcp;

            lock (_stateLock)
            {
                token = _workerCts != null ? _workerCts.Token : CancellationToken.None;
                isTcp = _isTcp;
            }

            try
            {
                while (!token.IsCancellationRequested)
                {
                    byte[] data;
                    if (!_writeQueue.TryTake(out data, 100, token))
                    {
                        continue;
                    }

                    if (data == null || data.Length == 0)
                    {
                        continue;
                    }

                    if (isTcp)
                    {
                        NetworkStream stream;
                        lock (_stateLock)
                        {
                            stream = _tcpStream;
                        }

                        if (stream == null)
                        {
                            throw new IOException("TCP stream is not available.");
                        }

                        stream.Write(data, 0, data.Length);
                        stream.Flush();
                    }
                    else
                    {
                        SerialPort serialPort;
                        lock (_stateLock)
                        {
                            serialPort = _serialPort;
                        }

                        if (serialPort == null || !serialPort.IsOpen)
                        {
                            throw new IOException("Serial port is not open.");
                        }

                        serialPort.Write(data, 0, data.Length);
                        serialPort.BaseStream.Flush();
                        Thread.Sleep(InterSignatureDelayMilliseconds);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ThreadInterruptedException)
            {
            }
            catch (Exception ex)
            {
                HandleTransportFailure("WriteThreadProc()", ex);
            }
        }

        private void TcpReadThreadProc()
        {
            CancellationToken token;

            lock (_stateLock)
            {
                token = _workerCts != null ? _workerCts.Token : CancellationToken.None;
            }

            try
            {
                var buf = new byte[256];

                while (!token.IsCancellationRequested)
                {
                    NetworkStream stream;
                    TcpClient client;

                    lock (_stateLock)
                    {
                        stream = _tcpStream;
                        client = _tcpClient;
                    }

                    if (stream == null || client == null)
                    {
                        break;
                    }

                    int n;
                    try
                    {
                        n = stream.Read(buf, 0, buf.Length);
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
                        throw new IOException("TCP stream closed by remote host.");
                    }

                    _readBuffer += Encoding.ASCII.GetString(buf, 0, n);
                    string remainingBuffer;
                    foreach (var line in SplitBufferLines(_readBuffer, out remainingBuffer))
                    {
                        _readBuffer = remainingBuffer;

                        if (string.Equals(line, ProtocolReady, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(line, ProtocolPong, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        OnDataReceived(new DataReceivedEventArgs(line));
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (IsExpectedDisconnectException(ex))
                {
                    Log.Add(Log.Severity.Debug, "TcpReadThread(): socket closed (expected disconnect).");
                }
                else
                {
                    HandleTransportFailure("TcpReadThread()", ex);
                }
            }
        }

        private static void SerialPinChanged(object sender, SerialPinChangedEventArgs e)
        {
            Log.Add(Log.Severity.Debug, "SerialPinChanged(): {0}", e.EventType);
        }

        private void SerialDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            SerialPort serialPort;
            lock (_stateLock)
            {
                serialPort = _serialPort;
            }

            if (sender != serialPort || serialPort == null) return;

            try
            {
                _readBuffer += serialPort.ReadExisting();
                string remainingBuffer;
                foreach (var line in SplitBufferLines(_readBuffer, out remainingBuffer))
                {
                    _readBuffer = remainingBuffer;
                    OnDataReceived(new DataReceivedEventArgs(line));
                }
            }
            catch (Exception ex)
            {
                HandleTransportFailure("SerialDataReceived()", ex);
            }
        }

        private static void SerialErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        {
            Log.Add(Log.Severity.Debug, "SerialErrorReceived(): {0}", e.EventType);
        }

        private void HandleTransportFailure(string context, Exception ex)
        {
            if (ex == null) return;

            if (IsExpectedDisconnectException(ex))
            {
                Log.Add(Log.Severity.Debug, "{0} socket closed (expected disconnect).", context);
            }
            else
            {
                Log.Add(context + " failed.", ex);
                OnIoError(new IoErrorEventArgs(null, ex));
            }

            try
            {
                Disconnect();
            }
            catch
            {
            }
        }

        private static bool IsSocketTimeoutException(Exception ex)
        {
            if (ex == null) return false;

            var ioEx = ex as IOException;
            if (ioEx != null)
            {
                var innerSocketEx = ioEx.InnerException as SocketException;
                if (innerSocketEx != null)
                {
                    return innerSocketEx.SocketErrorCode == SocketError.TimedOut;
                }
            }

            var socketEx = ex as SocketException;
            if (socketEx != null)
            {
                return socketEx.SocketErrorCode == SocketError.TimedOut;
            }

            return false;
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
            public DataReceivedEventArgs(string data)
            {
                Data = data;
            }

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
