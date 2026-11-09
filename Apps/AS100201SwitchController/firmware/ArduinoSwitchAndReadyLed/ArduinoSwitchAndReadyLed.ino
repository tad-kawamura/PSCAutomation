// AS100-201: USB Serial対応のArduino（UNO R4 / Nano R4など）
// 各ボタン: 給電ピン → 1 kΩ → LED → 入力ピンとスイッチの接続点 → スイッチ → GND
// LEDはスイッチを押している間だけ回路で点灯し、スケッチから個別には点滅させない。
// READY LEDにも直列抵抗を入れ、各GPIOの8 mA制限を守る。
//
// 版2（2026-11-09）: PC_go / CR_go の2回押し（ダブルクリック）をここで判定する。
//   1回目を離してから待ち時間（DOUBLE_WAIT_MS、PCから変更可）以内に同じボタンを押したら2回押し。
//   - 1回押し:  離してから待ち時間が過ぎた時点で "PC_go" / "CR_go" を送る
//   - 2回押し:  2回目を押した瞬間（チャタリング確定後）に "PC_go_2" / "CR_go_2" を送る
//   - 2回押しの後に続けて押した分: "PC_go_EXTRA" / "CR_go_EXTRA"（PCはログだけ）
//   - 待ち中に別のボタンを押したら、待っている1回押しを先に送る（押した順に届く）
//   F8 / sw4 は2回押しなし。今までどおり押した瞬間に送る。
//   アプリとスケッチは一緒に更新する（確認応答 AS100201_READY:2 の版で見分ける）。
const byte READY_LED_PIN = 1;
// D1 = app_on
const byte BUTTON_COUNT = 4;
const byte LED_SUPPLY_PINS[BUTTON_COUNT] = {2, 4, 6, 8};
const byte BUTTON_INPUT_PINS[BUTTON_COUNT] = {3, 5, 7, 9};
// D2/D3=F8, D4/D5=sw4, D6/D7=CR_go, D8/D9=PC_go
const char* const BUTTON_NAMES[BUTTON_COUNT] = {"F8", "sw4", "CR_go", "PC_go"};
// 2回押しを判定するボタン（F8 / sw4 は押した瞬間に送る）
const bool DOUBLE_ENABLED[BUTTON_COUNT] = {false, false, true, true};

const unsigned long DEBOUNCE_MS = 30;
const unsigned long DOUBLE_WAIT_MIN_MS = 50;
const unsigned long DOUBLE_WAIT_MAX_MS = 1000;
unsigned long doubleWaitMs = 250;   // 起動時の値。PCが接続確認の後に送り直す。

// 2回押しの判定の状態
enum PressState : byte {
  IDLE,          // 何もしていない
  FIRST_HELD,    // 1回目を押している（まだ何も送っていない）
  WAIT_SECOND,   // 1回目を離した。待ち時間以内に押されたら2回押し
  BURST_HELD,    // 2回押し（またはその後の押下）を押している
  BURST_WAIT,    // 2回押しの後に離した。待ち時間以内の押下は EXTRA
  DONE_HELD      // 押したまま別のボタンで1回押しを送った。離したら IDLE
};

// INPUT_PULLUP: 離す=HIGH、押す=LOW。
// 生の読み値が30 ms連続して変わらなければ確定。
byte lastSample[BUTTON_COUNT] = {HIGH, HIGH, HIGH, HIGH};
byte stableState[BUTTON_COUNT] = {HIGH, HIGH, HIGH, HIGH};
unsigned long changedAt[BUTTON_COUNT] = {};
PressState pressState[BUTTON_COUNT] = {IDLE, IDLE, IDLE, IDLE};
unsigned long releasedAt[BUTTON_COUNT] = {};   // 離した時刻（生の読み値が変わった時刻）
char command[24];
byte commandLength = 0;
bool discardCommand = false;
unsigned long lastFrameAt = 0;
unsigned long frameSequence = 0;

void sendButton(byte i, const char* suffix) {
  if (!Serial) return;
  Serial.print(BUTTON_NAMES[i]);
  Serial.println(suffix);
}

// 別のボタンが押されたとき、待っている1回押しを先に送り、続けての押下の扱いを終える。
void settleOthers(byte pressed) {
  for (byte j = 0; j < BUTTON_COUNT; ++j) {
    if (j == pressed) continue;
    switch (pressState[j]) {
      case FIRST_HELD:  sendButton(j, ""); pressState[j] = DONE_HELD; break;
      case WAIT_SECOND: sendButton(j, ""); pressState[j] = IDLE; break;
      case BURST_HELD:  pressState[j] = DONE_HELD; break;
      case BURST_WAIT:  pressState[j] = IDLE; break;
      default: break;
    }
  }
}

