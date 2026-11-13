// AS100-201 学習モードのタッチパネル(M5Stack Tab5、ESP32-P4、5 インチ 1280×720)
// 仕様: specs/AS100201SwitchController/SPEC_調整操作の記録.md
//
// 版 1(2026-11-13): 横置き・仮レイアウト。実物で見て直す前提。
//   - 1 画面目: 着磁・減磁・ノズル P/R・スペーサー P/R の 6 操作と、完了・取消
//   - 2 画面目: 減磁(しっかり / ちょんちょん)、ノズル(程度を選んでから方向を押すと確定。程度は「普通」が選ばれた状態で開く)、
//     スペーサー(上げる / 下げる)。どれも「戻る」で 1 画面目に戻れる(何も送らない)
//   - 取消: 確認「直前の○○を取り消しますか」→ はい で UNDO を送る
//   - 2 画面目の選択と取消の確認はパネルの中で完結させ、確定した 1 行だけを PC に送る
//   - 2026-11-13 追記: 「PC と切れました」は USB の未接続が 1.5 秒続いたときだけ出す(一瞬の揺れで出ていたため)。
//
// 版 2(2026-11-13、SPEC_調整操作の記録.md「Wi-Fi・バッテリーへの変更」): USB と Wi-Fi の両方でつながる・電池・画面を暗くする
//   - Tab5 が Wi-Fi の親機になり(WIFI_SSID、PANEL_IP)、PANEL_PORT で PC からの TCP 接続を待つ。PC は専用の USB Wi-Fi 子機でつなぐ
//   - PC の確認(PING)が最後に届いた道(USB / Wi-Fi)を「今の道」にし、定期応答・操作の行・電池残量はその道にだけ送る
//     (同じ操作が 2 本で二重に記録されないため)。両方つながっていれば PC が USB を選ぶ。今の道が切れたら PC がもう一方へ移る
//   - 電池残量を画面の上に出し、30 秒ごとと PC の確認に答えた直後に BATT: を送る
//   - 60 秒触らなければ画面を暗くする。暗いときの最初のタッチは明るくするだけで、ボタンとしては受け付けない
//
// PC とのやり取り(LF の行単位。USB-C の内蔵 USB シリアル、または Wi-Fi の TCP。どちらも同じ行):
//   PC → パネル  AS100201_PANEL_PING           → パネルは AS100201_PANEL_READY:2 を返す(この道が今の道になる)
//   パネル → PC  DIAG_FRAME:<連番>:<millis>     250ms ごと(PC はこれが 1 秒止まると操作を受け付けない)
//   パネル → PC  DIAG_BOOT:1                    起動通知(USB)
//   パネル → PC  BATT:<残量%>:<電圧mV>:<充電中 0/1>  30 秒ごと(電池が無いときは残量 -1)
//   パネル → PC  ACT:MAG / ACT:DEMAG:FULL|TAP / ACT:NOZZLE:P|R:IN|OUT:1|2|3 / ACT:SPACER:P|R:UP|DOWN / DONE / UNDO
//   PC → パネル  ACK:<受け取った行> / NAK:<理由>(理由は日本語 UTF-8)
//   Arduino 向けの AS100201_PING や知らない行(STATE: など)は無視する(接続確認が COM ポートを総当たりで探すため)。
//
// Arduino IDE の設定(ツール メニュー):
//   ボード: M5Tab5(esp32 by Espressif Systems 3.3 系)
//   USB Mode: Hardware CDC and JTAG
//   USB CDC On Boot: Enabled          ← これが無いと USB-C から PC に行が届かない
//   ライブラリ: M5Unified / M5GFX(0.3.0 で確認)
//   書き込めないときは、リセットボタンを 2 秒ほど長押しして緑の LED が速く点滅する状態(書き込み待ち)にしてから書き込む。

#include <M5Unified.h>
#include <WiFi.h>
#include <lwip/sockets.h>

// ---- 取り決め ----------------------------------------------------------
static const char* const HELLO_REQUEST = "AS100201_PANEL_PING";
static const char* const HELLO_REPLY = "AS100201_PANEL_READY:2";   // 版 2。PC 側 As100201PanelProtocol.PanelVersion と揃える

// Wi-Fi(Tab5 が親機)。名前とパスワードはここにだけ書く(2026-11-13、AI の仮決め)。変えたら書き込み直し、
// PC の子機の Wi-Fi の登録もし直す。IP とポートは PC 側 As100201PanelNetworkSettings と揃える。
static const char* const WIFI_SSID = "AS100201_PANEL";
static const char* const WIFI_PASSWORD = "as100201-panel";   // 8 文字以上
const IPAddress PANEL_IP(192, 168, 4, 1);                    // 社内 LAN と同じ範囲なら PC 側と両方を変える
const uint16_t PANEL_PORT = 50201;

