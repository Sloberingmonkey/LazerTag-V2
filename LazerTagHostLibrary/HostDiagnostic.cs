namespace LazerTagHostLibrary
{
    public enum HostDiagnostic
    {
        RelayConnectAttempt,
        RelayConnected,
        RelayConnectionRefused,
        RelayConnectTimeout,
        RelayHandshakeFailed,
        RelayHandshakeTimeout,
        RelayConnectFailed,
        RelayWriteRejected,
        UnexpectedPacket,
        StateTimeout
    }
}
