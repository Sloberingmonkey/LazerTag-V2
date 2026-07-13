
/* Copyright (c) 2018 Richie Mickan. All Rights Reserved.
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, version 3.
 *
 * This program is distributed in the hope that it will be useful, but
 * WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
 * General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>. *
 *
 * Based on the Code from Neil Kolban: https://github.com/nkolban/esp32-snippets/blob/master/hardware/infra_red/receiver/rmt_receiver.c
 * Based on the Code from pcbreflux: https://github.com/pcbreflux/espressif/blob/master/esp32/arduino/sketchbook/ESP32_IR_Remote/ir_demo/ir_demo.ino
 * Based on the Code from Xplorer001: https://github.com/ExploreEmbedded/ESP32_RMT
 * Based on the Code from Darryl Scott: https://github.com/Darryl-Scott/ESP32-RMT-Library-IR-code-RAW
 */

/* This library uses the RMT hardware in the ESP32 to send/receive LTTO infrared data.
 * It uses a single channel of the RMT, therefore up to 8 instances can be used in a single sketch.
 */

#include "Arduino.h"
#ifdef __cplusplus
extern "C" {
#endif

#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "freertos/event_groups.h"
#include "freertos/semphr.h"
#include "freertos/ringbuf.h"



#ifdef __cplusplus
}
#endif

#include "ESP32_IR_LTTO.h"

////////////////////////////////////
#define         DEBUG       false
////////////////////////////////////

#define PRE_SYNC_MARK        3000
#define PRE_SYNC_SPACE       6000
#define BEACON_HEADER        6000
#define TAG_PACKET_HEADER    3000
#define MARK_SPACE           2000
#define ZERO_BIT             1000
#define ONE_BIT              2000
#define INTERPACKET_DEFAULT 25000
#define INTERPACKET_TAG     56000
#define INTERPACKET_CSUM    80000
#define VARIATION             .20   // 20%

#define BEACON_BIT_COUNT            5
#define LTAR_BEACON_BIT_COUNT       9
#define TAG_BIT_COUNT               7
#define PACKET_BIT_COUNT            9
#define DATA_BIT_COUNT              8
#define CHECKSUM_BIT_COUNT          9
#define CHECKSUM_BIT_SET            256


#define ROUND_TO                1   //50          //rounding value for microseconds timings
#define MARK_EXCESS             0   //100         //tweeked to get the right timing
#define SPACE_EXCESS            0   //50          //tweeked to get the right timing
#define TIMEOUT_US              5   //50          //RMT receiver timeout value(us)
#define MIN_CODE_LENGTH         5                 //Minimum data pulses received for a valid packet

#define PACKET                  'P'
#define DATA                    'D'
#define CHECKSUM                'C'
#define TAG                     'T'
#define BEACON                  'Z'
#define LTAR_BEACON             'E'
#define BCD                     true
#define LTAR                    true


// Clock divisor (base clock is 80MHz)
#define CLK_DIV                 80

// With CLK_DIV=80, one RMT tick is one microsecond. Each legacy RMT
// duration field is only 15 bits, so a single field cannot represent the
// 40,000us half used by the LTTO checksum gap. Split long low periods across
// as many RMT items as necessary instead of silently truncating the duration.
static_assert(CLK_DIV == 80, "LTTO timing requires one RMT tick per microsecond");
static constexpr uint32_t RMT_DURATION_MAX_US = 0x7FFFU;

static constexpr int rmtGapPhaseCount(uint32_t gapUs)
{
    const int requiredPhases = (int)((gapUs + RMT_DURATION_MAX_US - 1U) / RMT_DURATION_MAX_US);
    return (requiredPhases & 1) == 0 ? requiredPhases : requiredPhases + 1;
}

static constexpr int rmtGapItemCount(uint32_t gapUs)
{
    return rmtGapPhaseCount(gapUs) / 2;
}

static_assert(rmtGapPhaseCount(INTERPACKET_DEFAULT) == 2, "default gap must use two RMT phases");
static_assert(rmtGapPhaseCount(INTERPACKET_TAG) == 2, "tag gap must use two RMT phases");
static_assert(rmtGapPhaseCount(INTERPACKET_CSUM) == 4, "checksum gap must use four RMT phases");

// Number of clock ticks that represent 10us.  10 us = 1/100th msec.
#define TICK_10_US              (80000000 / CLK_DIV / 100000) // = 10

static RingbufHandle_t          ringBuf;

//////////////////////////////////////////////////////////////////////////////////////////
//////////////////////////////////////////////////////////////////////////////////////////
//////////////////////////////////////////////////////////////////////////////////////////

ESP32_IR::ESP32_IR()
{
    usedItemCount = 0;
    arrayIndex = 0;
    if(DEBUG)   Serial.print("ESP32_IR::Constructing");
}

//////////////////////////////////////////////////////////////////////////////////////////

bool ESP32_IR::ESP32_IRrxPIN(int _rxPin, int _channel)
{
    bool _status = true;
    
    if (_rxPin >= GPIO_NUM_0 && _rxPin < GPIO_NUM_MAX)      gpioNum = _rxPin;
    else                                                    _status = false;
    
    if (_channel >= RMT_CHANNEL_0 && _channel < RMT_CHANNEL_MAX)  rmtPort = _channel;
    else                                                    _status = false;
    
    if(_status == false && DEBUG) Serial.println("ESP32_IR::Rx Pin init failed");
    return _status;
}

//////////////////////////////////////////////////////////////////////////////////////////