const unsigned long BATTERY_INTERVAL_MS = 30000;   // 電池残量を読んで送る間隔
const int LOW_BATTERY_LEVEL = 20;                  // これ以下で画面の残量を警告の色にする(PC のログと同じ値)
const unsigned long DIM_AFTER_MS = 60000;          // 触らなければ画面を暗くするまで(AI の仮決め。実物で決める)
const uint8_t DIM_BRIGHTNESS = 24;                 // 暗いときの明るさ(0〜255。状態は読める程度。実物で決める)
const unsigned long FRAME_INTERVAL_MS = 250;
const unsigned long REPLY_TIMEOUT_MS = 2000;   // 送ってから ACK / NAK が来ないと「返事なし」を出す
// USB の「つながっていない」がこの時間続いたときだけ「PC と切れました」にする。
// ESP32 の内蔵 USB シリアルの接続判定((bool)Serial)は、正常な接続でも一瞬 false になることがある
// (esp32 3.3.12 の HWCDC.cpp の注記)。1 回の false で切れたとすると、通信が続いているのに表示だけ切れる。
const unsigned long DISCONNECT_CONFIRM_MS = 1500;

// 横置きの向き。上下が逆に見えたら true にして書き込み直す。
const bool FLIP_LANDSCAPE = false;

// ---- 色 ----------------------------------------------------------------
uint16_t C_BG, C_HEADER, C_TEXT, C_SUBTEXT, C_OP, C_DONE, C_UNDO, C_BACK, C_SELECTED, C_UNSELECTED,
    C_DISABLED, C_DISABLED_TEXT, C_PRESSED, C_YES, C_MSG_INFO, C_MSG_OK, C_MSG_WARN, C_MSG_ERROR, C_MSG_WARN_TEXT;

// ---- 画面とボタン -------------------------------------------------------
enum Screen : uint8_t { HOME, DEMAG, NOZZLE, SPACER, UNDO_CONFIRM };

enum ButtonId : uint8_t {
  B_NONE, B_MAG, B_DEMAG, B_NOZ_P, B_NOZ_R, B_SPC_P, B_SPC_R, B_DONE, B_UNDO,
  B_BACK, B_FULL, B_TAP, B_DEG1, B_DEG2, B_DEG3, B_IN, B_OUT, B_UP, B_DOWN, B_YES, B_NO
};

struct Button {
  int16_t x, y, w, h;
  ButtonId id;
  const char* label;   // 2 行にするときは '\n' で区切る
  uint16_t color;
  bool selected;       // 程度の選択中の表示
};

const int MAX_BUTTONS = 12;
Button buttons[MAX_BUTTONS];
int buttonCount = 0;

Screen screen = HOME;
char side = 'P';     // ノズル・スペーサーの P / R
char degree = '2';   // ノズルの程度 1=微調整 2=普通 3=大きく

// ---- 状態 ---------------------------------------------------------------
// PC への道。PC の確認(PING)が最後に届いた道が「今の道」(なければ PATH_NONE)。
enum Path : uint8_t { PATH_NONE, PATH_USB, PATH_WIFI };
Path currentPath = PATH_NONE;

bool usbConnected = false;    // PC が USB シリアルを開いている(一瞬の揺れは無視した値)
unsigned long lastUsbSeenAt = 0;  // 最後に USB がつながっていると見えた時刻(受信があったときも含む)
bool lostShown = false;       // 「PC と切れました」を出したあと、まだつながり直していない

bool wifiStarted = false;     // Wi-Fi の親機を始められた
WiFiServer server(PANEL_PORT);
WiFiClient wifiClient;        // PC からの TCP 接続(1 本だけ。新しい接続が来たら古い方を閉じる)

int batteryLevel = -1;        // 残量 %(電池なしは -1)
int batteryMillivolts = 0;
bool batteryCharging = false;
unsigned long lastBatteryAt = 0;

uint8_t normalBrightness = 160;
bool dimmed = false;
unsigned long lastTouchAt = 0;

String pendingLine;           // 送って返事を待っている行(空なら待っていない)
unsigned long pendingAt = 0;
String lastOperation;         // 最後に記録された操作(ACT: の行)。取消の確認に出す
ButtonId pressedId = B_NONE;

enum MessageKind : uint8_t { MSG_INFO, MSG_OK, MSG_WARN, MSG_ERROR };
MessageKind messageKind = MSG_INFO;
String messageTitle = "起動しました";
String messageDetail = "PC のアプリで「学習モード」を選ぶと、つながります";

bool dirty = true;
unsigned long frameSequence = 0;
unsigned long lastFrameAt = 0;

// 道ごとの受信途中の行。
struct LineReader {
  char line[256];
  size_t length = 0;
  bool overflow = false;
};
LineReader usbReader, wifiReader;

