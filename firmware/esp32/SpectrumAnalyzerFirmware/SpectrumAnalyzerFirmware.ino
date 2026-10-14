#include <WiFi.h>
#include <WiFiUdp.h>
#include <driver/i2s.h>
#include <limits>
#include "config.h"

WiFiUDP udp;

// I2S pin configuration
constexpr int I2S_WS = 15;
constexpr int I2S_SD = 32;
constexpr int I2S_SCK = 14;
constexpr int BUTTON_PIN = 4;
constexpr i2s_port_t I2S_PORT = I2S_NUM_0;

// Audio configuration
constexpr int SAMPLE_RATE = 44100;
constexpr int BUFFER_SIZE = 256;
constexpr int MAX_LAG = 15;

// Button timing
constexpr unsigned long DEBOUNCE_DELAY_MS = 50;
constexpr unsigned long BUTTON_MUTE_MS = 400;

// UDP packet: 2-byte sample lag + 256 left samples + 256 right samples
struct __attribute__((packed)) AudioPacket {
    int16_t sampleLag;
    int16_t left[BUFFER_SIZE];
    int16_t right[BUFFER_SIZE];
};

static_assert(sizeof(AudioPacket) == 1026, "Unexpected AudioPacket size");

AudioPacket packet;
int32_t i2sRawBuffer[BUFFER_SIZE * 2];

// Button state
bool isStreaming = false;
int buttonState = HIGH;
int lastButtonReading = HIGH;
unsigned long lastDebounceTime = 0;
unsigned long muteStartedAt = 0;

void handleButton()
{
    int reading = digitalRead(BUTTON_PIN);

    if (reading != lastButtonReading) {
        lastDebounceTime = millis();
        lastButtonReading = reading;
    }

    if ((millis() - lastDebounceTime) > DEBOUNCE_DELAY_MS) {
        if (reading != buttonState) {
            buttonState = reading;

            // Ignore audio briefly to prevent the physical button click
            // from being captured by the microphones.
            muteStartedAt = millis();

            // Toggle streaming when the button is released.
            if (buttonState == HIGH) {
                isStreaming = !isStreaming;

                if (isStreaming) {
                    Serial.println("Audio streaming started.");
                }
                else {
                    Serial.println("Audio streaming stopped.");
                }
            }
        }
    }
}

bool readAudioFrame()
{
    size_t bytesRead = 0;

    esp_err_t result = i2s_read(
        I2S_PORT,
        i2sRawBuffer,
        sizeof(i2sRawBuffer),
        &bytesRead,
        portMAX_DELAY
    );

    return result == ESP_OK && bytesRead == sizeof(i2sRawBuffer);
}

int16_t clampToInt16(int32_t value)
{
    if (value > std::numeric_limits<int16_t>::max()) {
        return std::numeric_limits<int16_t>::max();
    }

    if (value < std::numeric_limits<int16_t>::min()) {
        return std::numeric_limits<int16_t>::min();
    }

    return static_cast<int16_t>(value);
}

void processAudioFrame()
{
    int64_t sumLeft = 0;
    int64_t sumRight = 0;

    // Convert interleaved 32-bit I2S samples to 16-bit stereo samples.
    for (int i = 0; i < BUFFER_SIZE; i++) {
        packet.left[i] = i2sRawBuffer[i * 2] >> 16;
        packet.right[i] = i2sRawBuffer[i * 2 + 1] >> 16;

        sumLeft += packet.left[i];
        sumRight += packet.right[i];
    }

    // Remove the DC component independently from both channels.
    int32_t meanLeft = static_cast<int32_t>(sumLeft / BUFFER_SIZE);
    int32_t meanRight = static_cast<int32_t>(sumRight / BUFFER_SIZE);

    for (int i = 0; i < BUFFER_SIZE; i++) {
        int32_t centeredLeft =
            static_cast<int32_t>(packet.left[i]) - meanLeft;

        int32_t centeredRight =
            static_cast<int32_t>(packet.right[i]) - meanRight;

        packet.left[i] = clampToInt16(centeredLeft);
        packet.right[i] = clampToInt16(centeredRight);
    }

    // Estimate the relative sample delay using cross-correlation.
    int bestLag = 0;
    int64_t maxCorrelation = std::numeric_limits<int64_t>::min();

    for (int lag = -MAX_LAG; lag <= MAX_LAG; lag++) {
        int64_t currentCorrelation = 0;

        for (int i = MAX_LAG; i < BUFFER_SIZE - MAX_LAG; i++) {
            currentCorrelation +=
                static_cast<int32_t>(packet.left[i]) *
                static_cast<int32_t>(packet.right[i + lag]);
        }

        if (currentCorrelation > maxCorrelation) {
            maxCorrelation = currentCorrelation;
            bestLag = lag;
        }
    }

    packet.sampleLag = bestLag;
}

