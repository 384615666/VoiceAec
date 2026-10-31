const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const html = fs.readFileSync(path.join(__dirname, '../wwwroot/index.html'), 'utf8');
let now = 100000, fetchCount = 0, fail = false, paused = false, nextTimer = 1, lang = 'zh';
const timers = new Map(), elements = new Map();
function element(id) {
  if (elements.has(id)) return elements.get(id);
  const classes = new Set();
  const e = {
    id, textContent: '', style: {}, dataset: {}, hidden: false, content: 'test-token',
    classList: { add: (...v) => v.forEach(x => classes.add(x)), remove: (...v) => v.forEach(x => classes.delete(x)),
      toggle: (v, on) => (on ? classes.add(v) : classes.delete(v)), contains: v => classes.has(v) },
    setAttribute(k,v) { this[k] = v; }, getAttribute(k) { return this[k]; }, removeAttribute(k) { delete this[k]; },
    addEventListener() {}, querySelectorAll() { return []; }, querySelector() { return element('child'); },
    contains() { return false; }
  };
  elements.set(id, e); return e;
}
const document = {
  hidden: false, documentElement: element('html'),
  getElementById: element, querySelector: () => element('meta'), querySelectorAll: () => [], addEventListener() {}
};
function payload() {
  const channel = {name:'Test device',levelDb:-40,bufferMs:0,active:false};
  return {ok:true,lang,mode:'v1.2',paused,uptimeSec:20,cpuPercent:0,mic:channel,ref:channel,out:channel,
    stats:{hops:10,processedSec:1,hopsPerSec:paused?0:62.5},autostart:false};
}
const window = { addEventListener() {}, setTimeout: (fn,delay) => {
  const id=nextTimer++; timers.set(id,{fn,delay});return id;
}, clearTimeout: id => timers.delete(id) };
const context = {document,window,Intl,console,AbortController,Element:class {},
  Date:class extends Date { static now() {return now;} },
  fetch: async (url, options) => {
    fetchCount++; assert.equal(options.headers['X-VoiceAec-Token'], 'test-token');
    if (fail) throw new Error('temporary connection failure');
    if (url === '/api/pause') paused = options.body === 'true';
    if (url === '/api/lang') {
      lang = String(options.body || '').trim().toLowerCase().startsWith('en') ? 'en' : 'zh';
      return {ok:true, status:200, json:async () => ({ok:true,lang})};
    }
    return {ok:true,json:async () => url === '/api/pause' ? {ok:true,paused} : payload()};
  }
};
const script = html.match(/<script>([\s\S]*?)<\/script>/)[1].replace(
  '  init();',
  '  globalThis.testUI = {state,pollStatus,setPanelVisible,changePaused,renderObsPlugin,refreshObsPlugin,changeLanguage,applyLanguage,getLanguage:() => currentLang,I18N_DICT,I18N_RULES,translateCore,applyElementAttrs,renderAutostart,renderDevicePickers,renderRef2,updateChannel,refreshControls,showToast};');
