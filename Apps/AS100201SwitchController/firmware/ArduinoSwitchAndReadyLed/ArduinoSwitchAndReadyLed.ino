// AS100-201: USB Serial対応のArduino（UNO R4 / Nano R4など）
// 各ボタン: 給電ピン → 1 kΩ → LED → 入力ピンとスイッチの接続点 → スイッチ → GND
// LEDはスイッチを押している間だけ回路で点灯し、スケッチから個別には点滅させない。
// READY LEDにも直列抵抗を入れ、各GPIOの8 mA制限を守る。
const byte READY_LED_PIN = 1;
// D1 = app_on
const byte BUTTON_COUNT = 4;
const byte LED_SUPPLY_PINS[BUTTON_COUNT] = {2, 4, 6, 8};
const byte BUTTON_INPUT_PINS[BUTTON_COUNT] = {3, 5, 7, 9};
// D2/D3=F8, D4/D5=sw4, D6/D7=CR_go, D8/D9=PC_go
const char* const BUTTON_NAMES[BUTTON_COUNT] = {"F8", "sw4", "CR_go", "PC_go"};

const unsigned long DEBOUNCE_MS = 30;

// INPUT_PULLUP: 離す=HIGH、押す=LOW。
// 生の読み値が30 ms連続して変わらなければ確定。LOW確定時のみ1回送信する。
byte lastSample[BUTTON_COUNT] = {HIGH, HIGH, HIGH, HIGH};
byte stableState[BUTTON_COUNT] = {HIGH, HIGH, HIGH, HIGH};
unsigned long changedAt[BUTTON_COUNT] = {};
char command[24];
byte commandLength = 0;
bool discardCommand = false;
unsigned long lastFrameAt = 0;
unsigned long frameSequence = 0;

void setup() {
  digitalWrite(READY_LED_PIN, LOW);
  pinMode(READY_LED_PIN, OUTPUT);

  for (byte i = 0; i < BUTTON_COUNT; ++i) {
    digitalWrite(LED_SUPPLY_PINS[i], HIGH);
    pinMode(LED_SUPPLY_PINS[i], OUTPUT);
    pinMode(BUTTON_INPUT_PINS[i], INPUT_PULLUP);
  }

  // C# の ArduinoConnection.cs と同じ通信速度にする。
  Serial.begin(115200);
  if (Serial) Serial.println("DIAG_BOOT:1");
}

void loop() {
  unsigned long now = millis();
  // 無操作時も250ms間隔で返信。PCは受信停止と無操作を区別できる。
  // PCへ未接続の間は送信を溜めない。
  if (Serial && now - lastFrameAt >= 250) {
    lastFrameAt = now;
    Serial.print("DIAG_FRAME:");
    Serial.print(++frameSequence);
    Serial.print(':');
    Serial.println(now);
  }
  // PC が切断・終了した後の点灯保持を避けるため、READY_ON は定期的に再送する。
  static unsigned long lastReadyOn = 0;
  if (digitalRead(READY_LED_PIN) == HIGH && now - lastReadyOn > 3000)
    digitalWrite(READY_LED_PIN, LOW);

  while (Serial.available()) {
    char c = (char)Serial.read();   //シリアル通信を読み取る cに保管
    if (discardCommand) {
      if (c == '\n') discardCommand = false;
      continue;
    }
    if (c == '\n') {
      command[commandLength] = '\0';
      if (strcmp(command, "AS100201_PING") == 0) Serial.println("AS100201_READY:1");
      else if (strcmp(command, "READY_ON") == 0) {
        digitalWrite(READY_LED_PIN, HIGH);
        lastReadyOn = now;
      }
      else if (strcmp(command, "READY_OFF") == 0) digitalWrite(READY_LED_PIN, LOW);
      commandLength = 0;
    } else if (c != '\r') {
      if (commandLength < sizeof(command) - 1) command[commandLength++] = c;
      else { commandLength = 0; discardCommand = true; }
    }
  }

  for (byte i = 0; i < BUTTON_COUNT; ++i) {
    // BUTTON_INPUT_PINSはINPUT_PULLUPで: 離す=HIGH、押す=LOW。
    byte sample = digitalRead(BUTTON_INPUT_PINS[i]);
    
    // ボタン押されたらsample=LOWになる
    // なのでlastSampleと違う事になる、そしたらchangedAtに現在時刻を記録  
    // sampleがHIGHの時は特に何もしない
    if (sample != lastSample[i]) changedAt[i] = now;
    lastSample[i] = sample;       //sampleがLOWの時はlastSampleがLOWに更新される
    
    //さっきの時刻と今の時間の引き算が30msec以上でかつsample=LOWの時
    // stabelState=LOWに更新される。以後、押されっぱなしの状態だとこのif文発動しない
    if (now - changedAt[i] >= DEBOUNCE_MS && sample != stableState[i]) {
      stableState[i] = sample;
      // ボタン押してる時シリアル通信を行う
      if (sample == LOW && Serial) Serial.println(BUTTON_NAMES[i]);
    }
  }
}
