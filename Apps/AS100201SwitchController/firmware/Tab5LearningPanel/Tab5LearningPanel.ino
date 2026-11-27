// AS100-201 のタッチパネル(M5Stack Tab5、ESP32-P4、5 インチ 1280×720)。版 6 から学習モード・通常モードの両方で使う
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
// 版 3(2026-11-13、人の指示。レイアウトは仮で、実物で変える前提)
//   - ノズルは程度を選ばず、方向だけ(押し込む → 近づける、外に出す → 離す)。ACT:NOZZLE:<P|R>:<IN|OUT>
//   - スペーサーは 上げる / 下げる のあとに、厚さ(箱の表示。SPACER_THICKNESSES)を選ぶページ、
//     表示との差(μm、-3〜+3 か「測っていない」)を選ぶページを出し、差を押すと記録する。
//     ACT:SPACER:<P|R>:<UP|DOWN>:<厚さ 例 0.450>:<差 例 +2 / -1 / 0 / NA>
//   - パネルに触れたら(1 画面目で 2 画面目のあるボタンを押した・暗い画面に触れた)HOLD を送る。
//     PC は測定中なら LOGGER を止める(人が次の行動を決めた所までを学習データにするため)。HOLD は返事を待たない
//
// 版 4(2026-11-13、人の指示): 選び終わるまで測定できないようにする
//   - 2 画面目以降(操作を選んでいる途中)に入ったら SELECT、1 画面目に戻ったら HOME を送る。PC は SELECT の間、
//     READY LED を消し PC_go / CR_go / F8 を受け付けない(sw4 は受け付ける)。PC の確認(PING)に答えた直後にも送り直す
//     (つなぎ直し・パネルの再起動で、PC が選択中のまま止まらないように)
//   - 2026-11-17 追記(PC とのやり取りは変えないので版は 4 のまま):
//     PC が USB を開いたときなどに本体(ESP32-P4)が再起動すると、PC と Wi-Fi で通じなくなっていた。
//     原因は PC の子機が親機のやり直しに気づかず古いつながりに留まることで、PC のアプリが子機をつなぎ直して直した。
//     その前に、無線用チップ ESP32-C6 が前の状態のまま残ることを疑って、起動のたびに Wi-Fi を始める前に
//     C6 の電源を入れ直す処理(wifiChipPowerCycle)を入れた。害はないので残している。
//     あわせて、USB の確認に答えたあと切り分け用の DIAG_WIFI: を送る(PC は知らない行としてログに残すだけ)。
//   - 2026-11-19 追記(やり取りは変えないので版 4 のまま): PC の子機がいきなり抜けると、切れたという知らせ(TCP の FIN / RST)が
//     届かず、送った行は TCP の送信バッファに溜まるだけなので、「Wi-Fi でつながっています」を出し続けていた。
//     受けた TCP 接続に keepalive を付け(PC から何も届かない時間が約 WIFI_KEEPALIVE_* 秒続いたら閉じる)、
//     あわせて子機が親機から離れた知らせ(AP_STADISCONNECTED)で親機につながる子機が 0 台になったら、すぐ閉じる。
//
// 版 5(2026-11-26、人の決定。SPEC_調整操作の記録.md「版 5 の変更」): 押し忘れの印と、測定の取消
//   - 1 画面目の右の列を 完了・取消・「測定中に動かした」の 3 段にする。「測定中に動かした」は切り替えで、入れると
//     次に確定する操作 1 件の行の後ろに :MOVED を付ける(着磁・減磁・ノズル・スペーサーのどれにも)。送れたら切れる
//   - PC が直前の記録(取消の対象)を LAST: で知らせる。取消の確認には、何が消えるか(測定なら「使わないにしますか」、
//     操作なら「取り消しますか」)を出す。取り消せるものが無ければ「はい」は押せない
//
// 版 6(2026-11-27、人の承認。SPEC_調整操作の記録.md「版 6 の変更」): Arduino のスイッチ箱をなくし、全部この Tab5 にまとめる
//   - 1 画面目: 発振器の 4 つ(PC 位相1・2、CR 位相1・2。押すと位相を出し直し、止まっていれば測り始める)と、
//     横長の「バルブを動かす」(測っていれば止めて保存し、操作の画面へ)。右の列は完了・取消
//   - 操作の画面: 着磁・減磁・ノズル P/R・スペーサー P/R と、戻る・「測定中に動かした」の印(版 5 の印はここへ移した)
//   - ノズルの画面(「P 側の立ち上がりを合わせています」): 発振器の 4 つ(合わせている側でない方は灰色で押せない)、
//     「P 側の立ち上がりを合わせた」・測り直す・戻る。測っている間に PC を押し直しても区切らない(PC が決める)
//   - 通常モード(PC が STATE:N で知らせる): 1 画面目に発振器の 4 つと「測定停止」だけ
//   - 触れても測定は止めない(HOLD をなくした)。測っている間とノズルの画面にいる間は画面を暗くしない
//   - 測っている位相のボタンには黄色の枠と「測定中」を出す(PC が STATE: で知らせる)。PC の処理中はボタンを押せない
//
// 版 7(2026-11-27、人の要望): 発振器のボタンの下に、位相(°)の代わりに電流(mA)と向き(上り ↑ / 下り ↓)を出す
//   - PC が STATE: の位相 4 つの所で、mA の 10 倍(符号付き)と向き U / D を送る(例 255U = +25.5mA の上り)。
//     換算(指令 5V = 30mA)と三角波の形は PC が持つ。パネルは届いた値をそのまま出すだけ。4 つとも同じ文字の大きさ
//   - 学習モードの 1 画面目の下の段を「バルブを動かす」と「測定停止」に分ける(測っている間だけ押せる。STOP を送り、1 画面目のまま)。
//     PC は位相を出し直さずに測っているとき、STATE: の測っている位相の所に M を送る
//   - 「測定中に動かした」を入れたら、操作の画面の題名と下の帯で、次に測定中にしてしまった操作を選ぶよう案内する
//
// PC とのやり取り(LF の行単位。USB-C の内蔵 USB シリアル、または Wi-Fi の TCP。どちらも同じ行):
//   PC → パネル  AS100201_PANEL_PING           → パネルは AS100201_PANEL_READY:7 を返す(この道が今の道になる)
//   パネル → PC  DIAG_FRAME:<連番>:<millis>     250ms ごと(PC はこれが 1 秒止まると操作を受け付けない)
//   パネル → PC  DIAG_BOOT:1                    起動通知(USB)
//   パネル → PC  BATT:<残量%>:<電圧mV>:<充電中 0/1>  30 秒ごと(電池が無いときは残量 -1)
//   パネル → PC  PHASE:PC1|PC2|CR1|CR2 / VALVE / REMEASURE / STOP(版 6。返事 ACK は表示しない)
//   パネル → PC  ACT:MAG / ACT:DEMAG:FULL|TAP / ACT:NOZZLE:P|R:START|DONE|CANCEL / ACT:SPACER:P|R:UP|DOWN:<厚さ>:<差> / DONE / UNDO
//               操作の行には、後ろに :MOVED(測定中に動かした)が付くことがある(版 5。戻る CANCEL には付かない)
//   パネル → PC  HOME / SELECT / NOZZLE:P / NOZZLE:R   画面の位置(1 画面目 / 操作を選んでいる途中 / ノズルの画面。返事なし)
//   PC → パネル  ACK:<受け取った行> / NAK:<理由>(理由は日本語 UTF-8)
//   PC → パネル  LAST:M:<説明> / LAST:O:<説明> / LAST:  直前の記録(測定 / 操作 / 取り消せるものなし。版 5)。
//               記録が変わったときと、画面の位置を受けるたび(1 秒ごと)に届く
//   PC → パネル  STATE:<L|N>:<測っている位相 PC1 など、止まっていれば ->:<処理中 0|1>:<電流 4 つ 例 255U / -238D>
//               (版 6 から。電流は mA の 10 倍と向き U 上り / D 下り。版 6 は位相°だった)
//   PC → パネル  GOTO:NOZZLE:P|R(「合わせた」を取り消したとき、ノズルの画面へ戻す)/ MSG:<E|W|I>:<文>(後から分かった知らせ)
//   Arduino 向けの AS100201_PING や知らない行は無視する。
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
static const char* const HELLO_REPLY = "AS100201_PANEL_READY:7";   // 版 7。PC 側 As100201PanelProtocol.PanelVersion と揃える
static const char* const MOVED_SUFFIX = ":MOVED";                   // 測定中に動かした印(版 5)