bool sendAudioPacket()
{
    if (WiFi.status() != WL_CONNECTED) {
        return false;
    }

    if (!udp.beginPacket(PC_IP, UDP_PORT)) {
        return false;
    }

    size_t bytesWritten = udp.write(
        reinterpret_cast<uint8_t*>(&packet),
        sizeof(packet)
    );

    if (bytesWritten != sizeof(packet)) {
        return false;
    }

    return udp.endPacket() == 1;
}

void connectToWiFi()
{
    WiFi.onEvent([](WiFiEvent_t event, WiFiEventInfo_t info) {
        if (event == ARDUINO_EVENT_WIFI_STA_DISCONNECTED) {
            Serial.print("[Wi-Fi] Disconnected. Reason code: ");
            Serial.println(info.wifi_sta_disconnected.reason);
        }
    });

    // Reset the current Wi-Fi connection state before connecting.
    WiFi.mode(WIFI_STA);
    WiFi.disconnect(true);
    delay(1000);

    WiFi.setAutoReconnect(true);

    Serial.print("Connecting to Wi-Fi: ");
    Serial.print(WIFI_SSID);

    WiFi.begin(WIFI_SSID, WIFI_PASSWORD);

    while (WiFi.status() != WL_CONNECTED) {
        delay(1000);
        Serial.print(".");
    }

    Serial.print("\nWi-Fi connected. ESP32 IP address: ");
    Serial.println(WiFi.localIP());
}

bool initializeI2S()
{
    i2s_config_t i2sConfig = {
        .mode = static_cast<i2s_mode_t>(I2S_MODE_MASTER | I2S_MODE_RX),
        .sample_rate = SAMPLE_RATE,
        .bits_per_sample = I2S_BITS_PER_SAMPLE_32BIT,
        .channel_format = I2S_CHANNEL_FMT_RIGHT_LEFT,
        .communication_format = I2S_COMM_FORMAT_STAND_I2S,
        .intr_alloc_flags = ESP_INTR_FLAG_LEVEL1,
        .dma_buf_count = 4,
        .dma_buf_len = BUFFER_SIZE
    };

    i2s_pin_config_t pinConfig = {
        .bck_io_num = I2S_SCK,
        .ws_io_num = I2S_WS,
        .data_out_num = I2S_PIN_NO_CHANGE,
        .data_in_num = I2S_SD
    };

    esp_err_t driverResult =
        i2s_driver_install(I2S_PORT, &i2sConfig, 0, nullptr);

    if (driverResult != ESP_OK) {
        Serial.print("Failed to install I2S driver. Error code: ");
        Serial.println(driverResult);
        return false;
    }

    esp_err_t pinResult = i2s_set_pin(I2S_PORT, &pinConfig);

    if (pinResult != ESP_OK) {
        Serial.print("Failed to configure I2S pins. Error code: ");
        Serial.println(pinResult);

        i2s_driver_uninstall(I2S_PORT);
        return false;
    }

    return true;
}

void setup()
{
    Serial.begin(115200);

    // The push button is connected between GPIO 4 and GND.
    pinMode(BUTTON_PIN, INPUT_PULLUP);

    Serial.println("\n=== Spectrum Analyzer ESP32 Firmware ===");

    connectToWiFi();

    if (!initializeI2S()) {
        Serial.println("I2S initialization failed. System halted.");

        while (true) {
            delay(1000);
        }
    }

    Serial.println("System ready. Press the button to start audio streaming.");
}

void loop()
{
    handleButton();

    bool frameReceived = readAudioFrame();

    if (!frameReceived) {
        return;
    }

    if (!isStreaming ||
        (millis() - muteStartedAt) < BUTTON_MUTE_MS) {
        return;
    }

    processAudioFrame();
    sendAudioPacket();
}