void onPress(byte i, unsigned long pressedAt) {
  settleOthers(i);
  if (!DOUBLE_ENABLED[i]) {
    sendButton(i, "");
    return;
  }
  switch (pressState[i]) {
    case WAIT_SECOND:
      if (pressedAt - releasedAt[i] < doubleWaitMs) {
        sendButton(i, "_2");
        pressState[i] = BURST_HELD;
      } else {
        // 待ち時間を過ぎていたが、まだ1回押しを送っていなかった（ループの遅れ）。
        sendButton(i, "");
        pressState[i] = FIRST_HELD;
      }
      break;
    case BURST_WAIT:
      if (pressedAt - releasedAt[i] < doubleWaitMs) {
        sendButton(i, "_EXTRA");
        pressState[i] = BURST_HELD;
      } else {
        pressState[i] = FIRST_HELD;
      }
      break;
    default:
      pressState[i] = FIRST_HELD;
      break;
  }
}

void onRelease(byte i, unsigned long at) {
  if (!DOUBLE_ENABLED[i]) return;
  releasedAt[i] = at;
  switch (pressState[i]) {
    case FIRST_HELD: pressState[i] = WAIT_SECOND; break;
    case BURST_HELD: pressState[i] = BURST_WAIT; break;
    default:         pressState[i] = IDLE; break;
  }
}

// 離してから待ち時間が過ぎたら、1回押しを送る（続けての押下の扱いも終える）。
// 押し始め（チャタリング確定前）のときは、確定を待ってから判定する。
void checkTimeouts(unsigned long now) {
  for (byte i = 0; i < BUTTON_COUNT; ++i) {
    if (!DOUBLE_ENABLED[i] || lastSample[i] != HIGH) continue;
    if (now - releasedAt[i] < doubleWaitMs) continue;
    if (pressState[i] == WAIT_SECOND) {
      sendButton(i, "");
      pressState[i] = IDLE;
    } else if (pressState[i] == BURST_WAIT) {
      pressState[i] = IDLE;
    }
  }
}

void handleCommand(unsigned long now, unsigned long& lastReadyOn) {
  if (strcmp(command, "AS100201_PING") == 0) Serial.println("AS100201_READY:2");
  else if (strcmp(command, "READY_ON") == 0) {
    digitalWrite(READY_LED_PIN, HIGH);
    lastReadyOn = now;
  }
  else if (strcmp(command, "READY_OFF") == 0) digitalWrite(READY_LED_PIN, LOW);
  else if (strncmp(command, "DOUBLE_WAIT_MS:", 15) == 0) {
    char* end = nullptr;
    long value = strtol(command + 15, &end, 10);
    // 範囲外・数字でないときは受け付けず、今の値を返す。
    if (end != command + 15 && *end == '\0' &&
        value >= (long)DOUBLE_WAIT_MIN_MS && value <= (long)DOUBLE_WAIT_MAX_MS)
      doubleWaitMs = (unsigned long)value;
    Serial.print("DOUBLE_WAIT_MS_OK:");
    Serial.println(doubleWaitMs);
  }
}

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
      handleCommand(now, lastReadyOn);
      commandLength = 0;
    } else if (c != '\r') {
      if (commandLength < sizeof(command) - 1) command[commandLength++] = c;
      else { commandLength = 0; discardCommand = true; }
    }
  }

  for (byte i = 0; i < BUTTON_COUNT; ++i) {
    // BUTTON_INPUT_PINSはINPUT_PULLUPで: 離す=HIGH、押す=LOW。
    byte sample = digitalRead(BUTTON_INPUT_PINS[i]);

    // 生の読み値が変わったら、その時刻を changedAt に記録する。
    if (sample != lastSample[i]) changedAt[i] = now;
    lastSample[i] = sample;

    // 30 ms 変わらなければ確定。押した・離した時刻は生の読み値が変わった時刻（changedAt）で数える。
    if (now - changedAt[i] >= DEBOUNCE_MS && sample != stableState[i]) {
      stableState[i] = sample;
      if (sample == LOW) onPress(i, changedAt[i]);
      else onRelease(i, changedAt[i]);
    }
  }

  checkTimeouts(now);
}
