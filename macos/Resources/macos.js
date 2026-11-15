/* VoiceAec macOS: adapt the shared panel without forking index.html. */
(function () {
  'use strict';
  document.documentElement.classList.add('platform-mac');

  function isEnglish() {
    return (document.documentElement.lang || 'zh').toLowerCase().indexOf('en') === 0;
  }
  function setText(selector, value) {
    const el = document.querySelector(selector);
    if (el && el.textContent !== value) el.textContent = value;
  }
  function setHTML(selector, value) {
    const el = document.querySelector(selector);
    if (el && el.innerHTML !== value) el.innerHTML = value;
  }
  function hide(selector) {
    const el = document.querySelector(selector);
    if (el && el.style.display !== 'none') el.style.display = 'none';
  }

  function apply() {
    const en = isEnglish();
    hide('#obsPluginOpen');
    hide('#ref2TopSwitch');
    hide('#pinDefaultField');
    hide('#ref2Card');
    hide('#obsPluginDialog');

    function fixWindowsWords(root) {
      if (!root) return;
      const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
      const nodes = [];
      while (walker.nextNode()) nodes.push(walker.currentNode);
      for (const node of nodes) {
        let text = node.nodeValue || '';
        if (text.indexOf('Windows') < 0) continue;
        text = text
          .replace(/Start with Windows/g, 'Start at login')
          .replace(/start-with-Windows/g, 'start-at-login')
          .replace(/Windows administrator/g, 'administrator')
          .replace(/Windows Settings/g, 'System Settings');
        if (text !== node.nodeValue) node.nodeValue = text;
      }
    }
    fixWindowsWords(document.querySelector('#toast'));
    fixWindowsWords(document.querySelector('#srStatus'));
    fixWindowsWords(document.querySelector('#autostartField'));
    fixWindowsWords(document.querySelector('#hintText'));
    const autostartToggle = document.querySelector('#autostartToggle');
    if (autostartToggle) {
      const aria = autostartToggle.getAttribute('aria-label') || '';
      if (aria.indexOf('Windows') >= 0) {
        autostartToggle.setAttribute('aria-label', aria.replace(/Start with Windows/g, 'Start at login'));
      }
    }
    const autostartField = document.querySelector('#autostartField');
    if (autostartField) {
      const title = en ? 'Run in the background at login' : '登录后自动在后台运行，无需手动启动';
      if (autostartField.title !== title) autostartField.title = title;
    }

    const autostartLabel = document.querySelector('#autostartField .top-switch-label');
    if (autostartLabel) {
      const text = en ? 'Start at login' : '开机自启';
      if (autostartLabel.textContent !== text) autostartLabel.textContent = text;
    }

    const close = document.querySelector('#winClose');
    if (close) {
      const label = en ? 'Hide to menu bar' : '隐藏到菜单栏';
      if (close.title !== label) close.title = label;
      if (close.getAttribute('aria-label') !== label) close.setAttribute('aria-label', label);
    }

    setText('#cableBanner .cable-copy strong',
      en ? 'One more step: install the BlackHole virtual audio device' : '还差一步：安装 BlackHole 虚拟声卡');
    setText('#cableBanner .cable-copy p',
      en ? 'VoiceAec sends the cleaned microphone signal to Discord, QQ or your game through BlackHole. BlackHole is free and open source; restart VoiceAec after installing it.'
         : 'VoiceAec 需要 BlackHole 把处理后的声音送给 Discord / QQ / 游戏。BlackHole 免费开源；安装后请重启 VoiceAec。');
    setText('#cableInstall', en ? 'Open BlackHole download' : '打开 BlackHole 下载页');
    setText('#cableDownload', en ? 'Download' : '官网下载');
    setText('#cableRecheck', en ? 'Recheck' : '重新检测');
    setText('#cableNote', en ? 'Installed. Restart VoiceAec, then choose BlackHole 2ch in your voice app.'
                             : '已安装完成。请重启 VoiceAec，并在语音软件里选择 BlackHole 2ch。');
    const download = document.querySelector('#cableDownload');
    if (download && download.href.indexOf('existential.audio') < 0) {
      download.href = 'https://existential.audio/blackhole/';
    }

    const sound = document.querySelector('#soundSettings');
    if (sound) {
      const title = en ? 'Open macOS System Settings → Sound' : '打开 macOS 的「系统设置 → 声音」';
      if (sound.title !== title) sound.title = title;
      const label = sound.querySelector('span');
      const text = en ? 'Open Sound settings' : '打开系统声音设置';
      if (label && label.textContent !== text) label.textContent = text;
    }

    setText('.about-tagline', en ? 'Real-time local audio console · macOS' : '本地实时音频控制台 · macOS');
    setHTML('#aboutDialog .about-links', [
      '<a href="https://github.com/384615666/VoiceAec" target="_blank" rel="noopener">GitHub</a>',
      '<a href="https://github.com/localai-org/LocalVQE" target="_blank" rel="noopener">LocalVQE</a>',
      '<a href="https://existential.audio/blackhole/" target="_blank" rel="noopener">BlackHole</a>'
    ].join(''));
    const aboutCopy = document.querySelectorAll('#aboutDialog > p');
    aboutCopy.forEach(function (p) {
      if (p.classList.contains('about-version') || p.classList.contains('about-fine')) return;
      const text = en
        ? 'Feeds the microphone and system audio into a neural network that removes the echo your teammates hear, then outputs the cleaned signal to the BlackHole virtual audio device. Game audio still plays on your speakers.'
        : '把麦克风与系统声音回采一起送入神经网络模型，消除对方听到的游戏与媒体回声，再输出到 BlackHole 虚拟声卡；游戏声音照常外放。';
      if (p.textContent !== text) p.textContent = text;
    });
    const fine = document.querySelector('#aboutDialog .about-fine');
    if (fine) {
      const text = en
        ? 'Built with LocalVQE (Apache-2.0), ggml, CoreAudio, ScreenCaptureKit and Swift.<br>© 2026 VoiceAec · MIT License'
        : '基于 LocalVQE（Apache-2.0）、ggml、CoreAudio、ScreenCaptureKit 与 Swift 构建。<br>© 2026 VoiceAec · MIT License';
      if (fine.innerHTML !== text) fine.innerHTML = text;
    }
  }

  apply();
  new MutationObserver(apply).observe(document.documentElement, { attributes: true, attributeFilter: ['lang'] });
  window.setInterval(apply, 1500);
})();