bool ESP32_IR::ESP32_IRtxPIN(int _txPin, int _channel)
{
    bool _status = true;
    
    if (_txPin >= GPIO_NUM_0 && _txPin < GPIO_NUM_MAX)      gpioNum = _txPin;
    else                                                    _status = false;
    
    if (_channel >= RMT_CHANNEL_0 && _channel < RMT_CHANNEL_MAX)  rmtPort = _channel;
    else                                                    _status = false;
    
    if(_status == false && DEBUG) Serial.println("ESP32_IR::Tx Pin init failed");
    return _status;
}

//////////////////////////////////////////////////////////////////////////////////////////

bool ESP32_IR::initReceive()
{
    ringBuf = nullptr;
    rmt_config_t config = {};
    config.rmt_mode = RMT_MODE_RX;
    config.channel = (rmt_channel_t)rmtPort;
    config.gpio_num = (gpio_num_t)gpioNum;
    gpio_pullup_en((gpio_num_t)gpioNum);
    config.mem_block_num = 1; //how many memory blocks 64 x N (0-7)
    config.rx_config.filter_en = 1;
    config.rx_config.filter_ticks_thresh = 100; // 80000000/100 -> 800000 / 100 = 8000  = 125us
    config.rx_config.idle_threshold = TICK_10_US * 100 * 8;      // 8mS
    config.clk_div = CLK_DIV;
    if (rmt_config(&config) != ESP_OK) return false;
    if (rmt_driver_install(config.channel, 1000, 0) != ESP_OK) return false;
    if (rmt_get_ringbuf_handle(config.channel, &ringBuf) != ESP_OK || ringBuf == nullptr)
    {
        rmt_driver_uninstall(config.channel);
        ringBuf = nullptr;
        return false;
    }
    if (rmt_rx_start(config.channel, true) != ESP_OK)
    {
        rmt_driver_uninstall(config.channel);
        ringBuf = nullptr;
        return false;
    }
    return true;
}

//////////////////////////////////////////////////////////////////////////////////////////

bool ESP32_IR::initTransmit()
{
  rmt_config_t config = {};
  config.channel = (rmt_channel_t)rmtPort;
  config.gpio_num = (gpio_num_t)gpioNum;
  config.mem_block_num = 1;//how many memory blocks 64 x N (0-7)
  config.clk_div = CLK_DIV;
  config.tx_config.loop_en = false;
  config.tx_config.carrier_duty_percent = 50;
  config.tx_config.carrier_freq_hz = 38000;
  config.tx_config.carrier_level = (rmt_carrier_level_t)1;
  config.tx_config.carrier_en = 1;
  config.tx_config.idle_level = (rmt_idle_level_t)0;
  config.tx_config.idle_output_en = true;
  config.rmt_mode = RMT_MODE_TX;
  if (rmt_config(&config) != ESP_OK) return false;
  if (rmt_driver_install(config.channel, 0, 0) != ESP_OK) return false;
  return true;
}

//////////////////////////////////////////////////////////////////////////////////////////

void ESP32_IR::sendIR(rmt_item32_t data[], int IRlength, bool waitTilDone)
{
    // Compatibility wrapper. IRlength is an item count, not a byte count.
    sendItems(data, IRlength, waitTilDone);
}

//////////////////////////////////////////////////////////////////////////////////////////

bool ESP32_IR::sendItems(const rmt_item32_t* items, int itemCount, bool waitTilDone)
{
    if(DEBUG)   Serial.println("ESP32_IR::sendItems()");
    if(items == nullptr || itemCount <= 0) return false;

    // rmt_write_items() does not copy its input. Always use its blocking mode so
    // a following command cannot overwrite irDataArray while the RMT interrupt
    // is still consuming it. The argument remains in the API for compatibility.
    (void)waitTilDone;
    const rmt_channel_t channel = (rmt_channel_t)rmtPort;
    esp_err_t err = rmt_write_items(channel, items, itemCount, true);
    if (err != ESP_OK)
    {
        if(DEBUG) Serial.println("ESP32_IR::sendItems() failed.");
        return false;
    }

    return true;
}

//////////////////////////////////////////////////////////////////////////////////////////

bool ESP32_IR::sendCurrentBuffer(bool waitTilDone)
{
    return sendItems(irDataArray, usedItemCount, waitTilDone);
}

//////////////////////////////////////////////////////////////////////////////////////////

int ESP32_IR::currentBufferItemCount() const
{
    return usedItemCount;
}

//////////////////////////////////////////////////////////////////////////////////////////

void ESP32_IR::clearTransmitBuffer()
{
    clearIRdataArray();
}

//////////////////////////////////////////////////////////////////////////////////////////

bool ESP32_IR::appendLttoPacket(char _type, uint16_t _data)
{
    return encodeLTTO(_type, _data) > 0;
}

//////////////////////////////////////////////////////////////////////////////////////////

bool ESP32_IR::sendLttoIR(String _fullDataString)
{
    int _delimiterPosition = 0;
    bool _encodedAnyPacket = false;

    clearTransmitBuffer();
    _fullDataString.trim();

    while(_fullDataString.length() > 0)
    {
        char _packetType = _fullDataString.charAt(0);
        _fullDataString.remove(0, 1);

        _delimiterPosition = _fullDataString.indexOf(":");
        if (_delimiterPosition < 0)
        {
            return false;
        }

        uint16_t _data = _fullDataString.substring(0,_delimiterPosition).toInt();
        _fullDataString.remove(0, _delimiterPosition + 1);

        if (!appendLttoPacket(_packetType, _data))
        {
            return false;
        }
        _encodedAnyPacket = true;
    }

    return _encodedAnyPacket && sendCurrentBuffer(true);
}

