/**
 * deepsleep 鸿蒙电脑版 · 壳本体
 *
 * 它做三件事：
 *   1. 探测内核（Linux 虚拟机里的 deepsleep-core）在不在
 *   2. 在就把它自带的网页界面塞进 ArkWeb —— 界面层是纯标准 Web，
 *      同一份 ui/app.js 不用改一行（core-bridge.js 已经把桌面端的
 *      chrome.webview 协议搬到了 HTTP + SSE 上）
 *   3. 不在就给一张引导页，告诉你虚拟机里的内核该怎么起
 *
 * ⚠ 为什么内核不在本进程里：鸿蒙禁 JIT、seccomp 里没有 get_mempolicy，
 *   .NET 在这儿根本起不来。详细原因见 ../README.md。
 *
 * ⚠ ArkTS 注意事项（都是硬规则，违反了 hvigor 直接报错）：
 *   · struct 里**不能定义 static 成员** —— 工具函数全部放模块级；
 *   · 组件属性链没有 .align()，Stack 的对齐用 .alignContent()；
 *   · 成员初始化器里别调 getContext(this)，挪到 aboutToAppear。
 */
import { webview } from '@kit.ArkWeb';
import { http } from '@kit.NetworkKit';
import { preferences } from '@kit.ArkData';
import { common } from '@kit.AbilityKit';
import { hilog } from '@kit.PerformanceAnalysisKit';
import { BusinessError } from '@kit.BasicServicesKit';

const DOMAIN: number = 0x0000;
const TAG: string = 'deepsleep';
const PREF_NAME: string = 'deepsleep_shell';
const DEFAULT_BASE: string = 'http://127.0.0.1:8756';

/** 补协议、去尾部斜杠 —— 用户可能只敲 "192.168.1.9:8756"。 */
function normalizeUrl(u: string): string {
  let s: string = (u || '').trim().replace(/\/+$/, '');
  if (s.length === 0) {
    return DEFAULT_BASE;
  }
  if (!/^https?:\/\//i.test(s)) {
    s = 'http://' + s;
  }
  return s;
}

/** 探测内核：/api/ping 有回应就算「内核在」（401 也算 —— 那只是令牌不对）。 */
function httpOk(url: string): Promise<boolean> {
  return new Promise<boolean>((resolve: (v: boolean) => void) => {
    const req: http.HttpRequest = http.createHttp();
    const header: Record<string, string> = { 'Cache-Control': 'no-store' };
    const opt: http.HttpRequestOptions = {
      method: http.RequestMethod.GET,
      connectTimeout: 3000,
      readTimeout: 3000,
      header: header
    };
    req.request(url, opt, (err: BusinessError, res: http.HttpResponse) => {
      const code: number = err ? 0 : (res.responseCode as number);
      resolve(code >= 200 && code < 500);
      req.destroy();
    });
  });
}

@Entry
@Component
struct Index {
  /** probing = 正在探测；online = 连上了；offline = 没找到内核 */
  @State phase: string = 'probing';
  @State baseUrl: string = DEFAULT_BASE;
  @State token: string = '';
  @State frameUrl: string = '';
  @State showSetup: boolean = false;
  @State editBase: string = '';
  @State editToken: string = '';
  @State toast: string = '';

  private controller: webview.WebviewController = new webview.WebviewController();
  private pref: preferences.Preferences | null = null;
  private ctx: common.UIAbilityContext | null = null;

  // ------------------------------------------------------------------
  // 生命周期
  // ------------------------------------------------------------------

  aboutToAppear(): void {
    this.ctx = getContext(this) as common.UIAbilityContext;
    this.boot();
  }

  private async boot(): Promise<void> {
    await this.loadPref();
    await this.probe();
  }

  // ------------------------------------------------------------------
  // 配置的读写（地址和令牌都存本机，重开不用再填）
  // ------------------------------------------------------------------

  private async loadPref(): Promise<void> {
    if (this.ctx === null) {
      return;
    }
    try {
      const opt: preferences.Options = { name: PREF_NAME };
      this.pref = await preferences.getPreferences(this.ctx, opt);
      const b: string = await this.pref.get('baseUrl', DEFAULT_BASE) as string;
      const t: string = await this.pref.get('token', '') as string;
      this.baseUrl = normalizeUrl(b);
      this.token = t;
      this.editBase = this.baseUrl;
      this.editToken = t;
    } catch (e) {
      hilog.warn(DOMAIN, TAG, 'loadPref failed: %{public}s', JSON.stringify(e));
    }
  }

  private async savePref(): Promise<void> {
    if (this.pref === null) {
      return;
    }
    try {
      await this.pref.put('baseUrl', this.baseUrl);
      await this.pref.put('token', this.token);
      await this.pref.flush();
    } catch (e) {
      hilog.warn(DOMAIN, TAG, 'savePref failed: %{public}s', JSON.stringify(e));
    }
  }

