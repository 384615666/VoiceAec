const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const html = fs.readFileSync(path.join(__dirname, '../wwwroot/index.html'), 'utf8');
let now = 100000, fetchCount = 0, fail = false, paused = false, nextTimer = 1;
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
  return {ok:true,mode:'v1.2',paused,uptimeSec:20,cpuPercent:0,mic:channel,ref:channel,out:channel,
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
    return {ok:true,json:async () => url === '/api/pause' ? {ok:true,paused} : payload()};
  }
};
const script = html.match(/<script>([\s\S]*?)<\/script>/)[1].replace(
  '  init();', '  globalThis.testUI = {state,pollStatus,setPanelVisible,changePaused,renderObsPlugin,refreshObsPlugin};');
vm.runInNewContext(script,context);
const ui = context.testUI;
ui.state.devicesLoaded=true; ui.state.lastDevicesAttempt=now; ui.state.devicePending='mic';
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
})().catch(e=>{console.error(e);process.exitCode=1;});