//////////////////////////////////////////////////////////////////////////////////////////

bool ESP32_IR::sendLttoIR(char _type, int _data)
{
    if(DEBUG)
    {
        Serial.print("	ESP32_IR::sendLTTOtoIR(Type,Data) - ");
        Serial.print(_type);Serial.print("	");
        Serial.print(_data);
    }

    clearTransmitBuffer();
    if (!appendLttoPacket(_type, _data)) return false;
    return sendCurrentBuffer(true);
}

//////////////////////////////////////////////////////////////////////////////////////////

void    ESP32_IR::sendBrxTest()
{
    const uint16_t    BRX_START     = 2000;
    const uint16_t    BRX_SPACE     =  500;
    const uint16_t    BRX_ONE       = 1000;
    const uint16_t    BRX_ZERO      =  500;
    
    clearTransmitBuffer();
    
    //Create MAB
    irDataArray[0].duration0 = BRX_START;
    irDataArray[0].level0    = 1;
    irDataArray[0].duration1 = BRX_SPACE;
    irDataArray[0].level1    = 0;
    
    for(int index = 1; index < 25; index++)
    {
        static bool oddEven = true;
        if (oddEven) irDataArray[index].duration0 = BRX_ONE;
        else         irDataArray[index].duration0 = BRX_ZERO;
        oddEven = !oddEven ;
        irDataArray[index].duration0 = BRX_ONE;
        irDataArray[index].level0    = 1;
        irDataArray[index].duration1 = BRX_SPACE;
        irDataArray[index].level1    = 0;
    }
    irDataArray[25].duration0 = BRX_ZERO;
    irDataArray[25].level0    = 1;
    irDataArray[25].duration1 = BRX_SPACE;
    irDataArray[25].level1    = 0;

    usedItemCount = 26;
    sendCurrentBuffer();
    Serial.println("\n----------\nBrx sent\n----------");
}


//////////////////////////////////////////////////////////////////////////////////////////


void ESP32_IR::clearIRdataArray()
{
    memset(irDataArray, 0, sizeof(irDataArray));
    arrayIndex = 0;
    usedItemCount = 0;
}

//////////////////////////////////////////////////////////////////////////////////////////
//////////////////////////////////////////////////////////////////////////////////////////
//////////////////////////////////////////////////////////////////////////////////////////