  // ------------------------------------------------------------------
  // 探测内核
  // ------------------------------------------------------------------

  private async probe(): Promise<void> {
    this.phase = 'probing';
    const url: string = this.baseUrl + '/api/ping' +
      (this.token.length > 0 ? ('?t=' + encodeURIComponent(this.token)) : '');
    try {
      const ok: boolean = await httpOk(url);
      if (ok) {
        this.phase = 'online';
        this.frameUrl = this.buildFrameUrl();
        hilog.info(DOMAIN, TAG, 'kernel online: %{public}s', this.baseUrl);
      } else {
        this.phase = 'offline';
      }
    } catch (e) {
      this.phase = 'offline';
      hilog.info(DOMAIN, TAG, 'kernel offline: %{public}s', JSON.stringify(e));
    }
  }

  /** 内核页面地址。令牌和地址都塞 hash：query 会被隧道商的警告页顶掉。 */
  private buildFrameUrl(): string {
    const u: string = this.baseUrl + '/web/core/';
    const parts: string[] = [];
    parts.push('u=' + encodeURIComponent(this.baseUrl));
    if (this.token.length > 0) {
      parts.push('t=' + encodeURIComponent(this.token));
    }
    return u + '#' + parts.join('&');
  }

  // ------------------------------------------------------------------

  private showToast(msg: string): void {
    this.toast = msg;
    setTimeout(() => {
      if (this.toast === msg) {
        this.toast = '';
      }
    }, 2600);
  }

  build() {
    // alignContent=Bottom：让提示条贴底。其余三个面板都铺满 100%，不受影响。
    Stack({ alignContent: Alignment.Bottom }) {
      if (this.phase === 'online') {
        this.OnlinePane()
      } else {
        this.StartPane()
      }

      if (this.showSetup) {
        this.SetupSheet()
      }

      if (this.toast.length > 0) {
        Text(this.toast)
          .fontSize(13)
          .fontColor('#FFFFFF')
          .backgroundColor('#CC000000')
          .borderRadius(10)
          .padding({ left: 14, right: 14, top: 9, bottom: 9 })
          .margin({ bottom: 42 })
      }
    }
    .width('100%')
    .height('100%')
    .backgroundColor('#101114')
  }