// スペーサーの箱の表示(厚さ mm)。2026-11-13 に箱の写真 3 枚から読み取った(手書きの札は読み違いがありうる。実物で確かめる)。
// 箱が増えたら、ここと PC 側 As100201SpacerCatalog(開発用の代わりのパネルの選択肢)の両方を直す。
// 0.2・0.3 台 / 0.4 台 / 0.5 台以上 の 3 つのタブに分けて出す(1 タブ 5 列。20 個までなら 4 段に収まる)。
static const char* const SPACER_THICKNESSES[] = {
  "0.271", "0.298", "0.300", "0.310", "0.320", "0.325", "0.330", "0.340", "0.348", "0.350",
  "0.358", "0.360", "0.363", "0.370", "0.375", "0.380", "0.390", "0.394", "0.398",
  "0.400", "0.410", "0.416", "0.420", "0.425", "0.427", "0.430", "0.440", "0.450", "0.458",
  "0.460", "0.465", "0.470", "0.480", "0.483", "0.485", "0.490",
  "0.500", "0.510", "0.520", "0.530", "0.540", "0.542", "0.550", "0.560", "0.568", "0.570",
  "0.580", "0.590", "0.593",
};
const int SPACER_COUNT = sizeof(SPACER_THICKNESSES) / sizeof(SPACER_THICKNESSES[0]);
static const char* const SPACER_TAB_LABELS[] = {"0.2・0.3 台", "0.4 台", "0.5 台〜"};
const int SPACER_TABS = 3;
// 2026-11-19 に 7 列を Tab5 で試したら数字が窮屈で読みにくかった(人)。5 列に戻し、厚さのページの題名をなくして
// 空いた高さでボタンを高くした(人の決定)。
const int SPACER_COLUMNS = 5;

// 表示との差(μm)の選択肢。だいたい ±3μm 違うとのこと(人の話)。広げるときはここだけ直す(PC は ±50 まで受け付ける)。
static const char* const SPACER_DIFFS[] = {"-3", "-2", "-1", "0", "+1", "+2", "+3"};
const int SPACER_DIFF_COUNT = sizeof(SPACER_DIFFS) / sizeof(SPACER_DIFFS[0]);

// Wi-Fi(Tab5 が親機)。名前とパスワードはここにだけ書く(2026-11-13、AI の仮決め)。変えたら書き込み直し、
// PC の子機の Wi-Fi の登録もし直す。IP とポートは PC 側 As100201PanelNetworkSettings と揃える。
static const char* const WIFI_SSID = "AS100201_PANEL";
static const char* const WIFI_PASSWORD = "as100201-panel";   // 8 文字以上
const IPAddress PANEL_IP(192, 168, 4, 1);                    // 社内 LAN と同じ範囲なら PC 側と両方を変える
const uint16_t PANEL_PORT = 50201;

// PC の子機がいきなり抜けたときに気づくための TCP keepalive(2026-11-19、AI の仮決め)。PC は定期応答(250ms ごと)に
// TCP の受け取り(ACK)を返すので、ふだんは何かが届き続ける。最後に届いてから IDLE 秒で問い合わせを始め、
// INTERVAL 秒おきに COUNT 回答えがなければ閉じる(合わせて約 6 秒)。数秒の電波の途切れでは切らない。
const int WIFI_KEEPALIVE_IDLE_S = 3;
const int WIFI_KEEPALIVE_INTERVAL_S = 1;
const int WIFI_KEEPALIVE_COUNT = 3;

// 無線用チップ ESP32-C6 の電源(WLAN_PWR_EN)。I/O 拡張チップ 2 個目(PI4IOE5V6408、I2C 0x44 = M5Unified の
// getIOExpander(1))の P0 で、High で電源が入る(M5GFX の Tab5 の電源投入手順で 0x44 の出力を 0x89 にしている)。
// 本体だけが再起動しても M5GFX は P0 を High のまま書くので、C6 は止まらない。そのため起動時に自分で切って入れ直す。
const uint8_t WIFI_CHIP_POWER_EXPANDER = 1;
const uint8_t WIFI_CHIP_POWER_PIN = 0;
const unsigned long WIFI_CHIP_OFF_MS = 300;        // 切っておく時間(AI の仮決め)
const unsigned long WIFI_CHIP_BOOT_MS = 500;       // 入れてから Wi-Fi を始めるまで(この後の Wi-Fi の準備でも C6 の起動を待つ)

const unsigned long BATTERY_INTERVAL_MS = 30000;   // 電池残量を読んで送る間隔
const int LOW_BATTERY_LEVEL = 20;                  // これ以下で画面の残量を警告の色にする(PC のログと同じ値)
const int BATTERY_MIN_PRESENT_MV = 4000;           // これ未満の電圧は電池なし(PC の As100201PanelBattery.MinPresentMillivolts と揃える)
const int BATTERY_MIN_PLAUSIBLE_MV = 6000;         // 4000 以上でこれ未満は 2 セルではありえないので読み違いとして捨てる(版 7)
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
    C_DISABLED, C_DISABLED_TEXT, C_PRESSED, C_YES, C_MSG_INFO, C_MSG_OK, C_MSG_WARN, C_MSG_ERROR, C_MSG_WARN_TEXT,
    C_PC, C_CR, C_VALVE;

// ---- 画面とボタン -------------------------------------------------------
// 版 6: OPS(操作の画面)を足し、NOZZLE はノズルの立ち上がりを合わせる画面にした。
enum Screen : uint8_t { HOME, OPS, DEMAG, NOZZLE, SPACER, SPACER_PICK, SPACER_DIFF, UNDO_CONFIRM };

enum ButtonId : uint8_t {
  B_NONE, B_MAG, B_DEMAG, B_NOZ_P, B_NOZ_R, B_SPC_P, B_SPC_R, B_DONE, B_UNDO,
  B_BACK, B_FULL, B_TAP, B_UP, B_DOWN, B_YES, B_NO,
  B_TAB, B_THICK, B_DIFF, B_NOT_MEASURED, B_MOVED,
  B_PHASE, B_VALVE, B_STOP, B_ALIGNED, B_REMEASURE
};

// 発振器のボタン(版 6)。PHASE:<名前> で送る。位相の値は PC が STATE: で知らせる(PC の画面の位相の欄)。
static const char* const PHASE_CODES[] = {"PC1", "PC2", "CR1", "CR2"};
const int PHASE_COUNT = 4;

struct Button {
  int16_t x, y, w, h;
  ButtonId id;
  const char* label;   // 2 行にするときは '\n' で区切る
  uint16_t color;
  bool selected;       // 選択中の表示(タブ・前回の厚さ)
  int8_t value;        // タブ・厚さ・差の番号(それ以外は -1)
};

const int MAX_BUTTONS = 32;
Button buttons[MAX_BUTTONS];
int buttonCount = 0;

Screen screen = HOME;
char side = 'P';         // ノズル・スペーサーの P / R
bool spacerUp = true;    // スペーサーの向き(上げる / 下げる)
int spacerTab = 1;       // 厚さのページで開いているタブ(初めは 0.4 台)
int spacerIndex = -1;    // 選んだ厚さ(SPACER_THICKNESSES の番号。まだなら -1)
// 関数(spacerTabOf など)は Path の宣言より後に書く。Arduino が最初の関数の前に関数の宣言を自動で入れるため、
// ここより前に関数があると、Path を使う関数の宣言が Path より前に入ってコンパイルできない。