// ---- 文言 ---------------------------------------------------------------
// PC の台帳・ログと同じ言葉で、送った行を人に見せる名前にする。
String describe(const String& line) {
  if (line == "DONE") return "完了";
  if (line == "UNDO") return "取消";
  if (line == "ACT:MAG") return "着磁";
  if (line == "ACT:DEMAG:FULL") return "減磁（しっかり）";
  if (line == "ACT:DEMAG:TAP") return "減磁（ちょんちょん）";
  if (line.startsWith("ACT:NOZZLE:")) {
    // ACT:NOZZLE:<P|R>:<IN|OUT>:<1|2|3>
    char s = line.charAt(11);
    bool in = line.indexOf(":IN:") > 0;
    char d = line.charAt(line.length() - 1);
    const char* deg = d == '1' ? "微調整" : d == '3' ? "大きく" : "普通";
    return String("ノズル ") + s + " 側（" + (in ? "押し込む" : "外に出す") + "・" + deg + "）";
  }
  if (line.startsWith("ACT:SPACER:")) {
    char s = line.charAt(11);
    bool up = line.endsWith(":UP");
    return String("スペーサー ") + s + " 側（" + (up ? "上げる" : "下げる") + "）";
  }
  return line;
}

void setMessage(MessageKind kind, const String& title, const String& detail) {
  messageKind = kind;
  messageTitle = title;
  messageDetail = detail;
  dirty = true;
}

const char* pathName(Path path) {
  return path == PATH_USB ? "USB" : path == PATH_WIFI ? "Wi-Fi" : "なし";
}

bool wifiClientAlive() {
  return wifiClient && wifiClient.connected();
}

// その道が今もつながっているか(USB は揺れを除いた値、Wi-Fi は TCP の接続)。
bool pathAlive(Path path) {
  return path == PATH_USB ? usbConnected : path == PATH_WIFI ? wifiClientAlive() : false;
}

// ---- 送信 ---------------------------------------------------------------
// USB: PC がポートを開いていないときに書き込みで止まらないよう、送れる分が空いているときだけ送る。
// (bool)Serial は見ない(一瞬 false になるため)。つながっていない間に書いた分は ESP32 の送信バッファに
// 残り、あふれたら古い方から捨てられる(HWCDC の動き)。操作の行を送ってよいかは sendCommand が決める。
bool writeUsb(const char* line) {
  size_t length = strlen(line) + 2;
  if (Serial.availableForWrite() < (int)length) return false;
  Serial.print(line);
  Serial.print("\r\n");
  return true;
}

void dropWifiClient() {
  if (wifiClient) wifiClient.stop();
  wifiReader.length = 0;
  wifiReader.overflow = false;
}

// Wi-Fi: 画面を止めないよう、待たずに送る(WiFiClient::write は相手が受け取らないと最大 10 秒ほど待つため使わない)。
// 送り切れなかったら行が途中で切れるので、接続ごと閉じる(PC は定期応答が止まったことで気づき、つなぎ直す)。
bool writeWifi(const char* line) {
  if (!wifiClientAlive()) return false;
  char buffer[300];
  int length = snprintf(buffer, sizeof(buffer), "%s\r\n", line);
  if (length <= 0 || length >= (int)sizeof(buffer)) return false;
  int sent = ::send(wifiClient.fd(), buffer, length, MSG_DONTWAIT);
  if (sent == length) return true;
  dropWifiClient();
  return false;
}

bool writeLine(Path path, const char* line) {
  if (path == PATH_USB) return writeUsb(line);
  if (path == PATH_WIFI) return writeWifi(line);
  return false;
}

void sendCommand(const String& line) {
  screen = HOME;
  pressedId = B_NONE;
  if (currentPath == PATH_NONE || !pathAlive(currentPath) || !writeLine(currentPath, line.c_str())) {
    setMessage(MSG_ERROR, "送れませんでした：" + describe(line),
               "USB ケーブル・PC の Wi-Fi・PC のアプリを確かめて、押し直してください");
    return;
  }
  pendingLine = line;
  pendingAt = millis();
  setMessage(MSG_INFO, "送りました：" + describe(line), "PC の返事を待っています…");
}

// ---- 電池 ---------------------------------------------------------------
// 残量は M5Unified が電池(2 セル)の電圧から出した値。電池が付いていないときは -1。
void readBattery() {
  int level = -1, millivolts = 0;
  bool charging = false;
  int raw = M5.Power.getBatteryVoltage();
  if (M5.Power.getBatteryPresence() != m5::battery_presence_t::absent && raw > 0) {
    millivolts = raw;
    level = M5.Power.getBatteryLevel();
    if (level < 0) level = 0;
    if (level > 100) level = 100;
    charging = M5.Power.isCharging();
  }
  if (level != batteryLevel || charging != batteryCharging) dirty = true;
  batteryLevel = level;
  batteryMillivolts = millivolts;
  batteryCharging = charging;
}

void sendBattery(Path path) {
  char line[48];
  snprintf(line, sizeof(line), "BATT:%d:%d:%d", batteryLevel, batteryMillivolts, batteryCharging ? 1 : 0);
  writeLine(path, line);
}