  // ==================================================================
  // 连上了：直接把内核界面铺满
  // ==================================================================
  @Builder
  OnlinePane() {
    Column() {
      Row() {
        Text('deepsleep')
          .fontSize(13)
          .fontWeight(FontWeight.Medium)
          .fontColor('#E8E8EA')
        Text('  ·  ' + this.baseUrl.replace(/^https?:\/\//, ''))
          .fontSize(11)
          .fontColor('#8A8A90')
          .layoutWeight(1)
          .maxLines(1)
          .textOverflow({ overflow: TextOverflow.Ellipsis })

        Button('刷新')
          .fontSize(12)
          .height(26)
          .backgroundColor('#2A2C31')
          .fontColor('#E8E8EA')
          .onClick(() => {
            this.probe();
            this.showToast('已重新加载');
          })
        Button('设置')
          .fontSize(12)
          .height(26)
          .margin({ left: 8 })
          .backgroundColor('#2A2C31')
          .fontColor('#E8E8EA')
          .onClick(() => {
            this.editBase = this.baseUrl;
            this.editToken = this.token;
            this.showSetup = true;
          })
      }
      .width('100%')
      .height(40)
      .padding({ left: 12, right: 12 })
      .backgroundColor('#17181C')

      Web({ src: this.frameUrl, controller: this.controller })
        .javaScriptAccess(true)
        // 必须开：core-bridge 用 localStorage 记住地址和令牌
        .domStorageAccess(true)
        .databaseAccess(true)
        .imageAccess(true)
        .fileAccess(false)
        .zoomAccess(true)
        .width('100%')
        .layoutWeight(1)
        .onPageEnd(() => {
          hilog.info(DOMAIN, TAG, '%{public}s', 'kernel page loaded');
        })
        .onErrorReceive(() => {
          this.showToast('内核连接断开，点「刷新」重试');
        })
    }
    .width('100%')
    .height('100%')
  }

  // ==================================================================
  // 没连上：引导页
  // ==================================================================
  @Builder
  StartPane() {
    Column() {
      Column() {
        Text('deepsleep')
          .fontSize(34)
          .fontWeight(FontWeight.Bold)
          .fontColor('#F2F2F4')
        Text('鸿蒙电脑版')
          .fontSize(14)
          .fontColor('#8A8A90')
          .margin({ top: 6 })

        if (this.phase === 'probing') {
          Row() {
            LoadingProgress().width(18).height(18).color('#5B8DEF')
            Text('正在找内核…').fontSize(13).fontColor('#B9B9C0').margin({ left: 8 })
          }
          .margin({ top: 34 })
        } else {
          Text('没有找到内核')
            .fontSize(16)
            .fontColor('#FF7A6B')
            .margin({ top: 30 })
          Text('内核跑在 Linux 虚拟机里（鸿蒙上没法直接跑 .NET）。\n先在虚拟机里把它起起来，再回来点「重试」。')
            .fontSize(13)
            .fontColor('#9A9AA2')
            .textAlign(TextAlign.Center)
            .lineHeight(21)
            .margin({ top: 12 })

          Column() {
            Text('在虚拟机（融合开发引擎 / 终端）里执行：')
              .fontSize(12)
              .fontColor('#8A8A90')
              .margin({ bottom: 8 })
            Text('./install.sh\n~/.local/share/deepsleep/deepsleep-core.sh start')
              .fontSize(12)
              .fontFamily('monospace')
              .fontColor('#C8E1FF')
              .backgroundColor('#1B1D22')
              .borderRadius(8)
              .padding(12)
              .width('100%')
              .textAlign(TextAlign.Start)
          }
          .alignItems(HorizontalAlign.Start)
          .width('100%')
          .constraintSize({ maxWidth: 420 })
          .margin({ top: 24 })

          Row() {
            Button('重试')
              .fontSize(14)
              .height(38)
              .layoutWeight(1)
              .backgroundColor('#5B8DEF')
              .fontColor('#FFFFFF')
              .onClick(() => {
                this.probe();
              })
            Button('改地址')
              .fontSize(14)
              .height(38)
              .layoutWeight(1)
              .margin({ left: 10 })
              .backgroundColor('#2A2C31')
              .fontColor('#E8E8EA')
              .onClick(() => {
                this.editBase = this.baseUrl;
                this.editToken = this.token;
                this.showSetup = true;
              })
          }
          .width('100%')
          .constraintSize({ maxWidth: 420 })
          .margin({ top: 22 })

          Text('当前尝试：' + this.baseUrl)
            .fontSize(11)
            .fontColor('#6E6E76')
            .margin({ top: 18 })
        }
      }
      .padding({ left: 32, right: 32 })
      .alignItems(HorizontalAlign.Center)
    }
    .width('100%')
    .height('100%')
    .justifyContent(FlexAlign.Center)
  }

  // ==================================================================
  // 设置面板
  // ==================================================================
  @Builder
  SetupSheet() {
    Column() {
      Column() {
        Text('内核地址')
          .fontSize(15)
          .fontWeight(FontWeight.Medium)
          .fontColor('#F2F2F4')
          .margin({ bottom: 16 })

        Text('地址')
          .fontSize(12)
          .fontColor('#8A8A90')
          .alignSelf(ItemAlign.Start)
        TextInput({ text: this.editBase, placeholder: DEFAULT_BASE })
          .fontSize(14)
          .height(42)
          .margin({ top: 6 })
          .backgroundColor('#1B1D22')
          .fontColor('#E8E8EA')
          .onChange((v: string) => {
            this.editBase = v;
          })

        Text('配对令牌')
          .fontSize(12)
          .fontColor('#8A8A90')
          .margin({ top: 16 })
          .alignSelf(ItemAlign.Start)
        TextInput({ text: this.editToken, placeholder: '虚拟机里敲 deepsleep-core.sh token' })
          .fontSize(14)
          .height(42)
          .margin({ top: 6 })
          .backgroundColor('#1B1D22')
          .fontColor('#E8E8EA')
          .onChange((v: string) => {
            this.editToken = v;
          })

        Text('· 官方融合开发引擎：地址就是 http://127.0.0.1:8756\n· 自建 QEMU：地址填你设置的转发端口\n· 也可以填虚拟机的局域网 IP，如 http://192.168.1.9:8756')
          .fontSize(11)
          .fontColor('#6E6E76')
          .lineHeight(18)
          .margin({ top: 12 })
          .alignSelf(ItemAlign.Start)

        Row() {
          Button('取消')
            .fontSize(14)
            .height(40)
            .layoutWeight(1)
            .backgroundColor('#2A2C31')
            .fontColor('#E8E8EA')
            .onClick(() => {
              this.showSetup = false;
            })
          Button('保存并连接')
            .fontSize(14)
            .height(40)
            .layoutWeight(1)
            .margin({ left: 10 })
            .backgroundColor('#5B8DEF')
            .fontColor('#FFFFFF')
            .onClick(() => {
              this.baseUrl = normalizeUrl(this.editBase);
              this.token = this.editToken.trim();
              this.showSetup = false;
              this.savePref();
              this.probe();
            })
        }
        .width('100%')
        .margin({ top: 22 })
      }
      .width('88%')
      .constraintSize({ maxWidth: 460 })
      .padding(22)
      .backgroundColor('#17181C')
      .borderRadius(16)
    }
    .width('100%')
    .height('100%')
    .backgroundColor('#99000000')
    .justifyContent(FlexAlign.Center)
  }
}