int ESP32_IR::encodeLTTO(char _type, uint16_t _data)
{
    int _startIndex = arrayIndex;
    int             _syncHeader         = 0;
    int             _bitCount           = 0;
    int             _endOfPacketDelay   = 0;
    bool            _setEndOfPacket     = false;
    static int      _totalMessageTime   = 0;
    
    //calculate the type an set parameters
    switch (_type)
    {
        case TAG:
            _syncHeader         = TAG_PACKET_HEADER;
            _bitCount           = TAG_BIT_COUNT;
            _endOfPacketDelay   = INTERPACKET_TAG;
            _setEndOfPacket     = true;
            break;
        case BEACON:
            _syncHeader         = BEACON_HEADER;
            _bitCount           = BEACON_BIT_COUNT;
            _endOfPacketDelay   = INTERPACKET_DEFAULT;
            _setEndOfPacket     = true;
            break;
        case LTAR_BEACON:
            _syncHeader         = BEACON_HEADER;
            _bitCount           = LTAR_BEACON_BIT_COUNT;
            _endOfPacketDelay   = INTERPACKET_DEFAULT;
            _setEndOfPacket     = true;
            break;
        case PACKET:
            _syncHeader         = TAG_PACKET_HEADER;
            _bitCount           = PACKET_BIT_COUNT;
            _endOfPacketDelay   = INTERPACKET_DEFAULT;
            break;
        case DATA:
            _syncHeader         = TAG_PACKET_HEADER;
            _bitCount           = DATA_BIT_COUNT;
            _endOfPacketDelay   = INTERPACKET_DEFAULT;
            break;
        case CHECKSUM:
            _syncHeader         = TAG_PACKET_HEADER;
            _bitCount           = CHECKSUM_BIT_COUNT;
            _endOfPacketDelay   = INTERPACKET_CSUM;
            _setEndOfPacket     = true;
            break;
        default:
            if(DEBUG)   Serial.println("ESP32_IR:: ERROR - No match for TYPE:");
            return 0;
    }

    // Pre-sync + header + data bits + one or more low-gap items are
    // transmitted. End-of-packet messages also write one zero sentinel, so
    // reserve space for it as well.
    const int _gapItemCount = rmtGapItemCount((uint32_t)_endOfPacketDelay);
    const int _itemsRequired = 2 + _bitCount + _gapItemCount + (_setEndOfPacket ? 1 : 0);
    if (arrayIndex < 0 || _itemsRequired > (ARRAY_SIZE - arrayIndex))
    {
        if(DEBUG) Serial.println("ESP32_IR:: ERROR - transmit buffer full");
        return 0;
    }

    // Update checksum state only after capacity validation succeeds.
    if (_type == PACKET)
    {
        calculatedCheckSum = _data;
        _totalMessageTime = 0;
    }
    else if (_type == DATA)
    {
        calculatedCheckSum += _data;
    }
    else if (_type == CHECKSUM)
    {
        calculatedCheckSum %= 256;       // CheckSum is the remainder of dividing by 256.
        calculatedCheckSum |= 256;       // Set the 9th MSB to indicate a checksum.
        _data = calculatedCheckSum;
    }
    
    if(DEBUG)
    {
        Serial.print("\tESP32_IR::encodeLTTO (String) - ");
        Serial.print(_type);Serial.print(" ");
        Serial.println(_data);
    }
    
    //Populate the array
    //PreSync
    irDataArray[arrayIndex].duration0 = PRE_SYNC_MARK;
    irDataArray[arrayIndex].level0    = 1;
    irDataArray[arrayIndex].duration1 = PRE_SYNC_SPACE;
    irDataArray[arrayIndex++].level1  = 0;
    
    _totalMessageTime += (PRE_SYNC_MARK + PRE_SYNC_SPACE);
    
    //Header
    irDataArray[arrayIndex].duration0 = _syncHeader;
    irDataArray[arrayIndex].level0    = 1;
    irDataArray[arrayIndex].duration1 = MARK_SPACE;
    irDataArray[arrayIndex++].level1  = 0;
    
    _totalMessageTime += (_syncHeader + MARK_SPACE);
    
    //Data
    int _dataPulse = 0;
    _bitCount--;
    
    while (_bitCount >= 0)
    {
        _dataPulse = (bitRead(_data, _bitCount)+1) * 1000;            // the +1 is to convert 0/1 data into 1/2mS pulses.
        
        irDataArray[arrayIndex].duration0 = _dataPulse;
        irDataArray[arrayIndex].level0    = 1;
        irDataArray[arrayIndex].duration1 = MARK_SPACE;
        irDataArray[arrayIndex++].level1  = 0;
        
        _totalMessageTime += (_dataPulse + MARK_SPACE);

        _bitCount--;
    }
    
    // Set the end-of-signature quiet period. Both halves of every item stay at
    // carrier-off level 0. Spread the total evenly across an even number of
    // 15-bit phases, so every phase is positive and <= 32767us. The 80ms
    // checksum gap therefore becomes four 20ms phases in two consecutive low
    // RMT items instead of overflowing a 40ms field.
    const uint32_t _gapPhaseCount = (uint32_t)rmtGapPhaseCount((uint32_t)_endOfPacketDelay);
    const uint32_t _basePhaseDuration = (uint32_t)_endOfPacketDelay / _gapPhaseCount;
    const uint32_t _remainder = (uint32_t)_endOfPacketDelay % _gapPhaseCount;
    uint32_t _phaseIndex = 0U;

    for (int _gapItem = 0; _gapItem < _gapItemCount; ++_gapItem)
    {
        const uint32_t _firstDuration = _basePhaseDuration + (_phaseIndex++ < _remainder ? 1U : 0U);
        const uint32_t _secondDuration = _basePhaseDuration + (_phaseIndex++ < _remainder ? 1U : 0U);

        irDataArray[arrayIndex].duration0 = _firstDuration;
        irDataArray[arrayIndex].level0    = 0;
        irDataArray[arrayIndex].duration1 = _secondDuration;
        irDataArray[arrayIndex++].level1  = 0;
    }
    
    _totalMessageTime += _endOfPacketDelay;
    
    if(_setEndOfPacket)
    {
        //Serial.print("\tESP32_IR::encodeLTTO() - Packet Length = ");Serial.print(_totalMessageTime/1000.0);Serial.println("mS");
        irDataArray[arrayIndex].duration0 = 0;
        irDataArray[arrayIndex].level0    = 0;
        irDataArray[arrayIndex].duration1 = 0;
        irDataArray[arrayIndex].level1    = 0;
    }

    usedItemCount = arrayIndex;
    return arrayIndex - _startIndex;
}

//////////////////////////////////////////////////////////////////////////////////////////

void ESP32_IR::stopIR()
{
    rmt_config_t config = {};
    config.channel = (rmt_channel_t)rmtPort;
    rmt_rx_stop(config.channel);
    if(DEBUG)   Serial.print("ESP32_IR::Uninstalling..");
    if(DEBUG)
    {
        Serial.print("Port : ");
        Serial.println(config.channel);
    }
    rmt_driver_uninstall(config.channel);
}

//////////////////////////////////////////////////////////////////////////////////////////

bool ESP32_IR::irAvailabl()
{
//    bool returnValue = false;
//
//    RingbufHandle_t rb = NULL;
//    rmt_config_t config;
//    config.channel = (rmt_channel_t)rmtPort;
//    rmt_get_ringbuf_handle(config.channel, &rb);
//
//    while(rb)
//    {
//        size_t itemSize = 0;    //Size of ringBuffer data
//        rmt_item32_t *item = (rmt_item32_t*) xRingbufferReceive(rb, &itemSize, (TickType_t)TIMEOUT_US);
//        int numItems = itemSize / sizeof(rmt_item32_t);
//
//        if( numItems == 0)
//        {
//            returnValue = false;
//        }
//        else
//        {
//            memset(irDataRxArray, 0, sizeof(irDataRxArray));
//            decodeLTTO(item, numItems, irDataRxArray);
//            vRingbufferReturnItem(ringBuf, (void*) item);
//            returnValue = true;
//        }
//
//    }
//    //vTaskDelete(NULL);
//    return returnValue;
    return false;
}

//////////////////////////////////////////////////////////////////////////////////////////