// ---- 受信 ---------------------------------------------------------------
void handleLine(Path from, const String& line) {
  if (line == HELLO_REQUEST) {
    // 確認が届いた道が今の道になる(PC は両方あれば USB を選ぶ)。
    writeLine(from, HELLO_REPLY);
    if (currentPath != from) {
      currentPath = from;
      lostShown = false;
      setMessage(MSG_INFO, String("PC のアプリと ") + pathName(from) + " でつながりました", "操作する前にボタンを押してください");
    }
    sendBattery(from);
    dirty = true;
    return;
  }
  // 返事は今の道からだけ受け取る。
  if (from != currentPath) return;
  if (line.startsWith("ACK:")) {
    String target = line.substring(4);
    if (target.startsWith("ACT:")) lastOperation = target;
    else if (target == "UNDO") lastOperation = "";
    if (target == pendingLine) pendingLine = "";
    setMessage(MSG_OK, "記録しました：" + describe(target), "");
    return;
  }
  if (line.startsWith("NAK:")) {
    String target = pendingLine;
    pendingLine = "";
    setMessage(MSG_ERROR, target.length() ? "記録できませんでした：" + describe(target) : "記録できませんでした",
               line.substring(4));
    return;
  }
  // それ以外(Arduino 向けの AS100201_PING、STATE: など)は無視する。
}

void feed(LineReader& reader, Path from, int c) {
  if (c == '\n') {
    if (!reader.overflow) {
      reader.line[reader.length] = '\0';
      handleLine(from, String(reader.line));
    }
    reader.length = 0;
    reader.overflow = false;
  } else if (c == '\r') {
    return;
  } else if (reader.length < sizeof(reader.line) - 1) {
    reader.line[reader.length++] = (char)c;
  } else {
    reader.overflow = true;   // 長すぎる行は捨てる
  }
}

void readUsb() {
  while (Serial.available() > 0) {
    int c = Serial.read();
    if (c < 0) break;
    feed(usbReader, PATH_USB, c);
  }
}

// 新しい TCP 接続を受け、届いた分を読む。新しい接続が来たら古い方を閉じる
// (PC が Wi-Fi の途切れのあとにつなぎ直したとき、古い接続が残っていることがある)。
void readWifi() {
  if (!wifiStarted) return;
  WiFiClient incoming = server.accept();
  if (incoming) {
    dropWifiClient();
    wifiClient = incoming;
    wifiClient.setNoDelay(true);
    // 今の道が Wi-Fi なら、つなぎ直しの確認(PING)がすぐ来るまで今の道なしにする。
    if (currentPath == PATH_WIFI) {
      currentPath = PATH_NONE;
      dirty = true;
    }
  }
  while (wifiClientAlive() && wifiClient.available() > 0) {
    int c = wifiClient.read();
    if (c < 0) break;
    feed(wifiReader, PATH_WIFI, c);
  }
}

// 今の道が切れた。PC は自動でもう一方の道(つながっていれば)へ移る。
void pathLost(Path path) {
  currentPath = PATH_NONE;
  lostShown = true;
  const char* detail = path == PATH_USB
      ? "USB ケーブルを確かめてください（Wi-Fi がつながっていれば、PC が自動で切り替えます）"
      : "PC の Wi-Fi 子機と距離を確かめてください（USB ケーブルを挿せば USB でつながります）";
  if (pendingLine.length()) {
    setMessage(MSG_WARN, "PC と切れました：" + describe(pendingLine), "記録されたか PC のアプリの画面で確かめてください");
    pendingLine = "";
  } else {
    setMessage(MSG_ERROR, String("PC と切れました（") + pathName(path) + "）", detail);
  }
  dirty = true;
}

// ---- 配置 ---------------------------------------------------------------
const int HEADER_H = 80;
const int FOOTER_H = 110;
const int GAP = 20;

void addButton(int x, int y, int w, int h, ButtonId id, const char* label, uint16_t color, bool selected = false) {
  if (buttonCount >= MAX_BUTTONS) return;
  buttons[buttonCount++] = {(int16_t)x, (int16_t)y, (int16_t)w, (int16_t)h, id, label, color, selected};
}

