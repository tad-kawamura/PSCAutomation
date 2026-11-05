#include <M5Unified.h>
// Basic v2.7。乾接点をG26-GND間へ接続。LOW=通電、HIGH=非通電。
// USBシリアルを使うG1/G3は外部入力に使用しない。
constexpr int NF_PIN=26;
constexpr uint32_t DEBOUNCE_MS=30, PC_TIMEOUT_MS=2500;
String receiveLine, epoch="0", token="", mode="", mask="000", serial="", labels[3]={"-","-","-"}, status="PC接続待ち";
String pressedEpoch[3],pressedToken[3]; bool armed[3]={false,false,false};
uint32_t lastUi=0,lastStatus=0,frame=0,nfChanged=0;
bool rawNf=false,stableNf=false,dirty=true;
String field(const String& value,int index) {
 int start=0;
 for(int i=0;i<index;i++){int next=value.indexOf(':',start);if(next<0)return "";start=next+1;}
 int end=value.indexOf(':',start);return end<0?value.substring(start):value.substring(start,end);
}
void disarm(){for(int i=0;i<3;i++)armed[i]=false;}
void handle(const String& line){
 if(line=="AS110_PING"){Serial.println("AS110_READY:1");return;}
 if(!line.startsWith("UI:"))return;
 String newEpoch=field(line,1),newToken=field(line,2);
 if(newEpoch!=epoch || newToken!=token)disarm();
 String old=mode+mask+serial+labels[0]+labels[1]+labels[2]+status;
 epoch=newEpoch;token=newToken;mode=field(line,3);mask=field(line,4);serial=field(line,5);
 labels[0]=field(line,6);labels[1]=field(line,7);labels[2]=field(line,8);status=field(line,9);
 lastUi=millis();
 dirty|=old!=mode+mask+serial+labels[0]+labels[1]+labels[2]+status;
}
void draw(){
 M5.Display.fillScreen(TFT_BLACK);M5.Display.setTextColor(TFT_WHITE,TFT_BLACK);
 // 日本語16pxフォントを1.125倍し、18px相当で表示する。
 M5.Display.setFont(&fonts::efontJA_16);M5.Display.setTextSize(1.125f);M5.Display.setCursor(8,8);
 M5.Display.println("AS110 / "+String(mode=="0"?"動き検査":mode=="1"?"データ調整":"データ取り"));
 if(mode!="1")M5.Display.println("S/N "+serial);
 if(mode=="0")M5.Display.println(stableNf?"外部接点：通電":"外部接点：非通電");
 M5.Display.setCursor(8,78);
 bool online=lastUi!=0 && (uint32_t)(millis()-lastUi)<PC_TIMEOUT_MS;
 M5.Display.println(online?status:"PC通信待ち：操作できません");
 M5.Display.setCursor(8,150);M5.Display.println(online && mask!="000"?"操作可能":"待機／処理中");
 for(int i=0;i<3;i++){
  int x=i*106;bool enabled=online && mask.length()==3 && mask[i]=='1';
  M5.Display.fillRect(x,192,104,48,enabled?TFT_DARKGREEN:TFT_DARKGREY);
  M5.Display.setCursor(x+3,198);M5.Display.print(String(char('A'+i))+"\n");
  // 長いボタン名は枠内に収める。通常の表示は18px相当。
  int labelWidth=M5.Display.textWidth(labels[i]);
  if(labelWidth>98)M5.Display.setTextSize(1.125f*98.0f/labelWidth);
  M5.Display.setCursor(x+3,216);M5.Display.print(labels[i]);
  M5.Display.setTextSize(1.125f);
 }
}
void button(m5::Button_Class& key,int index){
 bool online=lastUi!=0 && (uint32_t)(millis()-lastUi)<PC_TIMEOUT_MS;
 if(key.wasPressed()){
  armed[index]=online && mask.length()==3 && mask[index]=='1';
  pressedEpoch[index]=epoch;pressedToken[index]=token;
 }
 if(key.wasReleased()){
  if(armed[index] && online && pressedEpoch[index]==epoch && pressedToken[index]==token && mask[index]=='1')
   Serial.println("BUTTON:"+String(index+1)+":"+epoch+":"+token);
  armed[index]=false;
 }
}
void setup(){
 auto cfg=M5.config();M5.begin(cfg);Serial.begin(115200);
 pinMode(NF_PIN,INPUT_PULLUP);rawNf=stableNf=digitalRead(NF_PIN)==LOW;nfChanged=millis();
 M5.BtnA.setDebounceThresh(DEBOUNCE_MS);M5.BtnB.setDebounceThresh(DEBOUNCE_MS);M5.BtnC.setDebounceThresh(DEBOUNCE_MS);
 receiveLine.reserve(768);Serial.println("DIAG_BOOT:1");draw();
}
void loop(){
 M5.update();
 while(Serial.available()){
  char c=Serial.read();
  if(c=='\n'){receiveLine.trim();handle(receiveLine);receiveLine="";}
  else if(c!='\r'){if(receiveLine.length()<768)receiveLine+=c;else receiveLine="";}
 }
 bool nf=digitalRead(NF_PIN)==LOW;
 if(nf!=rawNf){rawNf=nf;nfChanged=millis();}
 if(stableNf!=rawNf && (uint32_t)(millis()-nfChanged)>=DEBOUNCE_MS){stableNf=rawNf;dirty=true;}
 if((uint32_t)(millis()-lastStatus)>=200){
  lastStatus=millis();Serial.println(stableNf?"NF:1":"NF:0");
  Serial.println("DIAG_FRAME:"+String(++frame)+":"+String(millis()));
 }
 static bool wasOnline=false;
 bool online=lastUi!=0 && (uint32_t)(millis()-lastUi)<PC_TIMEOUT_MS;
 if(online!=wasOnline){wasOnline=online;dirty=true;if(!online)disarm();}
 button(M5.BtnA,0);button(M5.BtnB,1);button(M5.BtnC,2);
 if(dirty){draw();dirty=false;}
 delay(2);
}