// ---- 状態 ---------------------------------------------------------------
// PC への道。PC の確認(PING)が最後に届いた道が「今の道」(なければ PATH_NONE)。
enum Path : uint8_t { PATH_NONE, PATH_USB, PATH_WIFI };
Path currentPath = PATH_NONE;

bool usbConnected = false;    // PC が USB シリアルを開いている(一瞬の揺れは無視した値)
unsigned long lastUsbSeenAt = 0;  // 最後に USB がつながっていると見えた時刻(受信があったときも含む)
bool lostShown = false;       // 「PC と切れました」を出したあと、まだつながり直していない

bool wifiStarted = false;     // Wi-Fi の親機を始められた
// Wi-Fi の切り分け用(2026-11-17)。USB の確認に答えたあとに DIAG_WIFI: で PC へ送る(PC はログに残すだけ)。
bool wifiPowerOffOk = false, wifiPowerOnOk = false, wifiPowerReadback = false;
unsigned long wifiStartMs = 0;
WiFiServer server(PANEL_PORT);
WiFiClient wifiClient;        // PC からの TCP 接続(1 本だけ。新しい接続が来たら古い方を閉じる)
// 子機が親機から離れた知らせ(Wi-Fi のイベント。別のタスクで呼ばれるので、印だけ付けて loop で見る)。
volatile bool stationLeft = false;

int batteryLevel = -1;        // 残量 %(電池なしは -1)
int batteryMillivolts = 0;
bool batteryCharging = false;
unsigned long lastBatteryAt = 0;

uint8_t normalBrightness = 160;
bool dimmed = false;
unsigned long lastTouchAt = 0;

String pendingLine;           // 送って返事を待っている行(空なら待っていない)
unsigned long pendingAt = 0;
int pressedIndex = -1;          // 押している最中のボタン(buttons の添字。なければ -1)
// 版 5: 「測定中に動かした」の印(次に確定する操作 1 件に付く)。
bool movedMark = false;
// 版 5: PC から届いた直前の記録(取消の対象)。kind は 'M'(測定)/ 'O'(操作)/ 0(取り消せるものなし)。
// 取消の確認を開いたら known を外し、PC から新しい知らせが届くまで「はい」を押せなくする(古い対象を出さないため)。
bool lastRecordKnown = false;
char lastRecordKind = 0;
String lastRecordText;

// 版 6: PC が STATE: で知らせる今の状態。届くまでは学習モード・止まっている・電流は初期値(PC の位相の初期値 0° / 75° / 140° / 250° の値)。
char panelMode = 'L';          // 'L' 学習モード / 'N' 通常モード
int measuringPhase = -1;       // 測っている位相のボタン(PHASE_CODES の番号。止まっていれば -1)
bool pcMeasuring = false;      // 測っている(位相を出し直さずに始めたとき PC は M を送る。版 7)
bool pcBusy = false;           // PC の処理中(PC / CR で区切って測り直すなど)。この間はボタンを押せない
// 版 7: 位相の点の電流(mA の 10 倍、符号付き)と、そこから上るか(PC が換算して送る)。
int phaseTenthsMilliamps[PHASE_COUNT] = {0, 255, 136, -238};
bool phaseRising[PHASE_COUNT] = {true, true, false, false};
char phaseLabels[PHASE_COUNT][24];   // ボタンの名前(例 "PC\n+25.5mA↑")

enum MessageKind : uint8_t { MSG_INFO, MSG_OK, MSG_WARN, MSG_ERROR };
MessageKind messageKind = MSG_INFO;
String messageTitle = "起動しました";
String messageDetail = "PC のアプリで「学習モード」を選ぶと、つながります";

bool dirty = true;
unsigned long frameSequence = 0;
unsigned long lastFrameAt = 0;
String reportedScreen;               // PC に最後に送れた画面の位置(HOME / SELECT / NOZZLE:P / NOZZLE:R。空ならまだ・送り直す)
unsigned long lastSelectionAt = 0;
const unsigned long SELECTION_RESEND_MS = 1000;   // 変わらなくても送り直す間隔(PC が取りこぼしても 1 秒で揃う)

// 道ごとの受信途中の行。
struct LineReader {
  char line[256];
  size_t length = 0;
  bool overflow = false;
};
LineReader usbReader, wifiReader;

// 厚さのタブ(0=0.2・0.3 台、1=0.4 台、2=0.5 台以上)。
int spacerTabOf(int index) {
  char c = SPACER_THICKNESSES[index][2];   // "0.450" の小数 1 桁目
  return c <= '3' ? 0 : c == '4' ? 1 : 2;
}

