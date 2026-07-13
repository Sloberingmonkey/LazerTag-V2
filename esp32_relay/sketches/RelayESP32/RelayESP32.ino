// LazerTag IR Relay — ESP32 WiFi TCP version (state machine rewrite)
// Uses ESP32_IR_LTTO (RMT hardware) for precise IR timing.
//
// Protocol:
//   Host  -> Relay: HELLO 1
//   Host  -> Relay: PING
//   Host  -> Relay: BYE
//   Host  -> Relay: CMD <cmdHex> <dataHex> <bitsHex> <beaconFlag>
//   Relay -> Host : READY 1
//   Relay -> Host : PONG
//   Relay -> Host : OK
//   Relay -> Host : RCV <valueHex> <bitsHex> <isBeacon>
//   Relay -> Host : ERROR <code>
//
// Wiring:
//   GPIO 15 — IR receiver (TSOP4838 OUT)
//   GPIO 4  — IR transmit (via transistor/FET + IR LED)
//
// ── CONFIGURATION ─────────────────────────────────────────────────
// The Pi creates the "LazerTag" hotspot; RelaySecrets.h supplies credentials.
// The ESP32 joins it as a client (STA mode).
// USE_AP is commented out — do NOT uncomment it.
// ─────────────────────────────────────────────────────────────────

#include <WiFi.h>
#include <ESPmDNS.h>
#include "ESP32_IR_LTTO.h"

// ── WiFi mode ─────────────────────────────────────────────────────
// USE_AP  — ESP32 creates its own hotspot (old setup, not used anymore)
// STA     — ESP32 joins the Pi's hotspot (current setup)
//
// Leave USE_AP commented out. The Pi is the hotspot now.
// #define USE_AP

#include "RelaySecrets.h"
#ifndef LAZERTAG_WIFI_SSID
  #error "RelaySecrets.h must define LAZERTAG_WIFI_SSID"
#endif
#ifndef LAZERTAG_WIFI_PASSWORD
  #error "RelaySecrets.h must define LAZERTAG_WIFI_PASSWORD"
#endif
static_assert(sizeof(LAZERTAG_WIFI_SSID) > 1 && sizeof(LAZERTAG_WIFI_SSID) <= 33,
              "Arena SSID must contain 1-32 characters");
static_assert(sizeof(LAZERTAG_WIFI_PASSWORD) >= 9 && sizeof(LAZERTAG_WIFI_PASSWORD) <= 65,
              "Arena WPA2 password must contain 8-64 characters");

#ifdef USE_AP
  #define AP_SSID    LAZERTAG_WIFI_SSID
  #define AP_PASS    LAZERTAG_WIFI_PASSWORD
#else
  // Copy RelaySecrets.example.h to RelaySecrets.h before building. The real
  // credential is deliberately excluded from version control.
  #define WIFI_SSID  LAZERTAG_WIFI_SSID
  #define WIFI_PASS  LAZERTAG_WIFI_PASSWORD
#endif

// The Pi/controller is 192.168.4.1; this ESP32 listens at 192.168.4.100:8266.
#define TCP_PORT    8266
#define MDNS_NAME   "lazertag"

#ifndef USE_AP
const IPAddress STA_LOCAL_IP(192, 168, 4, 100);
const IPAddress STA_GATEWAY(192, 168, 4, 1);
const IPAddress STA_SUBNET(255, 255, 255, 0);
const IPAddress STA_DNS(192, 168, 4, 1);
#endif

#define RX_PIN       15
#define TX_PIN        4
#define RX_CHANNEL    0
#define TX_CHANNEL    1

#define BUF_SIZE     96
#define STATS_PRINT_INTERVAL_MS 30000UL
#define STA_RECONNECT_INTERVAL_MS 5000UL
#define WIFI_START_DELAY_MS 500UL

enum class WifiState
{
    Starting,
    Ready,
    RetryWait,
    Fault
};

enum class SessionState
{
    NoClient,
    HandshakePending,
    Ready,
    Closing
};

enum class CommandType
{
    Invalid,
    Hello,
    Ping,
    Bye,
    Cmd
};

struct ParsedCommand
{
    CommandType type = CommandType::Invalid;
    unsigned int cmd = 0;
    unsigned int data = 0;
    unsigned int bits = 0;
    unsigned int beacon = 0;
    const char* errorCode = nullptr;
};