int ESP32_IR::readIR(unsigned int *irDataRx, int maxElements)
{
    // Use the ring buffer handle cached at initReceive() time.
    // The original code called rmt_get_ringbuf_handle() on every loop()
    // iteration which is an unnecessary driver lookup — the handle never changes.
    if (!ringBuf || irDataRx == nullptr || maxElements <= 0) return 0;

    size_t itemSize = 0;
    rmt_item32_t *item = (rmt_item32_t*) xRingbufferReceive(ringBuf, &itemSize, (TickType_t)TIMEOUT_US);
    if (item == nullptr || itemSize < sizeof(rmt_item32_t)) return 0;

    const size_t numItems = itemSize / sizeof(rmt_item32_t);
    const size_t maxItemsForOutput = (size_t)maxElements / 2U;
    const size_t maxLttoItems = 2U + CHECKSUM_BIT_COUNT;
    int result = 0;

    // Each RMT item represents two timing values. Reject truncated/oversized
    // captures before decoding so neither output storage nor the bit accumulator
    // can overflow on malformed input.
    if (numItems >= MIN_CODE_LENGTH
        && numItems <= maxItemsForOutput
        && numItems <= maxLttoItems)
    {
        const size_t valueCount = numItems * 2U;
        memset(irDataRx, 0, valueCount * sizeof(unsigned int));
        const bool decoded = decodeLTTO(item, (int)numItems, irDataRx);
        result = (int)valueCount - 1;
        if (!decoded)
        {
            // Surface a bounded-but-invalid capture to the sketch as an
            // unknown frame. This keeps malformed IR off the TCP bridge while
            // making timing failures visible in rxUnk instead of looking like
            // a dead receiver.
            lttoMessage.type = 'V';
            lttoMessage.data = 0;
        }
    }
    else
    {
        lttoMessage.type = ' ';
        lttoMessage.data = 0;
    }

    vRingbufferReturnItem(ringBuf, (void*) item);
    return result;
}

//////////////////////////////////////////////////////////////////////////////////////////

void ESP32_IR::decodeRAW(rmt_item32_t *rawDataIn, int numItems, unsigned int *irDataOut)
{
    if(DEBUG)   Serial.print("ESP32_IR::Raw IR Code :");
    int _bitCount = 0;
    for (int index = 0; index < numItems; index++)
    {
        getDataIR(rawDataIn[index], irDataOut, _bitCount);
        _bitCount = _bitCount + 2;
    }
}

//////////////////////////////////////////////////////////////////////////////////////////

void ESP32_IR::getDataIR(rmt_item32_t item, unsigned int* irDataOut, int index) {
    unsigned int lowValue = (item.duration0) * (10 / TICK_10_US)-SPACE_EXCESS;
    lowValue = ROUND_TO * round((float)lowValue/ROUND_TO);
    //Serial.print(lowValue);Serial.print("L ,");
    irDataOut[index] = lowValue;
    unsigned int highValue = (item.duration1) * (10 / TICK_10_US)+MARK_EXCESS;
    highValue = ROUND_TO * round((float)highValue/ROUND_TO);
    //Serial.print(highValue);Serial.print("H ,");
    irDataOut[index+1] = highValue;
}

//////////////////////////////////////////////////////////////////////////////////////////

bool ESP32_IR::decodeLTTO(rmt_item32_t *rawDataIn, int numItems, unsigned int *irDataOut)
{
    //Serial.println("-----------------------");
    bool _validPreSync      = false;
    bool _validHeader       = false;
    bool _validDataPacket   = false;
    bool _badMarkSpace      = false;
    bool _badData           = false;
    
    //Clear the message data
    lttoMessage.type = ' ';
    lttoMessage.data = 0;
    
    /*  Pseudo Code
     -Create a structure char+unsigned int
     -Check for the PreSync 3+6
     -Get packet value
     -Set value (unsigned int)
     -Check packet length and value
     -Check for Header 3 or 6 - set type (char)
     return true if good data, or false if bad data
     */
    
    //Check for the Pre Sync pulses.
    if(checkData(rawDataIn, 0, 0, PRE_SYNC_MARK) && checkData(rawDataIn, 0, 1, PRE_SYNC_SPACE) )
        _validPreSync = true;
        //Serial.println("ESP32_IR:: PreSyncOK!");

    // Item 1 holds the header mark followed by the normal 2ms pause before
    // the first data bit. The old loop started at item 2 and never validated
    // this pause.
    if (!checkData(rawDataIn, 1, 1, MARK_SPACE))
    {
        _badMarkSpace = true;
    }
 
    int _bitCount = numItems-2;
    
    //Calculate the valus of the bits.
    int _totalOfBits = 0;
    for (int index = 2; index <= _bitCount+1; index++) //start at 2 to miss PreSync and Header.
    {
        if      (checkData(rawDataIn, index, 0, ONE_BIT))
        {
            //_totalOfBits = (_totalOfBits << 1) | 1;
            _totalOfBits = _totalOfBits << 1;
            _totalOfBits = _totalOfBits +1;
            //Serial.println("ESP32_IR:: Bit = ONE");
        }
        else if (checkData(rawDataIn, index, 0, ZERO_BIT))
        {
            _totalOfBits = _totalOfBits << 1;
            //Serial.println("ESP32_IR:: Bit = ZERO");
        }
        else
        {
            _badData = true;
        }
        //Serial.print("ESP32_IR:: Counting Data = "); Serial.println(_totalOfBits);
        
        // The final bit's normal 2ms space runs directly into the
        // inter-signature quiet period. RMT therefore reports that last space
        // at the receiver's 8ms idle threshold rather than as a 2ms phase.
        // Validate intermediate spaces strictly, but do not reject an
        // otherwise valid signature for its intentionally extended tail.
        const bool isLastDataItem = (index == _bitCount + 1);
        if (!isLastDataItem && !checkData(rawDataIn, index, 1, MARK_SPACE))
        {
            _badMarkSpace = true;
        }
    }
    lttoMessage.data = _totalOfBits;
    //Serial.print("ESP32_IR:: Data = "); Serial.println(lttoMessage.data);
    
    
    //Work out the packet type
    if      (checkData(rawDataIn, 1, 0, TAG_PACKET_HEADER))
    {
        _validHeader     = true;
        //Serial.println("ESP32_IR:: isTagPacketData");
        //Now work out if it is a Tag, Packet, Data or Checksum
        //Serial.print("ESP32_IR:: _bitCount = ");Serial.println(_bitCount);
        switch (_bitCount)
        {
            case TAG_BIT_COUNT:
                lttoMessage.type = TAG;
                break;
            case PACKET_BIT_COUNT: //CHECKSUM_BIT_COUNT
                if      (lttoMessage.data < CHECKSUM_BIT_SET)     lttoMessage.type = PACKET;
                else if (lttoMessage.data >= CHECKSUM_BIT_SET)    lttoMessage.type = CHECKSUM;
                break;
            case DATA_BIT_COUNT:
                lttoMessage.type = DATA;
                break;
            default:
                lttoMessage.type = 'V';  //Void
                break;
        }
        //Serial.print("ESP32_IR:: Type = ");Serial.println(lttoMessage.type);
    }
    else if (checkData(rawDataIn,1,0, BEACON_HEADER))
    {
        _validHeader  = true;
        //Serial.println("ESP32_IR:: isBeaconData");
        int _bitCount = numItems-2;
        //Serial.print("ESP32_IR:: _bitCount = ");Serial.println(_bitCount);
        switch (_bitCount)
        {
            case BEACON_BIT_COUNT:
                lttoMessage.type = BEACON;
                Serial.println("ESP32:: LTTO Beacon");
                break;
            case LTAR_BEACON_BIT_COUNT:
                lttoMessage.type = LTAR_BEACON;
                Serial.println("ESP32:: LTAR Beacon");
                break;
            default:
                lttoMessage.type = 'V';   //Void
                break;
        }
        //Serial.print("ESP32_IR:: Type = ");Serial.println(lttoMessage.type);
    }
    else
    {
        _validHeader = false;
    }
        
    //Check all sections are valid and return result.
        if(_validPreSync && _validHeader && !_badMarkSpace && !_badData) _validDataPacket = true;
        
        return _validDataPacket;
}