// ---- 文言 ---------------------------------------------------------------
// PC の台帳・ログと同じ言葉で、送った行を人に見せる名前にする。
String describe(const String& line) {
  // 版 5: 後ろの :MOVED は印。外した残りを名前にして、印を書き足す。
  if (line.startsWith("ACT:") && line.endsWith(MOVED_SUFFIX))
    return describe(line.substring(0, line.length() - strlen(MOVED_SUFFIX))) + "・測定中に動かした";
  if (line == "DONE") return "完了";
  if (line == "UNDO") return "取消";
  if (line == "ACT:MAG") return "着磁";
  if (line == "ACT:DEMAG:FULL") return "減磁（しっかり）";
  if (line == "ACT:DEMAG:TAP") return "減磁（ちょんちょん）";
  if (line == "VALVE") return "バルブを動かす";
  if (line == "REMEASURE") return "測り直す";
  if (line == "STOP") return "測定停止";
  if (line.startsWith("PHASE:") && line.length() == 9) {
    // PHASE:PC1 → 「PC 位相1」
    return line.substring(6, 8) + " 位相" + line.charAt(8);
  }
  if (line.startsWith("ACT:NOZZLE:")) {
    // ACT:NOZZLE:<P|R>:<START|DONE|CANCEL>(版 6)
    char s = line.charAt(11);
    if (line.endsWith(":DONE")) return String(s) + " 側の立ち上がりを合わせた";
    if (line.endsWith(":CANCEL")) return String("ノズル ") + s + " 側（戻る）";
    return String("ノズル ") + s + " 側（合わせ始め）";
  }
  if (line.startsWith("ACT:SPACER:")) {
    // ACT:SPACER:<P|R>:<UP|DOWN>:<厚さ>:<差>
    char s = line.charAt(11);
    bool up = line.indexOf(":UP:") > 0;
    int last = line.lastIndexOf(':');
    int prev = line.lastIndexOf(':', last - 1);
    String thickness = line.substring(prev + 1, last);
    String diff = line.substring(last + 1);
    String diffText = diff == "NA" ? String("差未測定") : String("差") + diff + "μm";
    return String("スペーサー ") + s + " 側（" + (up ? "上げる" : "下げる") + "・" + thickness + "・" + diffText + "）";
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

// 返事(ACK)を下の帯に出さない行(版 6)。測っている位相は枠で分かり、画面も移るので、知らせは要らない。
bool quietReply(const String& line) {
  return line.startsWith("PHASE:") || line == "VALVE" || line == "REMEASURE" || line == "STOP";
}

// 行を送って返事を待つ。next: 送ったあとに出す画面。送れなければ画面は変えずに知らせる。
void sendCommand(const String& line, Screen next) {
  pressedIndex = -1;
  if (currentPath == PATH_NONE || !pathAlive(currentPath) || !writeLine(currentPath, line.c_str())) {
    setMessage(MSG_ERROR, "送れませんでした：" + describe(line),
               "USB ケーブル・PC の Wi-Fi・PC のアプリを確かめて、押し直してください");
    return;
  }
  screen = next;
  pendingLine = line;
  pendingAt = millis();
  if (line.startsWith("ACT:")) movedMark = false;   // 印は送れた操作 1 件で使い切る(版 5)
  if (quietReply(line)) setMessage(MSG_INFO, "", "");
  else setMessage(MSG_INFO, "送りました：" + describe(line), "PC の返事を待っています…");
}

// 操作の行を送る。「測定中に動かした」の印が入っていれば、後ろに :MOVED を付ける(版 5)。
void sendOperation(const String& line, Screen next) {
  sendCommand(movedMark ? line + MOVED_SUFFIX : line, next);
}

// 今の画面の位置を PC に知らせる行(版 4・6)。
const char* screenLine() {
  if (screen == HOME) return "HOME";
  if (screen == NOZZLE) return side == 'P' ? "NOZZLE:P" : "NOZZLE:R";
  return "SELECT";
}

// 画面の位置を PC に知らせる(版 4・6)。操作を選んでいる途中なら SELECT、1 画面目なら HOME、ノズルの画面なら NOZZLE:P / NOZZLE:R。
// 変わったときと、1 秒ごとに送る。PC は SELECT の間、測定を始めない。送れなければ次の周回でまた送る。
void reportSelection(unsigned long now) {
  if (currentPath == PATH_NONE || !pathAlive(currentPath)) return;
  String line = screenLine();
  if (line == reportedScreen && now - lastSelectionAt < SELECTION_RESEND_MS) return;
  if (!writeLine(currentPath, line.c_str())) return;
  reportedScreen = line;
  lastSelectionAt = now;
}

// 発振器のボタンの名前を電流から作る(版 7。例 "PC\n+25.5mA↑"。0 には符号を付けない)。
void updatePhaseLabels() {
  for (int i = 0; i < PHASE_COUNT; ++i) {
    int tenths = phaseTenthsMilliamps[i];
    int size = abs(tenths);
    const char* sign = tenths > 0 ? "+" : tenths < 0 ? "-" : "";
    snprintf(phaseLabels[i], sizeof(phaseLabels[i]), "%.2s\n%s%d.%dmA%s", PHASE_CODES[i], sign, size / 10, size % 10,
             phaseRising[i] ? "↑" : "↓");
  }
}

// STATE: の電流の 1 つ(版 7。例 255U / -238D / 0U)を読む。形が違えば false。
bool parsePhaseCurrent(const String& text, int& tenths, bool& rising) {
  int n = text.length();
  if (n < 2) return false;
  char direction = text[n - 1];
  if (direction != 'U' && direction != 'D') return false;
  String number = text.substring(0, n - 1);
  for (int i = 0; i < (int)number.length(); ++i)
    if (!isDigit(number[i]) && !(i == 0 && number[i] == '-')) return false;
  if (number.length() == 0 || number == "-") return false;
  tenths = number.toInt();
  rising = direction == 'U';
  return true;
}

// PC の今の状態(版 6。版 7 で位相の所が電流になった)。STATE:<L|N>:<PC1 など / ->:<0|1>:<電流 4 つ>。形が違えば何もしない。
void handleState(const String& line) {
  String rest = line.substring(6);
  String parts[7];
  int count = 0;
  while (count < 7) {
    int colon = rest.indexOf(':');
    if (colon < 0) { parts[count++] = rest; break; }
    parts[count++] = rest.substring(0, colon);
    rest = rest.substring(colon + 1);
  }
  if (count != 7 || parts[0].length() != 1 || (parts[0][0] != 'L' && parts[0][0] != 'N')) return;
  int measuring = -1;
  for (int i = 0; i < PHASE_COUNT; ++i) if (parts[1] == PHASE_CODES[i]) measuring = i;
  bool busy = parts[2] == "1";
  bool measuringAny = parts[1] != "-";
  int tenths[PHASE_COUNT];
  bool rising[PHASE_COUNT];
  for (int i = 0; i < PHASE_COUNT; ++i)
    if (!parsePhaseCurrent(parts[3 + i], tenths[i], rising[i])) return;
  bool changed = panelMode != parts[0][0] || measuring != measuringPhase || measuringAny != pcMeasuring || busy != pcBusy;
  for (int i = 0; i < PHASE_COUNT; ++i)
    changed = changed || tenths[i] != phaseTenthsMilliamps[i] || rising[i] != phaseRising[i];
  if (!changed) return;
  // 通常モードになったら、操作の画面などにいても 1 画面目へ(通常モードの画面は 1 画面目だけ)。
  if (parts[0][0] == 'N' && panelMode != 'N') screen = HOME;
  panelMode = parts[0][0];
  measuringPhase = measuring;
  pcMeasuring = measuringAny;
  pcBusy = busy;
  for (int i = 0; i < PHASE_COUNT; ++i) {
    phaseTenthsMilliamps[i] = tenths[i];
    phaseRising[i] = rising[i];
  }
  updatePhaseLabels();
  dirty = true;
}

// ---- 電池 ---------------------------------------------------------------
// 残量は M5Unified が電池(2 セル)の電圧から出した値。電池が付いていないときは -1。
void readBattery() {
  int level = -1, millivolts = 0;
  bool charging = false;
  int raw = M5.Power.getBatteryVoltage();
  // 2 セルの電池ではありえない電圧(4.0〜6.0V)は読み違いとして捨て、前の値のままにする(版 7、2026-11-27)。
  // 実機のログで、8.39V(100%)と 4.03〜4.07V(0%)が 30 秒ごとに入れ替わり、画面の残量が 0% と 100% を行き来していた
  // (4.0V 台は 8.4V の約半分。2 セルの電池は空でも約 6V より下がらないので、4.0V 台は読み違い)。
  if (raw >= BATTERY_MIN_PRESENT_MV && raw < BATTERY_MIN_PLAUSIBLE_MV) return;
  // 電池を付けていない Tab5 は「付いている・約 1100mV・残量 0」と読めた(2026-11-13 実機)。2 セルの電池は空でも
  // BATTERY_MIN_PRESENT_MV を下回らないので、それ未満は電池なし(-1)にする(2026-11-17)。
  if (M5.Power.getBatteryPresence() != m5::battery_presence_t::absent && raw >= BATTERY_MIN_PRESENT_MV) {
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
      // つながったときは下の帯に何も出さない(2026-11-25、人の指示)。つながっていることは上の帯で分かる。
      setMessage(MSG_INFO, "", "");
    }
    sendBattery(from);
    if (from == PATH_USB) {
      // Wi-Fi の切り分け用(2026-11-17)。PC は知らない行としてログに残すだけ。
      // 親機を始められたか / C6 の電源の切る・入れるの書き込みと読み返し / 始めるのにかかった ms / 起動からの ms
      char diag[96];
      snprintf(diag, sizeof(diag), "DIAG_WIFI:started=%d:pwr=%d%d%d:start_ms=%lu:up_ms=%lu",
               wifiStarted ? 1 : 0, wifiPowerOffOk ? 1 : 0, wifiPowerOnOk ? 1 : 0, wifiPowerReadback ? 1 : 0,
               wifiStartMs, millis());
      writeLine(from, diag);
    }
    reportedScreen = "";      // 確認のあとに今の画面の位置(HOME / SELECT / NOZZLE:)を送り直す
    dirty = true;
    return;
  }
  // 返事は今の道からだけ受け取る。
  if (from != currentPath) return;
  if (line.startsWith("LAST:")) {
    // 直前の記録(版 5)。LAST:M:<説明> / LAST:O:<説明> / LAST:。取消の確認を出しているときだけ描き直す。
    char kind = 0;
    String text;
    if (line.length() >= 7 && line.charAt(6) == ':' && (line.charAt(5) == 'M' || line.charAt(5) == 'O')) {
      kind = line.charAt(5);
      text = line.substring(7);
    }
    bool changed = !lastRecordKnown || kind != lastRecordKind || text != lastRecordText;
    lastRecordKnown = true;
    lastRecordKind = kind;
    lastRecordText = text;
    if (changed && screen == UNDO_CONFIRM) dirty = true;
    return;
  }
  if (line.startsWith("STATE:")) {
    handleState(line);
    return;
  }
  if (line.startsWith("GOTO:NOZZLE:") && line.length() == 13 && (line.charAt(12) == 'P' || line.charAt(12) == 'R')) {
    // 「合わせた」を取り消したので、合わせている画面へ戻る(版 6)。
    side = line.charAt(12);
    screen = NOZZLE;
    dirty = true;
    return;
  }
  if (line.startsWith("MSG:") && line.length() >= 6 && line.charAt(5) == ':') {
    // 後から分かった知らせ(版 6。測り始められなかった、など)。
    char kind = line.charAt(4);
    setMessage(kind == 'E' ? MSG_ERROR : kind == 'W' ? MSG_WARN : MSG_INFO, line.substring(6), "");
    return;
  }
  if (line.startsWith("ACK:")) {
    String target = line.substring(4);
    if (target == pendingLine) pendingLine = "";
    if (quietReply(target)) {
      setMessage(MSG_INFO, "", "");
      return;
    }
    setMessage(MSG_OK, (target.startsWith("ACT:NOZZLE:") && target.endsWith(":CANCEL") ? "取り消しました：" : "記録しました：") + describe(target), "");
    return;
  }
  if (line.startsWith("NAK:")) {
    String target = pendingLine;
    pendingLine = "";
    // 合わせ始めを断られたら、操作の画面へ戻す(合わせている画面のまま測らないように。版 6)。
    if (target.startsWith("ACT:NOZZLE:") && target.indexOf(":START") > 0 && screen == NOZZLE) screen = OPS;
    // 断られた理由を大きく出す(版 7、2026-11-27 人の指摘。「記録できませんでした：ノズル P 側（合わせ始め）」のように
    // 押したボタンを大きく出しても作業者には関係なく、なぜできないか(シリアル番号が入っていない、など)を伝えたい)。
    // 理由に「（…）」があれば、前を題名、括弧の中を下の行(どうすればよいか)に出す。押したボタンの名前は出さない。
    String reason = line.substring(4);
    String detail = "";
    int open = reason.indexOf("（");
    if (open > 0) {
      int close = reason.lastIndexOf("）");
      detail = close > open ? reason.substring(open + strlen("（"), close) : reason.substring(open + strlen("（"));
      reason = reason.substring(0, open);
    }
    if (reason.endsWith("。")) reason = reason.substring(0, reason.length() - strlen("。"));
    if (reason.length() == 0) reason = target.length() ? "受け付けられませんでした" : "記録できませんでした";
    setMessage(MSG_ERROR, reason, detail);
    return;
  }
  // それ以外(Arduino 向けの AS100201_PING など)は無視する。
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

// 受けた TCP 接続に keepalive を付ける(WIFI_KEEPALIVE_* の説明)。付けられなくても接続は使う(今までの動きになるだけ)。
void enableKeepAlive(WiFiClient& client) {
  int fd = client.fd();
  if (fd < 0) return;
  int on = 1, idle = WIFI_KEEPALIVE_IDLE_S, interval = WIFI_KEEPALIVE_INTERVAL_S, count = WIFI_KEEPALIVE_COUNT;
  setsockopt(fd, SOL_SOCKET, SO_KEEPALIVE, &on, sizeof(on));
  setsockopt(fd, IPPROTO_TCP, TCP_KEEPIDLE, &idle, sizeof(idle));
  setsockopt(fd, IPPROTO_TCP, TCP_KEEPINTVL, &interval, sizeof(interval));
  setsockopt(fd, IPPROTO_TCP, TCP_KEEPCNT, &count, sizeof(count));
}

// 新しい TCP 接続を受け、届いた分を読む。新しい接続が来たら古い方を閉じる
// (PC が Wi-Fi の途切れのあとにつなぎ直したとき、古い接続が残っていることがある)。
void readWifi() {
  if (!wifiStarted) return;
  // 子機が離れて、親機につながる子機が 1 台もなくなったら、TCP の知らせを待たずに閉じる。
  if (stationLeft) {
    stationLeft = false;
    if (wifiClient && WiFi.softAPgetStationNum() == 0) dropWifiClient();
  }
  WiFiClient incoming = server.accept();
  if (incoming) {
    dropWifiClient();
    wifiClient = incoming;
    wifiClient.setNoDelay(true);
    enableKeepAlive(wifiClient);
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
      : "PC の Wi-Fi 子機が抜けていないか・距離を確かめてください（USB ケーブルを挿せば USB でつながります）";
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

// 下の帯(知らせ)を出すか。スペーサーの厚さのページは厚さが多くて窮屈なので出さず、その高さを厚さのボタンに使う
// (2026-11-25、人の指示)。このページのボタン(タブ・厚さ)は PC に何も送らないので、知らせは次の差のページで見られる。
bool footerShown() {
  return screen != SPACER_PICK;
}

void addButton(int x, int y, int w, int h, ButtonId id, const char* label, uint16_t color, bool selected = false, int value = -1) {
  if (buttonCount >= MAX_BUTTONS) return;
  buttons[buttonCount++] = {(int16_t)x, (int16_t)y, (int16_t)w, (int16_t)h, id, label, color, selected, (int8_t)value};
}

// 発振器の 4 つを横に並べる(版 6)。PC は青、CR は紫(仮。操作の青と同じ画面には出ない)。
void addPhaseButtons(int x, int y, int w, int h) {
  int cw = (w - GAP * (PHASE_COUNT - 1)) / PHASE_COUNT;
  for (int i = 0; i < PHASE_COUNT; ++i)
    addButton(x + (cw + GAP) * i, y, cw, h, B_PHASE, phaseLabels[i], i < 2 ? C_PC : C_CR, false, i);
}

// 画面ごとのボタンを並べる。左に選ぶもの、右の列に完了・取消(1 画面目)または戻る(ほかの画面)。
void layoutButtons() {
  buttonCount = 0;
  const int W = M5.Display.width();
  const int H = M5.Display.height();
  const int top = HEADER_H + GAP;
  const int bottom = footerShown() ? H - FOOTER_H - GAP : H - GAP;
  const int rightW = 300;
  const int rightX = W - GAP - rightW;
  const int leftW = rightX - GAP - GAP;   // 左の領域の幅

  if (screen == HOME && panelMode == 'N') {
    // 通常モード(版 6): 上の段に発振器の 4 つ、下の段に横長の「測定停止」。完了・取消・バルブを動かすは置かない。
    int ch = (bottom - top - GAP) / 2;
    addPhaseButtons(GAP, top, W - GAP * 2, ch);
    addButton(GAP, top + ch + GAP, W - GAP * 2, ch, B_STOP, "測定停止", C_YES);
    return;
  }
  if (screen == HOME) {
    // 学習モードの 1 画面目(版 6): 上の段に発振器の 4 つ、下の段に横長の「バルブを動かす」。右の列は上が完了、下が取消。
    // 版 7: 下の段の右に「測定停止」(測っている間だけ押せる。止めて保存し、1 画面目のまま。2026-11-27 人の決定)。
    int ch = (bottom - top - GAP) / 2;
    addPhaseButtons(GAP, top, leftW, ch);
    const int stopW = 280;
    addButton(GAP, top + ch + GAP, leftW - stopW - GAP, ch, B_VALVE, "バルブを動かす", C_VALVE);
    addButton(GAP + leftW - stopW, top + ch + GAP, stopW, ch, B_STOP, "測定停止", C_YES);
    addButton(rightX, top, rightW, ch, B_DONE, "完了", C_DONE);
    addButton(rightX, top + ch + GAP, rightW, ch, B_UNDO, "取消", C_UNDO);
    return;
  }

  const int titleH = 70;
  const int contentTop = top + titleH;

  if (screen == OPS) {
    // 操作の画面(版 6): 左の 3×2 は版 5 までの 1 画面目と同じ並び。右の列は上が戻る、下が「測定中に動かした」の印。
    int cw = (leftW - GAP * 2) / 3;
    int ch = (bottom - contentTop - GAP) / 2;
    int y2 = contentTop + ch + GAP;
    addButton(GAP, contentTop, cw, ch, B_MAG, "着磁", C_OP);
    addButton(GAP, y2, cw, ch, B_DEMAG, "減磁", C_OP);
    addButton(GAP + cw + GAP, contentTop, cw, ch, B_NOZ_P, "ノズル\nP 側", C_OP);
    addButton(GAP + cw + GAP, y2, cw, ch, B_NOZ_R, "ノズル\nR 側", C_OP);
    addButton(GAP + (cw + GAP) * 2, contentTop, cw, ch, B_SPC_P, "スペーサー\nP 側", C_OP);
    addButton(GAP + (cw + GAP) * 2, y2, cw, ch, B_SPC_R, "スペーサー\nR 側", C_OP);
    int backH = (bottom - top - GAP) * 2 / 3;
    addButton(rightX, top, rightW, backH, B_BACK, "戻る", C_BACK);
    addButton(rightX, top + backH + GAP, rightW, bottom - top - backH - GAP, B_MOVED, "測定中に\n動かした", C_UNSELECTED, movedMark);
    return;
  }

  addButton(rightX, top, rightW, bottom - top, B_BACK, "戻る", C_BACK);

  if (screen == NOZZLE) {
    // ノズルの画面(版 6): 上の段に発振器の 4 つ、下の段に横長の「合わせた」と「測り直す」。取消は置かない。
    int ch = (bottom - contentTop - GAP) / 2;
    int y2 = contentTop + ch + GAP;
    addPhaseButtons(GAP, contentTop, leftW, ch);
    int cw = (leftW - GAP * (PHASE_COUNT - 1)) / PHASE_COUNT;
    int alignedW = cw * 3 + GAP * 2;
    addButton(GAP, y2, alignedW, ch, B_ALIGNED, side == 'P' ? "P 側の立ち上がりを\n合わせた" : "R 側の立ち上がりを\n合わせた", C_DONE);
    addButton(GAP + alignedW + GAP, y2, leftW - alignedW - GAP, ch, B_REMEASURE, "測り直す", C_BACK);
  } else if (screen == DEMAG || screen == SPACER) {
    int cw = (leftW - GAP) / 2;
    int h = bottom - contentTop;
    if (screen == DEMAG) {
      addButton(GAP, contentTop, cw, h, B_FULL, "しっかり", C_OP);
      addButton(GAP + cw + GAP, contentTop, cw, h, B_TAP, "ちょんちょん", C_OP);
    } else {
      // 向きを選ぶと、厚さのページへ進む(まだ記録しない)。
      addButton(GAP, contentTop, cw, h, B_UP, "上げる", C_OP);
      addButton(GAP + cw + GAP, contentTop, cw, h, B_DOWN, "下げる", C_OP);
    }
  } else if (screen == SPACER_PICK) {
    // 上段: タブ(0.2・0.3 台 / 0.4 台 / 0.5 台〜)。下段: そのタブの厚さ(選ぶと差のページへ)。前回選んだ厚さは色を変える。
    // 題名は出さず(2026-11-19、人の決定)、タブを題名の位置から始めて厚さのボタンを高くする。
    const int tabH = 80;
    int tabW = (leftW - GAP * (SPACER_TABS - 1)) / SPACER_TABS;
    for (int t = 0; t < SPACER_TABS; ++t)
      addButton(GAP + (tabW + GAP) * t, top, tabW, tabH, B_TAB, SPACER_TAB_LABELS[t], C_UNSELECTED, t == spacerTab, t);
    int count = 0;
    for (int i = 0; i < SPACER_COUNT; ++i) if (spacerTabOf(i) == spacerTab) ++count;
    const int gridGap = 12;
    int rows = (count + SPACER_COLUMNS - 1) / SPACER_COLUMNS;
    if (rows < 1) rows = 1;
    int gridTop = top + tabH + GAP;
    int cellW = (leftW - gridGap * (SPACER_COLUMNS - 1)) / SPACER_COLUMNS;
    int cellH = (bottom - gridTop - gridGap * (rows - 1)) / rows;
    int n = 0;
    for (int i = 0; i < SPACER_COUNT; ++i) {
      if (spacerTabOf(i) != spacerTab) continue;
      int col = n % SPACER_COLUMNS, row = n / SPACER_COLUMNS;
      addButton(GAP + (cellW + gridGap) * col, gridTop + (cellH + gridGap) * row, cellW, cellH,
                B_THICK, SPACER_THICKNESSES[i], C_OP, i == spacerIndex, i);
      ++n;
    }
  } else if (screen == SPACER_DIFF) {
    // 上段: 表示との差(μm)。下段: 測っていない。どれも押すと記録。
    const int gridGap = 12;
    int cw = (leftW - gridGap * (SPACER_DIFF_COUNT - 1)) / SPACER_DIFF_COUNT;
    int h = 170;
    for (int d = 0; d < SPACER_DIFF_COUNT; ++d)
      addButton(GAP + (cw + gridGap) * d, contentTop + 40, cw, h, B_DIFF, SPACER_DIFFS[d], C_OP, false, d);
    int naTop = contentTop + 40 + h + GAP;
    addButton(GAP, naTop, leftW, bottom - naTop, B_NOT_MEASURED, "測っていない", C_BACK);
  } else if (screen == UNDO_CONFIRM) {
    int cw = (leftW - GAP) / 2;
    int h = 220;
    addButton(GAP, bottom - h, cw, h, B_YES, lastRecordKind == 'M' ? "はい、使わない" : "はい、取り消す", C_YES);
    addButton(GAP + cw + GAP, bottom - h, cw, h, B_NO, "いいえ", C_BACK);
  }
}

// 押せるのは、PC の確認を受けていて、返事待ちでも PC の処理中でもないときだけ。戻る・いいえ・スペーサーの向き・タブ・厚さ・印は
// 送らない(次のページへ進むだけ)のでいつでも押せる。ただしノズルの画面の「戻る」は合わせ始めを取り消す行を送る(版 6)。
bool buttonEnabled(const Button& b) {
  bool sends = !(b.id == B_NO || b.id == B_UP || b.id == B_DOWN || b.id == B_TAB || b.id == B_THICK || b.id == B_MOVED ||
                 (b.id == B_BACK && screen != NOZZLE));
  if (!sends) return true;
  // 取消の「はい」は、PC から今の取消の対象が届いていて、取り消せるものがあるときだけ(版 5)。
  if (b.id == B_YES && (!lastRecordKnown || lastRecordKind == 0)) return false;
  // ノズルの画面では、合わせている側でない方の発振器は押し間違いなので押せない(版 6、人の決定)。
  if (b.id == B_PHASE && screen == NOZZLE && (b.value < 2) != (side == 'P')) return false;
  // 学習モードの「測定停止」は測っている間だけ(版 7)。通常モードはいつでも押せる(版 6 のまま)。
  if (b.id == B_STOP && panelMode == 'L' && !pcMeasuring) return false;
  return currentPath != PATH_NONE && pathAlive(currentPath) && pendingLine.length() == 0 && !pcBusy;
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
  bool pressed = (&b - buttons) == pressedIndex;   // b は buttons の要素
  uint16_t bg = !enabled ? C_DISABLED : pressed ? C_PRESSED : b.selected ? C_SELECTED : b.color;
  uint16_t fg = !enabled ? C_DISABLED_TEXT : (pressed || b.selected) ? TFT_BLACK : C_TEXT;
  M5.Display.fillRoundRect(b.x, b.y, b.w, b.h, 18, bg);
  if (b.selected) M5.Display.drawRoundRect(b.x, b.y, b.w, b.h, 18, TFT_WHITE);
  M5.Display.setFont(&fonts::lgfxJapanGothicP_40);
  // 厚さのボタンは、下の帯をなくして高くなった分だけ数字も大きくする(2026-11-25)。
  float textSize = b.h >= 200 || (b.id == B_THICK && b.h >= 110) ? 1.25f : 1.0f;
  // 発振器のボタンは電流の文字(例 "+25.5mA↑")が長いので、ボタンの幅に収まるまで小さくする(版 7)。
  // 4 つとも同じ大きさにする(短い "0.0mA↑" だけ大きく見えたため。2026-11-27 人の指摘)。いちばん長い名前で決める。
  if (b.id == B_PHASE) {
    M5.Display.setTextSize(1.0f);
    int widest = 0;
    for (int i = 0; i < PHASE_COUNT; ++i) {
      const char* label = phaseLabels[i];
      const char* nl = strchr(label, '\n');
      int w = nl ? max(M5.Display.textWidth(String(label).substring(0, nl - label)), M5.Display.textWidth(nl + 1))
                 : M5.Display.textWidth(label);
      widest = max(widest, w);
    }
    int room = b.w - 24;
    if (widest > 0 && widest * textSize > room) textSize = (float)room / widest;
  }
  M5.Display.setTextSize(textSize);
  bool measuring = b.id == B_PHASE && b.value == measuringPhase;
  drawLabel(b.label, b.x + b.w / 2, b.y + b.h / 2 - (measuring ? 18 : 0), fg);
  M5.Display.setTextSize(1.0f);
  if (measuring) {
    // 測っている位相(版 6): 黄色の太い枠と「測定中」。
    for (int i = 0; i < 6; ++i) M5.Display.drawRoundRect(b.x + i, b.y + i, b.w - i * 2, b.h - i * 2, 18 - i, C_SELECTED);
    M5.Display.setFont(&fonts::lgfxJapanGothicP_32);
    M5.Display.setTextColor(C_SELECTED);
    M5.Display.setTextDatum(bottom_center);
    M5.Display.drawString("測定中", b.x + b.w / 2, b.y + b.h - 14);
  }
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
  if (messageTitle.length() == 0) return;   // ふだん(つながっていて知らせがない)は文字を出さない
  // 2026-11-25(人の指示): 文字を大きくした。前は 1 行のとき 40、2 行のとき 36 と 28。
  // 帯の幅に入らない長い知らせは、入る大きさまで小さくする。
  const int maxW = W - GAP * 2;
  if (messageDetail.length() == 0) {
    drawFittedString(messageTitle, &fonts::lgfxJapanGothicP_40, 1.25f, GAP, H - FOOTER_H / 2, maxW);
    return;
  }
  drawFittedString(messageTitle, &fonts::lgfxJapanGothicP_40, 1.0f, GAP, H - FOOTER_H + 32, maxW);
  drawFittedString(messageDetail, &fonts::lgfxJapanGothicP_32, 1.0f, GAP, H - FOOTER_H + 82, maxW);
}

// 左寄せで 1 行書く。maxW に入らなければ入る大きさまで縮める(読めなくならないよう 0.6 倍までに留める)。
void drawFittedString(const String& text, const lgfx::IFont* font, float scale, int x, int y, int maxW) {
  M5.Display.setFont(font);
  M5.Display.setTextSize(1.0f);
  int width = M5.Display.textWidth(text.c_str());
  if (width > 0 && width * scale > maxW) scale = (float)maxW / width;
  if (scale < 0.6f) scale = 0.6f;
  M5.Display.setTextSize(scale);
  M5.Display.drawString(text, x, y);
  M5.Display.setTextSize(1.0f);
}

void drawTitle(const String& text) {
  M5.Display.setFont(&fonts::lgfxJapanGothicP_36);
  M5.Display.setTextSize(1.0f);
  M5.Display.setTextColor(C_TEXT);
  M5.Display.setTextDatum(middle_left);
  // 右の列(戻る)の下に隠れないよう、左の領域の幅に入らなければ縮める(版 5 で印の文字が付いて長くなるため)。
  const int leftW = M5.Display.width() - GAP - 300 - GAP - GAP;
  drawFittedString(text, &fonts::lgfxJapanGothicP_36, 1.0f, GAP, HEADER_H + GAP + 30, leftW);
}

void drawScreen() {
  layoutButtons();
  M5.Display.startWrite();
  M5.Display.fillScreen(C_BG);
  drawHeader();

  // 「測定中に動かした」の印が入っていれば、操作を選ぶページの題名に出す(版 5)。
  String moved = movedMark ? "（測定中に動かした）" : "";
  // 版 7: 印が入っているときは、何を選ぶのかを題名で言う。
  if (screen == OPS) drawTitle(movedMark ? String("測定中にしてしまった操作を選んでください") : String("バルブを動かす：操作を選んでください"));
  else if (screen == DEMAG) drawTitle("減磁：程度を押すと記録します" + moved);
  else if (screen == SPACER) drawTitle(String("スペーサー ") + side + " 側：向きを選んでください" + moved);
  else if (screen == NOZZLE) drawTitle(String(side) + " 側の立ち上がりを合わせています");
  else if (screen == SPACER_DIFF) {   // SPACER_PICK は題名を出さない(2026-11-19、人の決定)
    drawTitle(String("スペーサー ") + side + " 側・" + (spacerUp ? "上げる" : "下げる") + "・" +
              (spacerIndex >= 0 ? SPACER_THICKNESSES[spacerIndex] : "?") + moved);
    M5.Display.setFont(&fonts::lgfxJapanGothicP_32);
    M5.Display.setTextColor(C_SUBTEXT);
    M5.Display.setTextDatum(middle_left);
    M5.Display.drawString("表示との差（μm）を押すと記録します", GAP, HEADER_H + GAP + 30 + 60);
  } else if (screen == UNDO_CONFIRM) {
    drawTitle("取消");
    M5.Display.setFont(&fonts::lgfxJapanGothicP_40);
    M5.Display.setTextColor(C_TEXT);
    M5.Display.setTextDatum(middle_left);
    int y = HEADER_H + GAP + 110;
    // 何が消えるかは PC が LAST: で知らせる(版 5)。届くまでは「はい」を押せない。
    if (!lastRecordKnown) {
      M5.Display.drawString("PC に直前の記録を問い合わせています…", GAP, y);
    } else if (lastRecordKind == 'M') {
      M5.Display.drawString("直前の測定を使わないにしますか", GAP, y);
      M5.Display.drawString("「" + lastRecordText + "」", GAP, y + 70);
      M5.Display.setFont(&fonts::lgfxJapanGothicP_28);
      M5.Display.setTextColor(C_SUBTEXT);
      M5.Display.drawString("（波形のファイルは残り、台帳に「使わない」と書きます）", GAP, y + 130);
    } else if (lastRecordKind == 'O') {
      M5.Display.drawString("直前の操作を取り消しますか", GAP, y);
      M5.Display.drawString("「" + lastRecordText + "」", GAP, y + 70);
    } else {
      M5.Display.drawString("取り消せる記録がありません", GAP, y);
      M5.Display.setFont(&fonts::lgfxJapanGothicP_28);
      M5.Display.setTextColor(C_SUBTEXT);
      M5.Display.drawString("（完了・シリアル番号のあとと、取り消したあとは取り消せません）", GAP, y + 70);
    }
  }

  for (int i = 0; i < buttonCount; ++i) drawButton(buttons[i]);
  if (footerShown()) drawFooter();
  M5.Display.endWrite();
  dirty = false;
}

void redrawButton(int index) {
  if (index < 0 || index >= buttonCount) return;
  M5.Display.startWrite();
  drawButton(buttons[index]);
  M5.Display.endWrite();
}

// ---- タッチ -------------------------------------------------------------
// 指の下のボタンの番号(buttons の添字)。押せないボタン・ボタンの外は -1。
int hitTest(int x, int y) {
  for (int i = 0; i < buttonCount; ++i) {
    const Button& b = buttons[i];
    if (x >= b.x && x < b.x + b.w && y >= b.y && y < b.y + b.h) return buttonEnabled(b) ? i : -1;
  }
  return -1;
}

void sendSpacer(const char* diff) {
  if (spacerIndex < 0) return;
  sendOperation(String("ACT:SPACER:") + side + ":" + (spacerUp ? "UP" : "DOWN") + ":" +
                SPACER_THICKNESSES[spacerIndex] + ":" + diff, HOME);
}

void activate(const Button& b) {
  switch (b.id) {
    // 発振器(版 6)。今の画面のまま送る(1 画面目でもノズルの画面でも同じ行。区切るかどうかは PC が決める)。
    case B_PHASE:  sendCommand(String("PHASE:") + PHASE_CODES[b.value], screen); break;
    // バルブを動かす(版 6)。測っていれば PC が止めて保存する。操作の画面へ。
    case B_VALVE:  sendCommand("VALVE", OPS); break;
    case B_STOP:   sendCommand("STOP", HOME); break;
    case B_REMEASURE: sendCommand("REMEASURE", NOZZLE); break;
    case B_ALIGNED: sendOperation(String("ACT:NOZZLE:") + side + ":DONE", HOME); break;
    case B_MAG:   sendOperation("ACT:MAG", HOME); break;
    // 測定中に動かした印の切り替え(版 5。版 6 で操作の画面へ移した)。
    // 版 7: 入れたあとに何をするかを下の帯に出す(2026-11-27 人の指摘「そのあと何をするのか分かりづらい」)。
    case B_MOVED:
      movedMark = !movedMark;
      if (movedMark)
        setMessage(MSG_WARN, "次に、測定中にしてしまった操作を選んでください",
                   "例：測定中に減磁をしたら、ここで「減磁」を選んで記録します。その記録に「測定中に動かした」が付きます");
      else
        setMessage(MSG_INFO, "「測定中に動かした」を外しました", "");
      break;
    case B_DEMAG: screen = DEMAG; break;
    // ノズルを選んだ時点で合わせ始めを記録し、合わせる画面へ(版 6)。
    case B_NOZ_P: side = 'P'; sendOperation("ACT:NOZZLE:P:START", NOZZLE); break;
    case B_NOZ_R: side = 'R'; sendOperation("ACT:NOZZLE:R:START", NOZZLE); break;
    case B_SPC_P: screen = SPACER; side = 'P'; break;
    case B_SPC_R: screen = SPACER; side = 'R'; break;
    case B_DONE:  sendCommand("DONE", HOME); break;
    // 取消の確認を開いたら、PC から今の対象が届くまで「はい」を押せなくする(古い対象を出さないため)。
    case B_UNDO:  lastRecordKnown = false; screen = UNDO_CONFIRM; break;
    case B_BACK:
      // ノズルの画面の「戻る」は押し間違い: 合わせ始めを取り消して操作の画面へ(版 6)。
      if (screen == NOZZLE) { sendCommand(String("ACT:NOZZLE:") + side + ":CANCEL", OPS); break; }
      // 厚さ・差のページは 1 つ前のページへ。減磁・スペーサーは操作の画面へ(版 6)。操作の画面は 1 画面目へ。
      screen = screen == SPACER_DIFF ? SPACER_PICK : screen == SPACER_PICK ? SPACER
             : (screen == DEMAG || screen == SPACER) ? OPS : HOME;
      break;
    case B_NO:    screen = HOME; break;
    case B_FULL:  sendOperation("ACT:DEMAG:FULL", HOME); break;
    case B_TAP:   sendOperation("ACT:DEMAG:TAP", HOME); break;
    case B_UP:
    case B_DOWN:
      spacerUp = b.id == B_UP;
      if (spacerIndex >= 0) spacerTab = spacerTabOf(spacerIndex);   // 前回の厚さのタブを開く
      screen = SPACER_PICK;
      break;
    case B_TAB:   spacerTab = b.value; break;
    case B_THICK: spacerIndex = b.value; screen = SPACER_DIFF; break;
    case B_DIFF:  sendSpacer(SPACER_DIFFS[b.value]); break;
    case B_NOT_MEASURED: sendSpacer("NA"); break;
    case B_YES:   sendCommand("UNDO", HOME); break;
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
// 版 6 で、触れても測定は止めないことにした(HOLD をなくした。止めるのは「バルブを動かす」と測定停止)。
void handleTouch(unsigned long now) {
  if (M5.Touch.getCount() == 0 && pressedIndex < 0) return;
  if (M5.Touch.getCount() > 0 && !dimmed) lastTouchAt = now;   // 押している間は暗くしない
  auto t = M5.Touch.getDetail();
  if (t.wasPressed()) {
    if (dimmed) {
      wake(now);
      return;
    }
    pressedIndex = hitTest(t.x, t.y);
    if (pressedIndex >= 0) redrawButton(pressedIndex);
  } else if (t.wasReleased()) {
    int released = hitTest(t.x, t.y);
    int pressed = pressedIndex;
    pressedIndex = -1;
    if (pressed < 0) return;
    if (released == pressed) {
      Button b = buttons[pressed];   // activate の中で並びが変わっても読めるよう写しを渡す
      activate(b);
    }
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
  // 版 6(仮。実物で見て直す): 発振器の PC は青、CR は紫、バルブを動かすは落ち着いた灰青。
  C_PC = d.color565(0x25, 0x63, 0xC9);
  C_CR = d.color565(0x7A, 0x4F, 0xC0);
  C_VALVE = d.color565(0x4B, 0x5A, 0x73);
}

// 無線用チップ ESP32-C6 の電源を切って入れ直す(WIFI_CHIP_POWER_* の説明)。WiFi を始める前に 1 回だけ呼ぶ。
// I/O 拡張チップに書けなくても止めない(Wi-Fi を始めて、だめなら「Wi-Fi なし」になるだけ。USB では使える)。
void wifiChipPowerCycle() {
  auto& expander = M5.getIOExpander(WIFI_CHIP_POWER_EXPANDER);
  expander.setDirection(WIFI_CHIP_POWER_PIN, true);   // 出力(M5GFX の設定と同じ。念のため)
  wifiPowerOffOk = expander.digitalWrite(WIFI_CHIP_POWER_PIN, false);
  delay(WIFI_CHIP_OFF_MS);
  wifiPowerOnOk = expander.digitalWrite(WIFI_CHIP_POWER_PIN, true);
  wifiPowerReadback = expander.getWriteValue(WIFI_CHIP_POWER_PIN);
  delay(WIFI_CHIP_BOOT_MS);
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
  updatePhaseLabels();
  uint8_t brightness = M5.Display.getBrightness();
  if (brightness > DIM_BRIGHTNESS) normalBrightness = brightness;

  usbConnected = (bool)Serial;
  lastUsbSeenAt = millis();
  writeUsb("DIAG_BOOT:1");
  readBattery();
  setMessage(MSG_INFO, "起動しました", "Wi-Fi を始めています…");
  drawScreen();

  // C6 を前の状態から始めさせる(本体だけが再起動したとき、C6 が前の状態のまま残らないように。2026-11-17)。
  wifiChipPowerCycle();

  // Wi-Fi の親機を始める(無線用チップ ESP32-C6 を経由。ピンはボード M5Tab5 の設定にある)。
  // 始められなくても USB では使える。
  // ゲートウェイは 0.0.0.0 にして、PC へ「この先にインターネットがある」と知らせない(DHCP でルーターを渡さない)。
  // 渡すと、PC がふだんのインターネットの通信を Tab5 へ流してしまうことがある。DNS も渡さない。
  unsigned long wifiBeginAt = millis();
  WiFi.onEvent([](arduino_event_id_t, arduino_event_info_t) { stationLeft = true; }, ARDUINO_EVENT_WIFI_AP_STADISCONNECTED);
  WiFi.mode(WIFI_AP);
  wifiStarted = WiFi.softAPConfig(PANEL_IP, IPAddress(0, 0, 0, 0), IPAddress(255, 255, 255, 0)) &&
                WiFi.softAP(WIFI_SSID, WIFI_PASSWORD);
  wifiStartMs = millis() - wifiBeginAt;
  if (wifiStarted) {
    server.begin();
    server.setNoDelay(true);
    setMessage(MSG_INFO, "起動しました", String("PC のアプリを起動すると、USB か Wi-Fi（") + WIFI_SSID + "）でつながります");
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
  reportSelection(now);

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
  // 版 6: 測っている間とノズルの画面にいる間は暗くしない(暗いときの最初のタッチは記録されないので、
  // 「バルブを動かす」「合わせた」が効かないことを避ける)。測り終わってから 60 秒で暗くする。
  if (measuringPhase >= 0 || screen == NOZZLE) {
    lastTouchAt = now;
    if (dimmed) wake(now);
  }
  if (!dimmed && pressedIndex < 0 && now - lastTouchAt >= DIM_AFTER_MS) {
    dimmed = true;
    M5.Display.setBrightness(DIM_BRIGHTNESS);
  }
  if (dirty && pressedIndex < 0) drawScreen();
  delay(5);
}