struct RelayStats
{
    uint32_t tcpConnectCount = 0;
    uint32_t tcpDisconnectCount = 0;
    uint32_t handshakeSuccessCount = 0;
    uint32_t handshakeFailureCount = 0;
    uint32_t cmdCount = 0;
    uint32_t parseErrorCount = 0;
    uint32_t lineOverflowCount = 0;
    uint32_t txCount = 0;
    uint32_t txFailureCount = 0;
    uint32_t rxCount = 0;
    uint32_t rxUnknownCount = 0;
    uint32_t tcpWriteFailureCount = 0;
};

struct ClientSession
{
    WiFiClient client;
    SessionState state = SessionState::NoClient;
    char lineBuf[BUF_SIZE];
    uint8_t linePos = 0;
    char prevChar = '\0';
    bool handshakeComplete = false;
    IPAddress remoteIp;
    uint32_t connectedAtMs = 0;
};

struct RelayContext
{
    WifiState wifiState = WifiState::Starting;
    WiFiServer server = WiFiServer(TCP_PORT);
    ClientSession session;
    RelayStats stats;
    uint32_t lastStatsPrintMs = 0;
    uint32_t lastStaReconnectAttemptMs = 0;
    bool serverStarted = false;
    bool irReady = false;
};

ESP32_IR irRx;
ESP32_IR irTx;
static unsigned int irDataBuf[50];
static RelayContext gRelay;

// ── Forward declarations ──────────────────────────────────────────
void initializeIr();
void startWiFi();
bool configureStaNetwork();
void beginStaConnection();
void wifiManagerTick();
void sessionManagerTick();
void tcpReadTick();
void irReceiveTick();
void bridgeTick();
bool isRelayReady();
void startServerIfNeeded();
void ensureMdns();
void resetSession(ClientSession& s);
void closeClientSession(const char* reason);
bool sendLine(const char* line);
void handleLine(char* line);
ParsedCommand parseCommand(const char* line);
void handleParsedCommand(const ParsedCommand& cmd);
bool handleHello(const ParsedCommand& cmd);
void handlePing();
void handleBye();
void handleCmd(const ParsedCommand& cmd);
const char* validateIrCommand(const ParsedCommand& cmd);
bool transmitIrCommand(unsigned int cmd, unsigned int data, unsigned int bits, unsigned int beacon);
void processSignature();
void printStatsIfDue();
const char* wifiStateName(WifiState state);
const char* sessionStateName(SessionState state);

// ─────────────────────────────────────────────────────────────────
void setup()
{
    Serial.begin(115200);
    delay(100);
    Serial.println(F("\nLazerTag IR Relay — state machine booting"));

    initializeIr();
    startWiFi();

    delay(WIFI_START_DELAY_MS);
    startServerIfNeeded();

    Serial.println(F("Relay boot complete."));
}

void loop()
{
    wifiManagerTick();
    sessionManagerTick();
    // Service a completed IR capture before accepting another outbound command.
    // HostGun deliberately filters its own echoed packets, while starving this
    // receive path lets the legacy RMT ring buffer fill and disables tagger RX.
    irReceiveTick();
    tcpReadTick();
    bridgeTick();
    printStatsIfDue();
}

// ── Initialization ────────────────────────────────────────────────
void initializeIr()
{
    const bool rxReady = irRx.ESP32_IRrxPIN(RX_PIN, RX_CHANNEL) && irRx.initReceive();
    const bool txReady = irTx.ESP32_IRtxPIN(TX_PIN, TX_CHANNEL) && irTx.initTransmit();
    gRelay.irReady = rxReady && txReady;
    if (!gRelay.irReady)
    {
        Serial.println(F("ERROR: RMT IR initialization failed."));
    }
}