//////////////////////////////////////////////////////////////////////////////////////////

bool ESP32_IR::checkData(rmt_item32_t *rawDataIn, int _index, int _itemToCheck, unsigned int _expectedDuration)
{
    bool _result = false;
    
    if(_itemToCheck == 0)
    {
        if(rawDataIn[_index].duration0 >= (_expectedDuration - (_expectedDuration * VARIATION))
           &&
           rawDataIn[_index].duration0 <= (_expectedDuration + (_expectedDuration * VARIATION)) )
           {
               _result = true;
           }
    }
    else if(_itemToCheck == 1)
    {
        if(rawDataIn[_index].duration1 >= (_expectedDuration - (_expectedDuration * VARIATION))
           &&
           rawDataIn[_index].duration1 <= (_expectedDuration + (_expectedDuration * VARIATION)) )
           {
               _result = true;
           }
    }
    else _result = false;
           
    return _result;
}

//////////////////////////////////////////////////////////////////////////////////////////

char    ESP32_IR::readMessageType()
{
    return lttoMessage.type;
}

uint16_t ESP32_IR::readRawDataPacket()
{
    return lttoMessage.data;
}

////////////////////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////

//Calculating BCD and the CheckSum.
//This is here because it always does my head in !!!!!
//Certain Data is BCD Hex                               It means it is 2 x 4bit numbers in a single Byte
//                                                      Therefore 10 is actually 0001 0000 (which is 0x16)
//                                                      and       42 is actually 0100 0010 (which is 0x64)
//                                                      and       79 is actually 0111 1001 (which is 0x121)
//                                                      and      100 becomes     1111 1111 (which is 0xFF)
//This means that '10' is actually 0x16.
//However other data (taggerID, GameID, Flags, etc) are just plain Hex/Dec all the time.
//****** Now comes the nasty part!!!!! ******
//The LazerSwarm then needs all data sent as actual Hex, whereas the LTTO library expects it as Dec '10'.
//The CheckSum is the Sum of the final Hex values or Decimal values AFTER conversion (where applicable) to BCD.

//////////////////////////////////////////////////////////////////////////////////////////

int ESP32_IR::hostPlayerToGame(uint8_t _teamNumber, uint8_t _playerNumber,  uint8_t _gameType,
                               uint8_t _gameID,     uint8_t _gameLength,    uint8_t _health,
                               uint8_t _reloads,    uint8_t _shields,       uint8_t _megaTags,
                               uint8_t _flags1,     uint8_t _flags2,        int8_t _flags3)
{
    uint16_t        _taggerID               = -1;    // -1 means failed !
    bool            _isLtar                 = true;
    
    if(_flags3 == -1)   _isLtar = false;
    
    //callBack();

    if(DEBUG)   Serial.println("ESP32_IR - announcing game : ");

    //convert specific data packets to BCD
    if(_isLtar == false)
    {
        _gameLength = convertDecToBCD(_gameLength);
        _health     = convertDecToBCD(_health);
        _reloads    = convertDecToBCD(_reloads);
        _shields    = convertDecToBCD(_shields);
        _megaTags   = convertDecToBCD(_megaTags);
    }

    clearTransmitBuffer();

    encodeLTTO(PACKET,  _gameType);
    encodeLTTO(DATA,    _gameID);
    encodeLTTO(DATA,    _gameLength);
    encodeLTTO(DATA,    _health);
    encodeLTTO(DATA,    _reloads);
    encodeLTTO(DATA,    _shields);
    encodeLTTO(DATA,    _megaTags);
    encodeLTTO(DATA,    _flags1);
    encodeLTTO(DATA,    _flags2);
    if(_isLtar) encodeLTTO(DATA, _flags3);
    encodeLTTO(CHECKSUM);

    sendCurrentBuffer();
        
    //pseudo code
    //  if(cancelHosting) interval = infinite
    //  if(interval > millis() ) sendHostGame
    //  if(irReply.startsWith "P16" respond
    //    else if(irReply.startsWith "P17" respond

    return _taggerID;
}

