// AS100-201 学習モードのタッチパネル(M5Stack Tab5、ESP32-P4、5 インチ 1280×720)
// 仕様: specs/AS100201SwitchController/SPEC_調整操作の記録.md
//
// 版 1(2026-11-13): 横置き・仮レイアウト。実物で見て直す前提。
//   - 1 画面目: 着磁・減磁・ノズル P/R・スペーサー P/R の 6 操作と、完了・取消
//   - 2 画面目: 減磁(しっかり / ちょんちょん)、ノズル(程度を選んでから方向を押すと確定。程度は「普通」が選ばれた状態で開く)、
//     スペーサー(上げる / 下げる)。どれも「戻る」で 1 画面目に戻れる(何も送らない)
//   - 取消: 確認「直前の○○を取り消しますか」→ はい で UNDO を送る
//   - 2 画面目の選択と取消の確認はパネルの中で完結させ、確定した 1 行だけを PC に送る
//
// PC とのやり取り(115200bps 相当・LF の行単位。USB-C の内蔵 USB シリアル):
//   PC → パネル  AS100201_PANEL_PING           → パネルは AS100201_PANEL_READY:1 を返す
//   パネル → PC  DIAG_FRAME:<連番>:<millis>     250ms ごと(PC はこれが 1 秒止まると操作を受け付けない)
//   パネル → PC  DIAG_BOOT:1                    起動通知
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

// ---- 取り決め ----------------------------------------------------------
static const char* const HELLO_REQUEST = "AS100201_PANEL_PING";
static const char* const HELLO_REPLY = "AS100201_PANEL_READY:1";   // 版 1。PC 側 As100201PanelProtocol.PanelVersion と揃える
const unsigned long FRAME_INTERVAL_MS = 250;
const unsigned long REPLY_TIMEOUT_MS = 2000;   // 送ってから ACK / NAK が来ないと「返事なし」を出す

// 横置きの向き。上下が逆に見えたら true にして書き込み直す。
const bool FLIP_LANDSCAPE = false;

// ---- 色 ----------------------------------------------------------------
uint16_t C_BG, C_HEADER, C_TEXT, C_SUBTEXT, C_OP, C_DONE, C_UNDO, C_BACK, C_SELECTED, C_UNSELECTED,
    C_DISABLED, C_DISABLED_TEXT, C_PRESSED, C_YES, C_MSG_INFO, C_MSG_OK, C_MSG_WARN, C_MSG_ERROR;

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
bool usbConnected = false;    // PC が USB シリアルを開いている
bool verified = false;        // USB がつながってから PC の確認(PING)を受けた
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

char rxLine[256];
size_t rxLength = 0;
bool rxOverflow = false;

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

// ---- 送信 ---------------------------------------------------------------
// PC がポートを開いていないときに書き込みで止まらないよう、送れる分が空いているときだけ送る。
bool writeLine(const char* line) {
  if (!Serial) return false;
  size_t length = strlen(line) + 2;
  if (Serial.availableForWrite() < (int)length) return false;
  Serial.print(line);
  Serial.print("\r\n");
  return true;
}

void sendCommand(const String& line) {
  screen = HOME;
  pressedId = B_NONE;
  if (!writeLine(line.c_str())) {
    setMessage(MSG_ERROR, "送れませんでした：" + describe(line), "USB ケーブルと PC のアプリを確かめて、押し直してください");
    return;
  }
  pendingLine = line;
  pendingAt = millis();
  setMessage(MSG_INFO, "送りました：" + describe(line), "PC の返事を待っています…");
}

// ---- 受信 ---------------------------------------------------------------
void handleLine(const String& line) {
  if (line == HELLO_REQUEST) {
    writeLine(HELLO_REPLY);
    if (!verified) {
      verified = true;
      setMessage(MSG_INFO, "PC のアプリとつながりました", "操作する前にボタンを押してください");
    }
    dirty = true;
    return;
  }
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

void readSerial() {
  while (Serial.available() > 0) {
    int c = Serial.read();
    if (c < 0) break;
    if (c == '\n') {
      if (!rxOverflow) {
        rxLine[rxLength] = '\0';
        handleLine(String(rxLine));
      }
      rxLength = 0;
      rxOverflow = false;
    } else if (c == '\r') {
      continue;
    } else if (rxLength < sizeof(rxLine) - 1) {
      rxLine[rxLength++] = (char)c;
    } else {
      rxOverflow = true;   // 長すぎる行は捨てる
    }
  }
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
  return usbConnected && verified && pendingLine.length() == 0;
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

  uint16_t dot;
  const char* state;
  if (!usbConnected) { dot = C_MSG_ERROR; state = "PC：USB がつながっていません"; }
  else if (!verified) { dot = C_MSG_WARN; state = "PC：アプリの確認を待っています"; }
  else if (pendingLine.length()) { dot = C_MSG_WARN; state = "PC：返事を待っています"; }
  else { dot = C_MSG_OK; state = "PC：つながっています"; }
  M5.Display.setTextDatum(middle_right);
  M5.Display.drawString(state, W - GAP, HEADER_H / 2);
  int textW = M5.Display.textWidth(state);
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

// 押した所で色を変え、同じボタンの上で指を離したら確定する(押したまま外へずらせば取りやめ)。
void handleTouch() {
  if (M5.Touch.getCount() == 0 && pressedId == B_NONE) return;
  auto t = M5.Touch.getDetail();
  if (t.wasPressed()) {
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

  usbConnected = (bool)Serial;
  writeLine("DIAG_BOOT:1");
  drawScreen();
}

void loop() {
  M5.update();
  unsigned long now = millis();

  bool connected = (bool)Serial;
  if (connected != usbConnected) {
    usbConnected = connected;
    if (!connected) {
      verified = false;
      if (pendingLine.length()) {
        setMessage(MSG_WARN, "PC と切れました：" + describe(pendingLine), "記録されたか PC のアプリの画面で確かめてください");
        pendingLine = "";
      } else {
        setMessage(MSG_ERROR, "PC と切れました", "USB ケーブルを確かめてください");
      }
    }
    dirty = true;
  }

  readSerial();

  if (now - lastFrameAt >= FRAME_INTERVAL_MS) {
    lastFrameAt = now;
    char frame[48];
    snprintf(frame, sizeof(frame), "DIAG_FRAME:%lu:%lu", ++frameSequence, now);
    writeLine(frame);
  }

  if (pendingLine.length() && now - pendingAt >= REPLY_TIMEOUT_MS) {
    setMessage(MSG_WARN, "PC から返事がありません：" + describe(pendingLine), "記録されたか PC のアプリの画面で確かめてください（自動では送り直しません）");
    pendingLine = "";
  }

  handleTouch();
  if (dirty && pressedId == B_NONE) drawScreen();
  delay(5);
}