// ── WiFi manager ──────────────────────────────────────────────────
void startWiFi()
{
    WiFi.persistent(false);
    WiFi.setSleep(false);

#ifdef USE_AP
    WiFi.mode(WIFI_AP);

    bool apOk = WiFi.softAP(AP_SSID, AP_PASS[0] ? AP_PASS : nullptr);
    if (!apOk)
    {
        gRelay.wifiState = WifiState::Fault;
        Serial.println(F("ERROR: softAP start failed."));
        return;
    }

    gRelay.wifiState = WifiState::Ready;
    Serial.printf("AP: \"%s\"  IP: %s\n", AP_SSID, WiFi.softAPIP().toString().c_str());

    if (MDNS.begin(MDNS_NAME))
    {
        Serial.printf("mDNS: %s.local\n", MDNS_NAME);
    }
    else
    {
        Serial.println(F("mDNS start failed."));
    }
#else
    WiFi.mode(WIFI_STA);

    if (!configureStaNetwork())
    {
        gRelay.wifiState = WifiState::Fault;
        return;
    }
    beginStaConnection();

    Serial.printf("Connecting to \"%s\"", WIFI_SSID);

    uint32_t startMs = millis();
    while (WiFi.status() != WL_CONNECTED)
    {
        delay(400);
        Serial.print('.');
        if (millis() - startMs > 20000UL)
        {
            Serial.println(F("\nERROR: WiFi STA connect timeout."));
            gRelay.wifiState = WifiState::RetryWait;
            gRelay.lastStaReconnectAttemptMs = millis();
            return;
        }
    }

    Serial.println();
    Serial.printf("Connected! IP: %s\n", WiFi.localIP().toString().c_str());
    Serial.printf("Relay endpoint: %s:%d\n", WiFi.localIP().toString().c_str(), TCP_PORT);

    gRelay.wifiState = WifiState::Ready;

    if (MDNS.begin(MDNS_NAME))
    {
        Serial.printf("mDNS: %s.local:%d\n", MDNS_NAME, TCP_PORT);
    }
    else
    {
        Serial.println(F("mDNS start failed."));
    }
#endif
}

bool configureStaNetwork()
{
#ifdef USE_AP
    return true;
#else
    if (!WiFi.config(STA_LOCAL_IP, STA_GATEWAY, STA_SUBNET, STA_DNS))
    {
        Serial.println(F("ERROR: failed to configure static STA network."));
        return false;
    }
    return true;
#endif
}

void beginStaConnection()
{
#ifndef USE_AP
    // Production builds require the protected hotspot credential from RelaySecrets.h.
    if (strlen(WIFI_PASS) > 0)
    {
        WiFi.begin(WIFI_SSID, WIFI_PASS);
    }
    else
    {
        WiFi.begin(WIFI_SSID);
    }
#endif
}

void wifiManagerTick()
{
#ifdef USE_AP
    if (gRelay.wifiState != WifiState::Ready)
    {
        if (WiFi.getMode() == WIFI_AP)
        {
            gRelay.wifiState = WifiState::Ready;
        }
    }
#else
    if (WiFi.status() == WL_CONNECTED)
    {
        if (gRelay.wifiState != WifiState::Ready)
        {
            gRelay.wifiState = WifiState::Ready;
            Serial.printf("WiFi ready. IP: %s\n", WiFi.localIP().toString().c_str());
            ensureMdns();
        }
        return;
    }

    uint32_t now = millis();
    if (now - gRelay.lastStaReconnectAttemptMs < STA_RECONNECT_INTERVAL_MS)
    {
        gRelay.wifiState = WifiState::RetryWait;
        return;
    }

    gRelay.lastStaReconnectAttemptMs = now;
    gRelay.wifiState = WifiState::RetryWait;
    Serial.println(F("WiFi lost — reconnecting..."));
    WiFi.disconnect();

    if (!configureStaNetwork())
    {
        gRelay.wifiState = WifiState::Fault;
        return;
    }
    beginStaConnection();

    for (int i = 0; i < 10 && WiFi.status() != WL_CONNECTED; ++i)
    {
        delay(300);
    }

    if (WiFi.status() == WL_CONNECTED)
    {
        gRelay.wifiState = WifiState::Ready;
        Serial.printf("Reconnected. IP: %s\n", WiFi.localIP().toString().c_str());
        ensureMdns();
    }
#endif
}

void ensureMdns()
{
    MDNS.end();
    if (MDNS.begin(MDNS_NAME))
    {
        Serial.printf("mDNS restarted: %s.local\n", MDNS_NAME);
    }
    else
    {
        Serial.println(F("mDNS restart failed."));
    }
}

void startServerIfNeeded()
{
    if (gRelay.serverStarted)
    {
        return;
    }

    gRelay.server.begin();
    gRelay.server.setNoDelay(true);
    gRelay.serverStarted = true;
    Serial.printf("TCP server on port %d\n", TCP_PORT);
}

bool isRelayReady()
{
    return gRelay.irReady
        && gRelay.serverStarted
        && gRelay.wifiState == WifiState::Ready;
}