//////////////////////////////////////////////////////////////////////////////////////////

void ESP32_IR::assignPlayer(uint8_t _gameID, uint8_t _taggerID, uint8_t _teamNumber, uint8_t _playerNumber, bool _isLtar)
{
    if(DEBUG)
    {
        Serial.print("ESP32_IR::assignPlayer() - Team = ");
        Serial.print(_teamNumber);
        Serial.print(", Player = ");
        Serial.println(_playerNumber);
    }
    
    uint8_t _teamAndPlayer = encodeTeamAndPlayer(_teamNumber, _playerNumber);
    
    clearTransmitBuffer();
    
    if(_isLtar) encodeLTTO(PACKET,  131);
    else        encodeLTTO(PACKET,    1);
    encodeLTTO(DATA,    _gameID);
    encodeLTTO(DATA,    _taggerID);
    encodeLTTO(DATA,    _teamAndPlayer);
    encodeLTTO(CHECKSUM);
    
    sendCurrentBuffer();
    
}

//////////////////////////////////////////////////////////////////////////////////////////

void ESP32_IR::assignPlayerFailed(uint8_t _gameID, uint8_t _taggerID, bool _isLtar)
{
    if(DEBUG)   Serial.print("ESP32_IR::assignPlayerFailed() - TaggerID: ");
    if(DEBUG)   Serial.println(_taggerID);
    
    clearTransmitBuffer();
    
    if(_isLtar) encodeLTTO(PACKET,  143);
    else        encodeLTTO(PACKET,   15);
    encodeLTTO(DATA,    _gameID);
    encodeLTTO(DATA,    _taggerID);
    encodeLTTO(CHECKSUM);
    
    sendCurrentBuffer();
}

//////////////////////////////////////////////////////////////////////////////////////////

void ESP32_IR::ltarAssignPlayerSuccess(uint8_t _gameID, uint8_t _teamNumber, uint8_t _playerNumber)
{
    if(DEBUG)   Serial.println("ESP32_IR::ltarAssignPlayerSuccess()");

    uint8_t _teamAndPlayer = encodeTeamAndPlayer(_teamNumber, _playerNumber);
    
    clearTransmitBuffer();
    
    encodeLTTO(PACKET,  135);
    encodeLTTO(DATA,    _gameID);
    encodeLTTO(DATA,    _teamAndPlayer);
    encodeLTTO(CHECKSUM);
    
    sendCurrentBuffer();
}

//////////////////////////////////////////////////////////////////////////////////////////

void ESP32_IR::requestTagReport(uint8_t _gameID, uint8_t _teamNumber, uint8_t _playerNumber, uint8_t _reportRequired)
{
    if(DEBUG)   Serial.println("ESP32_IR::requestTagReport()");
    
    clearTransmitBuffer();
    
    encodeLTTO(PACKET,  49);            //0x31
    encodeLTTO(DATA,    _gameID);
    encodeLTTO(DATA,    _teamNumber);
    encodeLTTO(DATA,    _playerNumber);
    encodeLTTO(DATA,    _reportRequired);
    encodeLTTO(CHECKSUM);
    
    sendCurrentBuffer();
}

////////////////////////////
////////////////////////////

//////////////////////////////////////////////////////////////////////////////////////////
//////////////////////////////////////////////////////////////////////////////////////////
//////////////////////////////////////////////////////////////////////////////////////////

void ESP32_IR::taggerRequestToJoin(uint8_t _gameID, uint8_t _taggerID, uint8_t _preferredTeam, bool _isLtar)
//NB in LTAR mode _preferredTeam is actually TaggerInformation (optional)
{
    if(DEBUG)   Serial.println("ESP32_IR::taggerRequestToJoin()");
    if(_isLtar) _preferredTeam = 0x1B;  //Fake up Firmware Version.
    
    clearTransmitBuffer();
    
    if(_isLtar) encodeLTTO(PACKET, 130);
    else        encodeLTTO(PACKET,  16);
    encodeLTTO(DATA,    _gameID);
    encodeLTTO(DATA,    _taggerID);
    encodeLTTO(DATA,    _preferredTeam);
    encodeLTTO(CHECKSUM);
    
    sendCurrentBuffer();
}

//////////////////////////////////////////////////////////////////////////////////////////

void ESP32_IR::taggerAckPlayerAssign(uint8_t _gameID, uint8_t _taggerID)
{
    if(DEBUG)   Serial.println("ESP32_IR::taggerAckPlayerAssign()");
    
    clearTransmitBuffer();
    
    encodeLTTO(PACKET,  17);
    encodeLTTO(DATA,    _gameID);
    encodeLTTO(DATA,    _taggerID);
    encodeLTTO(CHECKSUM);
    
    sendCurrentBuffer();
}

//////////////////////////////////////////////////////////////////////////////////////////