// 画面ごとのボタンを並べる。左に選ぶもの、右の列に完了・取消(1 画面目)または戻る(2 画面目)。
void layoutButtons() {
  buttonCount = 0;
  const int W = M5.Display.width();
  const int H = M5.Display.height();
  const int top = HEADER_H + GAP;
  const int bottom = H - FOOTER_H - GAP;
  const int rightW = 300;
  const int rightX = W - GAP - rightW;
  const int leftW = rightX - GAP - GAP;   // 左の領域の幅

  if (screen == HOME) {
    int cw = (leftW - GAP * 2) / 3;
    int ch = (bottom - top - GAP) / 2;
    int y2 = top + ch + GAP;
    addButton(GAP, top, cw, ch, B_MAG, "着磁", C_OP);
    addButton(GAP, y2, cw, ch, B_DEMAG, "減磁", C_OP);
    addButton(GAP + cw + GAP, top, cw, ch, B_NOZ_P, "ノズル\nP 側", C_OP);
    addButton(GAP + cw + GAP, y2, cw, ch, B_NOZ_R, "ノズル\nR 側", C_OP);
    addButton(GAP + (cw + GAP) * 2, top, cw, ch, B_SPC_P, "スペーサー\nP 側", C_OP);
    addButton(GAP + (cw + GAP) * 2, y2, cw, ch, B_SPC_R, "スペーサー\nR 側", C_OP);
    addButton(rightX, top, rightW, ch, B_DONE, "完了", C_DONE);
    addButton(rightX, y2, rightW, ch, B_UNDO, "取消", C_UNDO);
    return;
  }

  const int titleH = 70;
  const int contentTop = top + titleH;
  addButton(rightX, top, rightW, bottom - top, B_BACK, "戻る", C_BACK);

  if (screen == DEMAG || screen == SPACER) {
    int cw = (leftW - GAP) / 2;
    int h = bottom - contentTop;
    if (screen == DEMAG) {
      addButton(GAP, contentTop, cw, h, B_FULL, "しっかり", C_OP);
      addButton(GAP + cw + GAP, contentTop, cw, h, B_TAP, "ちょんちょん", C_OP);
    } else {
      addButton(GAP, contentTop, cw, h, B_UP, "上げる", C_OP);
      addButton(GAP + cw + GAP, contentTop, cw, h, B_DOWN, "下げる", C_OP);
    }
  } else if (screen == NOZZLE) {
    // 上段: 程度(選ぶだけ。初めは普通)。下段: 方向(押すと記録)。
    const int labelW = 120;
    int degW = (leftW - labelW - GAP * 2) / 3;
    int degH = 130;
    int x0 = GAP + labelW;
    addButton(x0, contentTop, degW, degH, B_DEG1, "微調整", C_UNSELECTED, degree == '1');
    addButton(x0 + degW + GAP, contentTop, degW, degH, B_DEG2, "普通", C_UNSELECTED, degree == '2');
    addButton(x0 + (degW + GAP) * 2, contentTop, degW, degH, B_DEG3, "大きく", C_UNSELECTED, degree == '3');
    int dirTop = contentTop + degH + GAP;
    int dirW = (leftW - labelW - GAP) / 2;
    addButton(x0, dirTop, dirW, bottom - dirTop, B_IN, "押し込む", C_OP);
    addButton(x0 + dirW + GAP, dirTop, dirW, bottom - dirTop, B_OUT, "外に出す", C_OP);
  } else if (screen == UNDO_CONFIRM) {
    int cw = (leftW - GAP) / 2;
    int h = 220;
    addButton(GAP, bottom - h, cw, h, B_YES, "はい、取り消す", C_YES);
    addButton(GAP + cw + GAP, bottom - h, cw, h, B_NO, "いいえ", C_BACK);
  }
}

// 押せるのは、PC の確認を受けていて、返事待ちでないときだけ。戻る・いいえ・程度の選択は送らないのでいつでも押せる。
bool buttonEnabled(const Button& b) {
  if (b.id == B_BACK || b.id == B_NO || b.id == B_DEG1 || b.id == B_DEG2 || b.id == B_DEG3) return true;
  return currentPath != PATH_NONE && pathAlive(currentPath) && pendingLine.length() == 0;
}

// ---- 描画 ---------------------------------------------------------------
void drawLabel(const char* label, int cx, int cy, uint16_t color) {
  M5.Display.setTextColor(color);
  M5.Display.setTextDatum(middle_center);
  const char* nl = strchr(label, '\n');
  if (!nl) {
    M5.Display.drawString(label, cx, cy);
    return;
  }
  String first = String(label).substring(0, nl - label);
  int lineH = M5.Display.fontHeight() + 8;
  M5.Display.drawString(first, cx, cy - lineH / 2);
  M5.Display.drawString(nl + 1, cx, cy + lineH / 2);
}

void drawButton(const Button& b) {
  bool enabled = buttonEnabled(b);
  bool pressed = b.id == pressedId;
  uint16_t bg = !enabled ? C_DISABLED : pressed ? C_PRESSED : b.selected ? C_SELECTED : b.color;
  uint16_t fg = !enabled ? C_DISABLED_TEXT : (pressed || b.selected) ? TFT_BLACK : C_TEXT;
  M5.Display.fillRoundRect(b.x, b.y, b.w, b.h, 18, bg);
  if (b.selected) M5.Display.drawRoundRect(b.x, b.y, b.w, b.h, 18, TFT_WHITE);
  M5.Display.setFont(&fonts::lgfxJapanGothicP_40);
  M5.Display.setTextSize(b.h >= 200 ? 1.25f : 1.0f);
  drawLabel(b.label, b.x + b.w / 2, b.y + b.h / 2, fg);
  M5.Display.setTextSize(1.0f);
}