// ── Session manager ───────────────────────────────────────────────
void resetSession(ClientSession& s)
{
    s.linePos = 0;
    s.prevChar = '\0';
    s.handshakeComplete = false;
    s.state = SessionState::NoClient;
    s.remoteIp = IPAddress();
    s.connectedAtMs = 0;
    if (BUF_SIZE > 0)
    {
        s.lineBuf[0] = '\0';
    }
}

void closeClientSession(const char* reason)
{
    ClientSession& s = gRelay.session;

    if (s.client)
    {
        if (reason != nullptr)
        {
            Serial.print(F("Closing client session: "));
            Serial.println(reason);
        }

        s.client.stop();
        gRelay.stats.tcpDisconnectCount++;
    }

    resetSession(s);
}

void sessionManagerTick()
{
    if (!gRelay.serverStarted || !isRelayReady())
    {
        return;
    }

    ClientSession& s = gRelay.session;

    if (s.state != SessionState::NoClient)
    {
        if (!s.client.connected())
        {
            Serial.println(F("Client disconnected."));
            closeClientSession("remote disconnect");
        }
        return;
    }

    WiFiClient newClient = gRelay.server.accept();
    if (newClient)
    {
        if (s.client)
        {
            s.client.stop();
        }

        s.client = newClient;
        s.client.setNoDelay(true);
        s.state = SessionState::HandshakePending;
        s.remoteIp = s.client.remoteIP();
        s.connectedAtMs = millis();
        s.handshakeComplete = false;
        s.linePos = 0;
        s.prevChar = '\0';
        s.lineBuf[0] = '\0';
        gRelay.stats.tcpConnectCount++;

        Serial.print(F("Client connected: "));
        Serial.println(s.remoteIp);
    }
}

// ── TCP parser / command handling ─────────────────────────────────
bool sendLine(const char* line)
{
    ClientSession& s = gRelay.session;

    if (line == nullptr)
    {
        gRelay.stats.tcpWriteFailureCount++;
        return false;
    }

    if (s.state == SessionState::NoClient)
    {
        return false;
    }

    if (!s.client || !s.client.connected())
    {
        closeClientSession("send attempted on dead client");
        return false;
    }

    char out[128];
    const size_t maxPayloadLength = sizeof(out) - 2U;
    const size_t lineLength = strnlen(line, maxPayloadLength + 1U);
    if (lineLength > maxPayloadLength)
    {
        gRelay.stats.tcpWriteFailureCount++;
        Serial.println(F("ERROR: outbound TCP line too long."));
        return false;
    }

    memcpy(out, line, lineLength);
    size_t len = lineLength;
    out[len++] = '\r';
    out[len++] = '\n';

    size_t written = s.client.write((const uint8_t*)out, len);
    if (written != (size_t)len)
    {
        gRelay.stats.tcpWriteFailureCount++;
        closeClientSession("partial tcp write");
        return false;
    }

    return true;
}

void tcpReadTick()
{
    ClientSession& s = gRelay.session;

    if (s.state == SessionState::NoClient)
    {
        return;
    }

    if (!s.client || !s.client.connected())
    {
        closeClientSession("read on dead client");
        return;
    }

    while (s.client.available() > 0)
    {
        char c = (char)s.client.read();

        if (s.prevChar == '\r' && c == '\n')
        {
            s.lineBuf[s.linePos > 0 ? s.linePos - 1 : 0] = '\0';
            if (s.linePos > 1)
            {
                handleLine(s.lineBuf);
            }
            s.linePos = 0;
            s.prevChar = '\0';
            s.lineBuf[0] = '\0';
            // Process at most one line per loop iteration. A game announcement
            // arrives as many CMD lines in one TCP write; draining the entire
            // socket here would block irReceiveTick while RMT queues self-echo.
            return;
        }

        if (s.linePos < BUF_SIZE - 1)
        {
            s.lineBuf[s.linePos++] = c;
            s.lineBuf[s.linePos] = '\0';
        }
        else
        {
            gRelay.stats.lineOverflowCount++;
            gRelay.stats.parseErrorCount++;
            sendLine("ERROR LINE_TOO_LONG");
            closeClientSession("line overflow");
            return;
        }

        s.prevChar = c;
    }
}

void handleLine(char* line)
{
    ParsedCommand cmd = parseCommand(line);
    handleParsedCommand(cmd);
}