void ESP32_IR::taggerTagSummary(uint8_t _gameID,          uint8_t _teamAndPlayerNumber,
                                uint8_t _totalNumberTagsRx,
                                uint8_t _survivalMinutes, uint8_t _survivalSeconds,
                                uint8_t _zoneTimeMinutes, uint8_t _zoneTimeSeconds,
                                uint8_t _teamReportFlag)
{
    if(DEBUG)   Serial.println("ESP32_IR::taggerTagSummary()");
        
    clearTransmitBuffer();
    
    encodeLTTO(PACKET,  64);            //0x40
    encodeLTTO(DATA,    _gameID);
    encodeLTTO(DATA,    _teamAndPlayerNumber);
    encodeLTTO(DATA,    convertDecToBCD(_totalNumberTagsRx));
    encodeLTTO(DATA,    convertDecToBCD(_survivalMinutes));
    encodeLTTO(DATA,    convertDecToBCD(_survivalSeconds));
    encodeLTTO(DATA,    convertDecToBCD(_zoneTimeMinutes));
    encodeLTTO(DATA,    convertDecToBCD(_zoneTimeSeconds));
    encodeLTTO(DATA,    _teamReportFlag);
    encodeLTTO(CHECKSUM);
    
    sendCurrentBuffer();
}

//////////////////////////////////////////////////////////////////////////////////////////

void ESP32_IR::taggerTeamReport(uint8_t _teamToReport,     uint8_t _gameID,                uint8_t _teamAndPlayerNumber,
                                uint8_t _playersIncluded,  uint8_t _player1tags,           uint8_t _player2tags,
                                uint8_t _player3tags,      uint8_t _player4tags,           uint8_t _player5tags,
                                uint8_t _player6tags,      uint8_t _player7tags,           uint8_t _player8tags)
{
    if(DEBUG)   Serial.println("ESP32_IR::taggerTagSummary()");
    
    clearTransmitBuffer();
    
    switch(_teamToReport)
    {
        case 1:
            encodeLTTO(PACKET, 65 );      //0x41
            break;
        case 2:
            encodeLTTO(PACKET,  66);      //0x42
            break;
        case 3:
            encodeLTTO(PACKET,  67);      //0x43
            break;
    }
    encodeLTTO(DATA,    _gameID);
    encodeLTTO(DATA,    _teamAndPlayerNumber);
    encodeLTTO(DATA,    (_playersIncluded));
    if((_playersIncluded & 0b00000001U) != 0U) encodeLTTO(DATA, (_player1tags));
    if((_playersIncluded & 0b00000010U) != 0U) encodeLTTO(DATA, (_player2tags));
    if((_playersIncluded & 0b00000100U) != 0U) encodeLTTO(DATA, (_player3tags));
    if((_playersIncluded & 0b00001000U) != 0U) encodeLTTO(DATA, (_player4tags));
    if((_playersIncluded & 0b00010000U) != 0U) encodeLTTO(DATA, (_player5tags));
    if((_playersIncluded & 0b00100000U) != 0U) encodeLTTO(DATA, (_player6tags));
    if((_playersIncluded & 0b01000000U) != 0U) encodeLTTO(DATA, (_player7tags));
    if((_playersIncluded & 0b10000000U) != 0U) encodeLTTO(DATA, (_player8tags));
    encodeLTTO(CHECKSUM);
    
    sendCurrentBuffer();
}

//////////////////////////////////////////////////////////////////////////////////////////
//////////////////////////////////////////////////////////////////////////////////////////
//////////////////////////////////////////////////////////////////////////////////////////

                 
int ESP32_IR::encodeTeamAndPlayer(uint8_t _teamNumber, uint8_t _playerNumber)
{
    uint8_t _teamAndPlayer = 0;
    
    if(_teamNumber == 0)    //zero-based player number + 8
    {
        if(DEBUG)   Serial.println("ESP32_IR::encodeTeamAndPlayer() - Team = 0");
        _teamAndPlayer = _playerNumber + 7;
    }
    else
    {
        if(DEBUG)   Serial.print("ESP32_IR::encodeTeamAndPlayer() - Team = ");
        if(DEBUG)   Serial.println(_teamNumber);
        _teamAndPlayer = _teamNumber << 3;
        _teamAndPlayer += (_playerNumber -1);
    }
    if(DEBUG)   Serial.print("ESP32_IR::encodeTeamAndPlayer() = ");
    if(DEBUG)   Serial.println(_teamAndPlayer);
    return _teamAndPlayer;
}

//int ESP32_IR::decodeTeamAndPlayer(uint8_t _teamAndPlayerNumber)
//{
//    Serial.println("\n\n\n\t\tESP32_IR::decodeTeamAndPlayer() has no code yet !!!!\n\n\n");"
//    //TODO:
//}



//////////////////////////////////////////////////////////////////////////////////////////

int ESP32_IR::convertDecToBCD(int _dec)
{
    if (_dec == 100) return 0xFF;
    return (int) (((_dec/10) << 4) | (_dec %10) );
}

//////////////////////////////////////////////////////////////////////////////////////////

int ESP32_IR::convertBCDtoDec(int _bcd)
{
    if (_bcd == 0xFF) return _bcd;
    return (int) (((_bcd >> 4) & 0xF) *10) + (_bcd & 0xF);
}

//////////////////////////////////////////////////////////////////////////////////////////
//////////////////////////////////////////////////////////////////////////////////////////
//////////////////////////////////////////////////////////////////////////////////////////
//////////////////////////////////////////////////////////////////////////////////////////
//////////////////////////////////////////////////////////////////////////////////////////
//////////////////////////////////////////////////////////////////////////////////////////
//////////////////////////////////////////////////////////////////////////////////////////