void drawHeader() {
  const int W = M5.Display.width();
  M5.Display.fillRect(0, 0, W, HEADER_H, C_HEADER);
  M5.Display.setFont(&fonts::lgfxJapanGothicP_32);
  M5.Display.setTextSize(1.0f);
  M5.Display.setTextColor(C_TEXT);
  M5.Display.setTextDatum(middle_left);
  M5.Display.drawString("調整記録パネル", GAP, HEADER_H / 2);

  // 電池残量(と Wi-Fi を始められなかったとき)。題名の右。
  String power = batteryLevel < 0 ? String("電池なし")
               : String(batteryCharging ? "充電中 " : "電池 ") + batteryLevel + "%";
  if (!wifiStarted) power += "　Wi-Fi なし";
  M5.Display.setTextColor(batteryLevel >= 0 && batteryLevel <= LOW_BATTERY_LEVEL && !batteryCharging ? C_MSG_WARN_TEXT : C_SUBTEXT);
  M5.Display.drawString(power, 330, HEADER_H / 2);
  M5.Display.setTextColor(C_TEXT);

  uint16_t dot;
  String state;
  if (currentPath == PATH_NONE) {
    bool any = usbConnected || wifiClientAlive();
    dot = any ? C_MSG_WARN : C_MSG_ERROR;
    state = any ? "PC：アプリの確認を待っています" : "PC：つながっていません";
  }
  else if (pendingLine.length()) { dot = C_MSG_WARN; state = String("PC：返事を待っています（") + pathName(currentPath) + "）"; }
  else { dot = C_MSG_OK; state = String("PC：") + pathName(currentPath) + " でつながっています"; }
  M5.Display.setTextDatum(middle_right);
  M5.Display.drawString(state, W - GAP, HEADER_H / 2);
  int textW = M5.Display.textWidth(state.c_str());
  M5.Display.fillCircle(W - GAP - textW - 26, HEADER_H / 2, 14, dot);
}

void drawFooter() {
  const int W = M5.Display.width();
  const int H = M5.Display.height();
  uint16_t bg = messageKind == MSG_OK ? C_MSG_OK : messageKind == MSG_WARN ? C_MSG_WARN
              : messageKind == MSG_ERROR ? C_MSG_ERROR : C_MSG_INFO;
  M5.Display.fillRect(0, H - FOOTER_H, W, FOOTER_H, bg);
  M5.Display.setTextColor(C_TEXT);
  M5.Display.setTextDatum(middle_left);
  M5.Display.setTextSize(1.0f);
  if (messageDetail.length() == 0) {
    M5.Display.setFont(&fonts::lgfxJapanGothicP_40);
    M5.Display.drawString(messageTitle, GAP, H - FOOTER_H / 2);
    return;
  }
  M5.Display.setFont(&fonts::lgfxJapanGothicP_36);
  M5.Display.drawString(messageTitle, GAP, H - FOOTER_H + 32);
  M5.Display.setFont(&fonts::lgfxJapanGothicP_28);
  M5.Display.drawString(messageDetail, GAP, H - FOOTER_H + 80);
}

void drawTitle(const String& text) {
  M5.Display.setFont(&fonts::lgfxJapanGothicP_36);
  M5.Display.setTextSize(1.0f);
  M5.Display.setTextColor(C_TEXT);
  M5.Display.setTextDatum(middle_left);
  M5.Display.drawString(text, GAP, HEADER_H + GAP + 30);
}

void drawScreen() {
  layoutButtons();
  M5.Display.startWrite();
  M5.Display.fillScreen(C_BG);
  drawHeader();

  if (screen == DEMAG) drawTitle("減磁：程度を押すと記録します");
  else if (screen == SPACER) drawTitle(String("スペーサー ") + side + " 側：向きを押すと記録します");
  else if (screen == NOZZLE) {
    drawTitle(String("ノズル ") + side + " 側：程度を選んでから、方向を押すと記録します");
    M5.Display.setFont(&fonts::lgfxJapanGothicP_32);
    M5.Display.setTextDatum(middle_left);
    M5.Display.setTextColor(C_SUBTEXT);
    // ボタンの左に「程度」「方向」
    for (int i = 0; i < buttonCount; ++i) {
      if (buttons[i].id == B_DEG1) M5.Display.drawString("程度", GAP, buttons[i].y + buttons[i].h / 2);
      if (buttons[i].id == B_IN) M5.Display.drawString("方向", GAP, buttons[i].y + buttons[i].h / 2);
    }
  } else if (screen == UNDO_CONFIRM) {
    drawTitle("取消");
    M5.Display.setFont(&fonts::lgfxJapanGothicP_40);
    M5.Display.setTextColor(C_TEXT);
    M5.Display.setTextDatum(middle_left);
    int y = HEADER_H + GAP + 110;
    if (lastOperation.length()) {
      M5.Display.drawString("直前の操作を取り消しますか", GAP, y);
      M5.Display.drawString("「" + describe(lastOperation) + "」", GAP, y + 70);
    } else {
      M5.Display.drawString("直前の操作を取り消しますか", GAP, y);
      M5.Display.setFont(&fonts::lgfxJapanGothicP_28);
      M5.Display.setTextColor(C_SUBTEXT);
      M5.Display.drawString("（このパネルでは直前の操作が分かりません。取り消すのは PC の記録の直前の操作です）", GAP, y + 70);
    }
  }

  for (int i = 0; i < buttonCount; ++i) drawButton(buttons[i]);
  drawFooter();
  M5.Display.endWrite();
  dirty = false;
}