ParsedCommand parseCommand(const char* line)
{
    ParsedCommand result;

    if (line == nullptr || line[0] == '\0')
    {
        result.errorCode = "BAD_FORMAT";
        return result;
    }

    if (strcmp(line, "PING") == 0)
    {
        result.type = CommandType::Ping;
        return result;
    }

    if (strcmp(line, "BYE") == 0)
    {
        result.type = CommandType::Bye;
        return result;
    }

    unsigned int version = 0;
    int consumed = -1;
    if (sscanf(line, "HELLO %u %n", &version, &consumed) == 1
        && consumed >= 0
        && line[consumed] == '\0')
    {
        if (version == 1)
        {
            result.type = CommandType::Hello;
        }
        else
        {
            result.errorCode = "BAD_VERSION";
        }
        return result;
    }

    unsigned int cmd = 0;
    unsigned int data = 0;
    unsigned int bits = 0;
    unsigned int beacon = 0;

    consumed = -1;
    if (sscanf(line, "CMD %x %x %x %x %n", &cmd, &data, &bits, &beacon, &consumed) == 4
        && consumed >= 0
        && line[consumed] == '\0')
    {
        result.type = CommandType::Cmd;
        result.cmd = cmd;
        result.data = data;
        result.bits = bits;
        result.beacon = beacon;
        return result;
    }

    result.errorCode = "BAD_FORMAT";
    return result;
}

void handleParsedCommand(const ParsedCommand& cmd)
{
    ClientSession& s = gRelay.session;

    if (cmd.type == CommandType::Invalid)
    {
        gRelay.stats.parseErrorCount++;
        sendLine(cmd.errorCode != nullptr ? (String("ERROR ") + cmd.errorCode).c_str() : "ERROR BAD_FORMAT");
        return;
    }

    switch (cmd.type)
    {
        case CommandType::Hello:
            if (!handleHello(cmd))
            {
                gRelay.stats.handshakeFailureCount++;
            }
            break;

        case CommandType::Ping:
            handlePing();
            break;

        case CommandType::Bye:
            handleBye();
            break;

        case CommandType::Cmd:
            if (!s.handshakeComplete || s.state != SessionState::Ready)
            {
                gRelay.stats.parseErrorCount++;
                sendLine("ERROR NOT_READY");
                return;
            }
            handleCmd(cmd);
            break;

        default:
            gRelay.stats.parseErrorCount++;
            sendLine("ERROR UNKNOWN_COMMAND");
            break;
    }
}

bool handleHello(const ParsedCommand& cmd)
{
    (void)cmd;

    if (!isRelayReady())
    {
        sendLine("ERROR RELAY_NOT_READY");
        return false;
    }

    ClientSession& s = gRelay.session;
    s.handshakeComplete = true;
    s.state = SessionState::Ready;
    gRelay.stats.handshakeSuccessCount++;
    return sendLine("READY 1");
}

void handlePing()
{
    sendLine("PONG");
}

void handleBye()
{
    sendLine("OK");
    closeClientSession("client requested bye");
}

void handleCmd(const ParsedCommand& cmd)
{
    gRelay.stats.cmdCount++;

    if (cmd.cmd != 0x10)
    {
        gRelay.stats.parseErrorCount++;
        sendLine("ERROR UNKNOWN_COMMAND");
        return;
    }

    const char* validationError = validateIrCommand(cmd);
    if (validationError != nullptr)
    {
        gRelay.stats.parseErrorCount++;
        String response = String("ERROR ") + validationError;
        sendLine(response.c_str());
        return;
    }

    if (!transmitIrCommand(cmd.cmd, cmd.data, cmd.bits, cmd.beacon))
    {
        gRelay.stats.txFailureCount++;
        sendLine("ERROR TX_FAILED");
        return;
    }

    gRelay.stats.txCount++;
    sendLine("OK");
}

const char* validateIrCommand(const ParsedCommand& cmd)
{
    if (cmd.beacon > 1U)
    {
        return "BAD_BEACON";
    }

    const bool validBits = cmd.beacon == 1U
        ? (cmd.bits == 5U || cmd.bits == 9U)
        : (cmd.bits == 7U || cmd.bits == 8U || cmd.bits == 9U);
    if (!validBits)
    {
        return "BAD_BITS";
    }

    const unsigned int maxData = (1U << cmd.bits) - 1U;
    if (cmd.data > maxData)
    {
        return "BAD_DATA";
    }

    return nullptr;
}