vm.runInNewContext(script,context);
const ui = context.testUI;
ui.state.devicesLoaded=true; ui.state.lastDevicesAttempt=now; ui.state.devicePending='mic';
const CJK = /[\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff]/;
async function settlePolling() { for (let i = 0; i < 40 && ui.state.polling; i += 1) await Promise.resolve(); }
(async () => {
  await ui.pollStatus();
  assert.equal(ui.state.connected,true);
  assert.equal([...timers.values()].at(-1).delay,500);
  fail=true; now+=500; await ui.pollStatus();
  assert.equal(ui.state.offline,false);
  assert.equal(element('offlineOverlay').classList.contains('is-visible'),false);
  console.log('PASS: one failed request keeps the panel available');
  fail=false; now+=500; await ui.pollStatus();
  assert.equal(ui.state.retryCount,0);
  fail=true;
  for(let i=0;i<4;i++){now+=1100;await ui.pollStatus();}
  assert.equal(ui.state.offline,true);
  assert.equal(element('offlineOverlay').classList.contains('is-visible'),true);
  assert(html.includes('音频处理可能仍在继续'));
  console.log('PASS: persistent failures show connection loss without claiming audio stopped');
  fail=false; await ui.pollStatus();
  assert.equal(ui.state.offline,false);
  ui.setPanelVisible(false);
  const count=fetchCount; await ui.pollStatus();
  assert.equal(fetchCount,count);
  assert.equal([...timers.values()].some(timer => timer.fn === ui.pollStatus),false);
  console.log('PASS: hidden panel stops status polling');
  ui.state.panelVisible=true; paused=true; await ui.pollStatus();
  assert.equal([...timers.values()].at(-1).delay,2000);
  assert.equal(element('pauseOutput')['aria-pressed'],'true');
  assert.equal(element('hopsNote').textContent,'已暂停推理');
  console.log('PASS: paused panel uses slower polling and shows paused inference');
  ui.renderObsPlugin({ok:true,managed:false,installed:true,payloadAvailable:true,obsRunning:false,
    canInstall:true,canUninstall:false,target:'test-plugin-path'});
  assert.equal(element('obsPluginInstall').disabled,false);
  assert.equal(element('obsPluginUninstall').disabled,true);
  assert.match(element('obsPluginState').textContent,/已有插件/);
  ui.renderObsPlugin({ok:true,managed:true,payloadAvailable:true,obsRunning:true,canInstall:false,canUninstall:false});
  assert.equal(element('obsPluginInstall').disabled,true);
  assert.equal(element('obsPluginUninstall').disabled,true);
  assert.match(element('obsPluginState').textContent,/OBS 正在运行/);
  console.log('PASS: OBS controls protect unmanaged plugins and running OBS');
  element('obsPluginDialog').open=false;
  const beforeObs=fetchCount;await ui.refreshObsPlugin();assert.equal(fetchCount,beforeObs);
  console.log('PASS: OBS status does not poll while its dialog is closed');

  // ── i18n ────────────────────────────────────────────────────
  assert.equal(ui.getLanguage(), 'zh', 'default language is zh');
  assert(html.includes('<meta name="voiceaec-lang" content="__VOICEAEC_LANG__">'), 'language meta placeholder injected');
  assert(html.includes('popover="manual"'), 'toast uses a top-layer popover');
  assert(html.includes('id="aboutLang"'), 'language button exists');
  const aboutStart = html.indexOf('id="aboutDialog"');
  const aboutMarkup = html.slice(aboutStart, html.indexOf('</dialog>', aboutStart));
  assert(aboutMarkup.includes('id="aboutLang"'), 'language button inside about dialog');
  assert(aboutMarkup.indexOf('id="aboutLang"') < aboutMarkup.indexOf('id="aboutClose"'), 'language button next to close');
  assert(aboutMarkup.indexOf('class="obs-dialog-head"') < aboutMarkup.indexOf('id="aboutLang"'), 'language button inside dialog head');
  console.log('PASS: about dialog exposes a language button next to close');

  const dict = ui.I18N_DICT, rules = ui.I18N_RULES;
  assert(Object.keys(dict).length >= 200, 'dictionary has enough entries');
  assert(rules.length >= 30, 'patterns cover dynamic templates');
  for (const [zh, en] of Object.entries(dict)) {
    assert(CJK.test(zh), 'dictionary keys are Chinese originals: ' + zh);
    assert(typeof en === 'string' && en.trim() !== '', 'translation is not empty: ' + zh);
    assert(!CJK.test(en), 'translation has no CJK: ' + zh + ' -> ' + en);
    assert(!en.includes('${'), 'translation has no template placeholder: ' + zh);
  }
  console.log('PASS: dictionary is complete, English-only and placeholder-free');

  const styleEnd = html.indexOf('</style>') + '</style>'.length;
  const scriptStart = html.indexOf('<script>');
  const staticHtml = html.slice(styleEnd, scriptStart);
  const staticStrings = new Set();
  for (const m of staticHtml.replace(/<svg[\s\S]*?<\/svg>/g, '').matchAll(/>([^<>]+)</g)) {
    const text = m[1].replace(/\s+/g, ' ').trim();
    if (text && CJK.test(text)) staticStrings.add(text);
  }
  for (const m of staticHtml.matchAll(/\b(title|placeholder|aria-label|aria-description|alt|data-caption|data-hint|aria-valuetext)="([^"]*)"/g)) {
    if (CJK.test(m[2])) staticStrings.add(m[2]);
  }
  const staticMisses = [...staticStrings].filter((s) => ui.translateCore(s) === s);
  assert(staticStrings.size >= 120, 'inventory still finds the static Chinese strings: ' + staticStrings.size);
  assert.deepEqual(staticMisses, [], 'every static Chinese string has an English translation');
  console.log('PASS: all ' + staticStrings.size + ' static Chinese strings are translated');

  const dynamicSamples = [
    '更换麦克风设备，当前：正在获取设备…', '正在切换到 均衡模式…', '正在切换到 Test device…',
    '第二参考已设为 Test device。', '麦克风设备已切换为 Test device。',
    '正在自动重连 · 第 3 次', '运行中 · 00:20', '已暂停 · 静音 · 00:20',
    '峰值 -12.3 dB', '缓冲 48 ms', '服务返回异常（HTTP 500）',
    '安装未完成：请到官网手动安装', '语言切换失败：服务返回异常（HTTP 500）',
    '每秒推理 62.5 跳每秒，正常值 62.5', '消除强度 4/5', '开启第二参考，已关闭',
    '开机自启，正在读取', '正在启用第二参考 Test device', '正在启用 Test device',
    'Test device（点击更换设备）'
  ];
  const dynamicMisses = dynamicSamples.filter((s) => {
    const en = ui.translateCore(s);
    return en === s || CJK.test(en.replace(/Test device/g, ''));
  });
  assert.deepEqual(dynamicMisses, [], 'dynamic template samples are translated');
  assert.equal(ui.translateCore('正在启用 Test device'), 'Enabling Test device');
  assert.ok(ui.translateCore('正在启用 扬声器 (Realtek(R) Audio)').startsWith('Enabling '), 'pending reference variant keeps device name but translates the template');
  console.log('PASS: ' + dynamicSamples.length + ' dynamic template samples are translated');

  await ui.changeLanguage();
  await settlePolling();
  assert.equal(ui.getLanguage(), 'en', 'language switched to en');
  assert.equal(document.documentElement.lang, 'en');
  assert.equal(document.title, 'VoiceAec — Real-Time Echo Cancellation');
  assert.equal(element('aboutLang').textContent, '中文');
  await ui.pollStatus();
  ui.renderObsPlugin({ok:true,managed:true,payloadAvailable:true,obsRunning:true,canInstall:false,canUninstall:false});

  // ── P2-1 regression: dynamic attributes stay on their latest value ──
  ui.state.autostartKnown = true;
  ui.state.autostart = false;
  ui.state.autostartPending = false;
  ui.renderAutostart();
  const autostart = element('autostartToggle');
  assert.equal(autostart['aria-label'], 'Start with Windows: off');
  ui.applyElementAttrs(autostart);
  assert.equal(autostart['aria-label'], 'Start with Windows: off', 'observer must not restore a stale attribute translation');
  ui.state.autostart = true;
  ui.renderAutostart();
  ui.applyElementAttrs(autostart);
  assert.equal(autostart['aria-label'], 'Start with Windows: on');
  ui.state.autostartPending = true;
  ui.renderAutostart();
  ui.applyElementAttrs(autostart);
  assert.equal(autostart['aria-label'], 'Start with Windows: saving');
  ui.state.autostartPending = false;
  ui.state.autostart = true;

  ui.state.deviceNames.mic = 'Test device';
  ui.state.currentDevices.mic = 'Test device';
  ui.state.deviceNames.ref = 'Ref device';
  ui.state.currentDevices.ref = 'Ref device';
  ui.state.deviceNames.out = 'Out device';
  ui.state.currentDevices.out = 'Out device';
  ui.state.devicesStatus = 'ready';
  ui.state.devicePending = null;
  ui.state.devicePendingName = '';
  ui.renderDevicePickers();
  const micButton = element('micDeviceBtn');
  assert.match(micButton.title, /Test device/, 'device tooltip keeps the current device');
  assert.match(micButton['aria-label'], /Change microphone device, current: Test device/);
  ui.applyElementAttrs(micButton);
  assert.match(micButton.title, /Test device/, 'observer keeps the latest device tooltip');

  ui.updateChannel('mic', {name:'Test device', levelDb:-23.4, bufferMs:48, active:true});
  const micMeter = element('micMeter');
  assert.equal(micMeter['aria-valuetext'], '-23.4 dBFS');
  ui.applyElementAttrs(micMeter);
  assert.equal(micMeter['aria-valuetext'], '-23.4 dBFS', 'observer keeps the latest level aria-valuetext');
  ui.updateChannel('mic', {name:'Test device', levelDb:-11.1, bufferMs:48, active:true});
  assert.equal(micMeter['aria-valuetext'], '-11.1 dBFS');

  ui.state.paused = true;
  ui.refreshControls();
  const pauseButton = element('pauseOutput');
  assert.equal(pauseButton.title, 'Resume audio capture, inference and output');
  ui.applyElementAttrs(pauseButton);
  assert.equal(pauseButton.title, 'Resume audio capture, inference and output');
  ui.state.paused = false;
  ui.refreshControls();
  assert.equal(pauseButton.title, 'Pause capture, inference and output; mute and reduce CPU load');
  ui.state.paused = true;
  ui.refreshControls();

  // P2-3: pending second reference kind text
  ui.state.ref2Visible = true;
  ui.state.devicePending = 'ref2';
  ui.state.devicePendingName = 'Test device';
  ui.renderRef2();
  assert.equal(element('ref2KindText').textContent, 'Enabling Test device');
  ui.state.devicePending = null;
  ui.state.devicePendingName = '';
  ui.renderRef2();
  console.log('PASS: dynamic attributes/labels survive the i18n observer and pending ref2 has no CJK');

  // P3-2: toast is promoted to the top layer so dialogs cannot cover it
  const toastEl = element('toast');
  let toastOpen = false;
  toastEl.showPopover = () => { toastOpen = true; };
  toastEl.hidePopover = () => { toastOpen = false; };
  toastEl.matches = (selector) => (selector === ':popover-open' ? toastOpen : false);
  ui.showToast('Language switch failed: test', 'error');
  assert.equal(toastOpen, true, 'toast is promoted to the top layer');
  assert.equal(toastEl.textContent, 'Language switch failed: test');
  console.log('PASS: toast is shown in the top layer above modal dialogs');

  const leftovers = [];
  for (const e of elements.values()) {
    if (e.id === 'aboutLang') continue;
    if (typeof e.textContent === 'string' && CJK.test(e.textContent)) leftovers.push([e.id, 'textContent', e.textContent]);
    for (const attr of ['title', 'aria-label', 'aria-valuetext', 'placeholder', 'alt']) {
      if (typeof e[attr] === 'string' && CJK.test(e[attr])) leftovers.push([e.id, attr, e[attr]]);
    }
  }
  assert.deepEqual(leftovers, [], 'English mode renders no Chinese in the DOM');
  console.log('PASS: English mode renders without Chinese leftovers');

  await ui.changeLanguage();
  await settlePolling();
  assert.equal(ui.getLanguage(), 'zh', 'language switched back to zh');
  assert.equal(document.documentElement.lang, 'zh-CN');
  assert.equal(document.title, 'VoiceAec');
  assert.equal(element('aboutLang').textContent, 'EN');
  await ui.pollStatus();
  assert.equal(element('hopsNote').textContent, '已暂停推理');
  assert.equal(element('pauseLabel').textContent, '恢复处理与输出');
  assert.equal(element('aboutLang').title, 'Switch to English');
  assert.equal(element('retryText').textContent, '正在自动重连', 'hidden reconnect copy matches the baseline after switching back');
  console.log('PASS: switching back restores the original Chinese copy');
})().catch(e=>{console.error(e);process.exitCode=1;});