void redrawButton(ButtonId id) {
  for (int i = 0; i < buttonCount; ++i) {
    if (buttons[i].id != id) continue;
    M5.Display.startWrite();
    drawButton(buttons[i]);
    M5.Display.endWrite();
  }
}

// ---- タッチ -------------------------------------------------------------
ButtonId hitTest(int x, int y) {
  for (int i = 0; i < buttonCount; ++i) {
    const Button& b = buttons[i];
    if (x >= b.x && x < b.x + b.w && y >= b.y && y < b.y + b.h) return buttonEnabled(b) ? b.id : B_NONE;
  }
  return B_NONE;
}

void activate(ButtonId id) {
  switch (id) {
    case B_MAG:   sendCommand("ACT:MAG"); break;
    case B_DEMAG: screen = DEMAG; break;
    case B_NOZ_P: screen = NOZZLE; side = 'P'; degree = '2'; break;
    case B_NOZ_R: screen = NOZZLE; side = 'R'; degree = '2'; break;
    case B_SPC_P: screen = SPACER; side = 'P'; break;
    case B_SPC_R: screen = SPACER; side = 'R'; break;
    case B_DONE:  sendCommand("DONE"); break;
    case B_UNDO:  screen = UNDO_CONFIRM; break;
    case B_BACK:
    case B_NO:    screen = HOME; break;
    case B_FULL:  sendCommand("ACT:DEMAG:FULL"); break;
    case B_TAP:   sendCommand("ACT:DEMAG:TAP"); break;
    case B_DEG1:  degree = '1'; break;
    case B_DEG2:  degree = '2'; break;
    case B_DEG3:  degree = '3'; break;
    case B_IN:    sendCommand(String("ACT:NOZZLE:") + side + ":IN:" + degree); break;
    case B_OUT:   sendCommand(String("ACT:NOZZLE:") + side + ":OUT:" + degree); break;
    case B_UP:    sendCommand(String("ACT:SPACER:") + side + ":UP"); break;
    case B_DOWN:  sendCommand(String("ACT:SPACER:") + side + ":DOWN"); break;
    case B_YES:   sendCommand("UNDO"); break;
    default: return;
  }
  dirty = true;
}

// 画面を元の明るさに戻す。
void wake(unsigned long now) {
  lastTouchAt = now;
  if (!dimmed) return;
  dimmed = false;
  M5.Display.setBrightness(normalBrightness);
}

// 押した所で色を変え、同じボタンの上で指を離したら確定する(押したまま外へずらせば取りやめ)。
// 暗いときの最初のタッチは明るくするだけで、ボタンとしては受け付けない(暗い画面を触って誤って記録しないため)。
void handleTouch(unsigned long now) {
  if (M5.Touch.getCount() == 0 && pressedId == B_NONE) return;
  if (M5.Touch.getCount() > 0 && !dimmed) lastTouchAt = now;   // 押している間は暗くしない
  auto t = M5.Touch.getDetail();
  if (t.wasPressed()) {
    if (dimmed) {
      wake(now);
      return;
    }
    pressedId = hitTest(t.x, t.y);
    if (pressedId != B_NONE) redrawButton(pressedId);
  } else if (t.wasReleased()) {
    ButtonId released = hitTest(t.x, t.y);
    ButtonId pressed = pressedId;
    pressedId = B_NONE;
    if (pressed == B_NONE) return;
    if (released == pressed) activate(pressed);
    else redrawButton(pressed);
  }
}

// ---- 本体 ---------------------------------------------------------------
void setupColors() {
  auto& d = M5.Display;
  C_BG = d.color565(0x16, 0x1B, 0x26);
  C_HEADER = d.color565(0x0B, 0x0F, 0x17);
  C_TEXT = TFT_WHITE;
  C_SUBTEXT = d.color565(0xB8, 0xC0, 0xCC);
  C_OP = d.color565(0x25, 0x63, 0xC9);
  C_DONE = d.color565(0x22, 0x8B, 0x4A);
  C_UNDO = d.color565(0xB0, 0x63, 0x1E);
  C_BACK = d.color565(0x55, 0x5D, 0x6B);
  C_SELECTED = d.color565(0xF2, 0xC2, 0x30);
  C_UNSELECTED = d.color565(0x3A, 0x47, 0x5C);
  C_DISABLED = d.color565(0x30, 0x35, 0x3F);
  C_DISABLED_TEXT = d.color565(0x78, 0x7F, 0x8A);
  C_PRESSED = d.color565(0xE6, 0xEE, 0xFA);
  C_YES = d.color565(0xC0, 0x39, 0x2B);
  C_MSG_INFO = d.color565(0x2B, 0x33, 0x42);
  C_MSG_OK = d.color565(0x1D, 0x6B, 0x3A);
  C_MSG_WARN = d.color565(0x9A, 0x63, 0x00);
  C_MSG_ERROR = d.color565(0x9B, 0x25, 0x25);
  C_MSG_WARN_TEXT = d.color565(0xF2, 0xC2, 0x30);
}