// ── IR subsystem ──────────────────────────────────────────────────
bool transmitIrCommand(unsigned int cmd, unsigned int data, unsigned int bits, unsigned int beacon)
{
    (void)cmd;

    char type = 'T';
    bool isBeacon = (beacon != 0);

    if (isBeacon)
    {
        type = (bits == 9) ? 'E' : 'Z';
    }
    else
    {
        // Checksum signatures are 9-bit non-beacons with bit 8 set. They need
        // the checksum encoder's packet-ending gap; treating them as ordinary
        // packets prevents some taggers from recognizing a complete message.
        if (bits == 9)      type = ((data & 0x100U) != 0U) ? 'C' : 'P';
        else if (bits == 8) type = 'D';
        else                type = 'T';
    }

    if (!irTx.sendLttoIR(type, (int)data))
    {
        return false;
    }
    return true;
}

void irReceiveTick()
{
    if (!gRelay.irReady)
    {
        return;
    }

    const int irDataElementCount = sizeof(irDataBuf) / sizeof(irDataBuf[0]);
    int n = irRx.readIR(irDataBuf, irDataElementCount);
    if (n > 0)
    {
        processSignature();
    }
}

void processSignature()
{
    char msgType = irRx.readMessageType();
    uint16_t msgData = irRx.readRawDataPacket();

    static const struct
    {
        char type;
        uint8_t bits;
        bool beacon;
    } typeMap[] = {
        {'T', 7, false},
        {'Z', 5, true},
        {'E', 9, true},
        {'P', 9, false},
        {'D', 8, false},
        {'C', 9, false},
    };

    for (const auto& entry : typeMap)
    {
        if (msgType == entry.type)
        {
            gRelay.stats.rxCount++;

            if (gRelay.session.state == SessionState::Ready && gRelay.session.handshakeComplete)
            {
                char out[32];
                snprintf(out, sizeof(out), "RCV %X %X %d", msgData, entry.bits, entry.beacon ? 1 : 0);
                sendLine(out);
            }
            return;
        }
    }

    if (msgType != ' ')
    {
        gRelay.stats.rxUnknownCount++;

        if (gRelay.session.state == SessionState::Ready && gRelay.session.handshakeComplete)
        {
            char out[24];
            snprintf(out, sizeof(out), "RAW %c %X", msgType, msgData);
            sendLine(out);
        }
    }
}

// ── Bridge / diagnostics ──────────────────────────────────────────
void bridgeTick()
{
    // Reserved for future bridge-specific periodic work.
}

void printStatsIfDue()
{
    uint32_t now = millis();
    if (now - gRelay.lastStatsPrintMs < STATS_PRINT_INTERVAL_MS)
    {
        return;
    }

    gRelay.lastStatsPrintMs = now;

    Serial.printf(
        "STATE wifi=%s session=%s ready=%d | tcpC=%lu tcpD=%lu hsOK=%lu hsFail=%lu cmd=%lu parseErr=%lu lineOv=%lu tx=%lu txFail=%lu rx=%lu rxUnk=%lu tcpWrFail=%lu\n",
        wifiStateName(gRelay.wifiState),
        sessionStateName(gRelay.session.state),
        isRelayReady() ? 1 : 0,
        (unsigned long)gRelay.stats.tcpConnectCount,
        (unsigned long)gRelay.stats.tcpDisconnectCount,
        (unsigned long)gRelay.stats.handshakeSuccessCount,
        (unsigned long)gRelay.stats.handshakeFailureCount,
        (unsigned long)gRelay.stats.cmdCount,
        (unsigned long)gRelay.stats.parseErrorCount,
        (unsigned long)gRelay.stats.lineOverflowCount,
        (unsigned long)gRelay.stats.txCount,
        (unsigned long)gRelay.stats.txFailureCount,
        (unsigned long)gRelay.stats.rxCount,
        (unsigned long)gRelay.stats.rxUnknownCount,
        (unsigned long)gRelay.stats.tcpWriteFailureCount
    );
}

const char* wifiStateName(WifiState state)
{
    switch (state)
    {
        case WifiState::Starting:  return "Starting";
        case WifiState::Ready:     return "Ready";
        case WifiState::RetryWait: return "RetryWait";
        case WifiState::Fault:     return "Fault";
        default:                   return "Unknown";
    }
}

const char* sessionStateName(SessionState state)
{
    switch (state)
    {
        case SessionState::NoClient:          return "NoClient";
        case SessionState::HandshakePending:  return "HandshakePending";
        case SessionState::Ready:             return "Ready";
        case SessionState::Closing:           return "Closing";
        default:                              return "Unknown";
    }
}