void setup() {
  Serial.setTxBufferSize(2048);
  Serial.setRxBufferSize(1024);
  Serial.begin(115200);
  Serial.setTxTimeoutMs(0);   // PC が読んでいなくても画面を止めない

  auto cfg = M5.config();
  M5.begin(cfg);
  // 横置きにする(Tab5 の画面は縦長が基準)
  if (M5.Display.width() < M5.Display.height()) M5.Display.setRotation((M5.Display.getRotation() + 1) % 4);
  if (FLIP_LANDSCAPE) M5.Display.setRotation((M5.Display.getRotation() + 2) % 4);
  setupColors();
  uint8_t brightness = M5.Display.getBrightness();
  if (brightness > DIM_BRIGHTNESS) normalBrightness = brightness;

  usbConnected = (bool)Serial;
  lastUsbSeenAt = millis();
  writeUsb("DIAG_BOOT:1");
  readBattery();
  setMessage(MSG_INFO, "起動しました", "Wi-Fi を始めています…");
  drawScreen();

  // Wi-Fi の親機を始める(無線用チップ ESP32-C6 を経由。ピンはボード M5Tab5 の設定にある)。
  // 始められなくても USB では使える。
  // ゲートウェイは 0.0.0.0 にして、PC へ「この先にインターネットがある」と知らせない(DHCP でルーターを渡さない)。
  // 渡すと、PC がふだんのインターネットの通信を Tab5 へ流してしまうことがある。DNS も渡さない。
  WiFi.mode(WIFI_AP);
  wifiStarted = WiFi.softAPConfig(PANEL_IP, IPAddress(0, 0, 0, 0), IPAddress(255, 255, 255, 0)) &&
                WiFi.softAP(WIFI_SSID, WIFI_PASSWORD);
  if (wifiStarted) {
    server.begin();
    server.setNoDelay(true);
    setMessage(MSG_INFO, "起動しました", String("PC のアプリで「学習モード」を選ぶと、USB か Wi-Fi（") + WIFI_SSID + "）でつながります");
  } else {
    setMessage(MSG_WARN, "Wi-Fi を始められませんでした", "USB ケーブルでつなげば使えます");
  }
  lastBatteryAt = lastTouchAt = millis();
  dirty = true;
}

void loop() {
  M5.update();
  unsigned long now = millis();

  // 受信があれば、その時点で USB は生きている。
  bool seen = (bool)Serial || Serial.available() > 0;
  if (seen) {
    lastUsbSeenAt = now;
    if (!usbConnected) {
      usbConnected = true;
      if (lostShown && currentPath == PATH_NONE) {
        lostShown = false;
        setMessage(MSG_INFO, "USB がつながりました", "PC のアプリが自動で確かめ直すと、操作できるようになります");
      }
      dirty = true;
    }
  } else if (usbConnected && now - lastUsbSeenAt >= DISCONNECT_CONFIRM_MS) {
    usbConnected = false;
    if (currentPath == PATH_USB) pathLost(PATH_USB);
    dirty = true;
  }

  readUsb();
  readWifi();
  if (currentPath == PATH_WIFI && !wifiClientAlive()) pathLost(PATH_WIFI);

  // 定期応答は今の道にだけ送る(PC はこれが 1 秒止まると操作を受け付けない)。
  if (now - lastFrameAt >= FRAME_INTERVAL_MS) {
    lastFrameAt = now;
    if (currentPath != PATH_NONE) {
      char frame[48];
      snprintf(frame, sizeof(frame), "DIAG_FRAME:%lu:%lu", ++frameSequence, now);
      writeLine(currentPath, frame);
    }
  }

  if (now - lastBatteryAt >= BATTERY_INTERVAL_MS) {
    lastBatteryAt = now;
    readBattery();
    if (currentPath != PATH_NONE) sendBattery(currentPath);
  }

  if (pendingLine.length() && now - pendingAt >= REPLY_TIMEOUT_MS) {
    setMessage(MSG_WARN, "PC から返事がありません：" + describe(pendingLine), "記録されたか PC のアプリの画面で確かめてください（自動では送り直しません）");
    pendingLine = "";
  }

  handleTouch(now);
  if (!dimmed && pressedId == B_NONE && now - lastTouchAt >= DIM_AFTER_MS) {
    dimmed = true;
    M5.Display.setBrightness(DIM_BRIGHTNESS);
  }
  if (dirty && pressedId == B_NONE) drawScreen();
  delay(5);
}
