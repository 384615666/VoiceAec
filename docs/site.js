(() => {
  "use strict";

  const nav = document.getElementById("siteNav");
  const navToggle = document.getElementById("navToggle");
  const navMenu = document.getElementById("navMenu");
  const copyStatus = document.getElementById("copyStatus");

  /* -------------------------------------------------------------------------
     Sticky navigation: soften the bar after scrolling.
     ---------------------------------------------------------------------- */
  const setNavState = () => {
    if (!nav) return;
    nav.classList.toggle("is-scrolled", window.scrollY > 10);

    if (window.scrollY < 180) {
      document.querySelectorAll(".nav-menu a.is-active").forEach((link) => {
        link.classList.remove("is-active");
        link.removeAttribute("aria-current");
      });
    }
  };

  setNavState();
  window.addEventListener("scroll", setNavState, { passive: true });

  /* -------------------------------------------------------------------------
     Mobile menu.
     ---------------------------------------------------------------------- */
  const mobileQuery = window.matchMedia("(max-width: 980px)");

  const isMenuOpen = () => Boolean(nav && nav.classList.contains("is-open"));

  const setMenu = (open, returnFocus = false) => {
    if (!nav || !navToggle) return;
    nav.classList.toggle("is-open", open);
    navToggle.setAttribute("aria-expanded", String(open));
    navToggle.setAttribute("aria-label", open ? "关闭菜单" : "打开菜单");
    if (!open && returnFocus) navToggle.focus();
  };

  if (navToggle && navMenu) {
    navToggle.addEventListener("click", () => {
      setMenu(!isMenuOpen());
    });

    navMenu.addEventListener("click", (event) => {
      if (event.target.closest("a")) setMenu(false);
    });

    document.addEventListener("keydown", (event) => {
      if (event.key === "Escape" && isMenuOpen()) {
        setMenu(false, true);
      }
    });

    document.addEventListener("pointerdown", (event) => {
      if (isMenuOpen() && !nav.contains(event.target)) setMenu(false);
    });

    if (typeof mobileQuery.addEventListener === "function") {
      mobileQuery.addEventListener("change", (event) => {
        if (!event.matches) setMenu(false);
      });
    }
  }

  /* -------------------------------------------------------------------------
     Scrollspy: mark the section currently in the middle of the viewport.
     ---------------------------------------------------------------------- */
  const sectionLinks = navMenu
    ? Array.from(navMenu.querySelectorAll('a[href^="#"]'))
    : [];

  const spySections = [];
  sectionLinks.forEach((link) => {
    const id = link.getAttribute("href").slice(1);
    const section = document.getElementById(id);
    if (section) spySections.push({ link, section });
  });

  if (spySections.length && "IntersectionObserver" in window) {
    const setActive = (activeLink) => {
      sectionLinks.forEach((link) => {
        const active = link === activeLink;
        link.classList.toggle("is-active", active);
        if (active) {
          link.setAttribute("aria-current", "location");
        } else {
          link.removeAttribute("aria-current");
        }
      });
    };

    const spy = new IntersectionObserver(
      (entries) => {
        entries.forEach((entry) => {
          if (!entry.isIntersecting) return;
          const found = spySections.find((item) => item.section === entry.target);
          if (found) setActive(found.link);
        });
      },
      { rootMargin: "-42% 0px -52% 0px", threshold: 0 }
    );

    spySections.forEach((item) => spy.observe(item.section));
  }

  /* -------------------------------------------------------------------------
     Reveal on scroll. Content is visible without JS; this only adds motion.
     ---------------------------------------------------------------------- */
  const reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  const revealItems = Array.from(document.querySelectorAll("[data-reveal]"));

  if (!reduceMotion && "IntersectionObserver" in window && revealItems.length) {
    const revealObserver = new IntersectionObserver(
      (entries, observer) => {
        entries.forEach((entry) => {
          if (!entry.isIntersecting) return;
          entry.target.classList.add("is-visible");
          observer.unobserve(entry.target);
        });
      },
      { rootMargin: "0px 0px -8% 0px", threshold: 0.06 }
    );

    revealItems.forEach((item) => revealObserver.observe(item));
  }

  /* -------------------------------------------------------------------------
     SHA256 copy buttons.
     ---------------------------------------------------------------------- */
  const fallbackCopy = (text) => {
    const field = document.createElement("textarea");
    field.value = text;
    field.setAttribute("readonly", "");
    field.style.position = "fixed";
    field.style.top = "-1000px";
    field.style.opacity = "0";
    document.body.appendChild(field);
    field.select();

    let copied = false;
    try {
      copied = document.execCommand("copy");
    } catch (error) {
      copied = false;
    }

    field.remove();
    return copied;
  };

  document.querySelectorAll("[data-copy]").forEach((button) => {
    button.addEventListener("click", async () => {
      const text = button.getAttribute("data-copy") || "";
      const label = button.querySelector(".copy-label") || button;

      if (!button.dataset.copyLabel) {
        button.dataset.copyLabel = label.textContent;
      }

      const original = button.dataset.copyLabel;
      let copied = false;

      try {
        if (navigator.clipboard && window.isSecureContext) {
          await navigator.clipboard.writeText(text);
          copied = true;
        } else {
          copied = fallbackCopy(text);
        }
      } catch (error) {
        copied = fallbackCopy(text);
      }

      button.classList.toggle("is-copied", copied);
      label.textContent = copied ? "已复制" : "复制失败";

      if (document.activeElement !== button) {
        button.focus({ preventScroll: true });
      }

      if (copyStatus) {
        copyStatus.textContent = copied ? "校验值已复制到剪贴板" : "复制失败，请手动选择校验值";
      }

      window.clearTimeout(button._copyTimer);
      button._copyTimer = window.setTimeout(() => {
        button.classList.remove("is-copied");
        label.textContent = original;
      }, 1800);
    });
  });
})();

/* Interactive control-panel prototype. It simulates UI state only:
   nothing here touches the microphone or system settings. */
(() => {
  "use strict";

  const panel = document.getElementById("demoPanel");
  if (!panel) return;

  const $ = (id) => document.getElementById(id);
  const setText = (id, text) => {
    const el = $(id);
    if (el) el.textContent = text;
  };

  /* Labels and tips follow the desktop app. `drop` is how many dB the
     output sits below the microphone; `cpu` is the simulated range in %. */
  const modes = {
    off: {
      label: "关闭（直通）",
      drop: 0,
      cpu: [0.2, 0.5],
      tip: "麦克风原样转发，不经过模型，适合戴耳机时用",
    },
    "v1.2": {
      label: "均衡模式",
      drop: 4,
      cpu: [2.6, 3.4],
      tip: "推荐默认，日常外放先用它",
    },
    "v1.3": {
      label: "强力模式",
      drop: 7,
      cpu: [6.2, 7.6],
      tip: "消得最彻底，CPU 更高，可能轻微削弱人声",
    },
    "v1.4": {
      label: "纯回声消除模式",
      drop: 3,
      cpu: [1.0, 1.6],
      tip: "只消回声、尽量保留人声，适合近场麦克风",
    },
  };

  const state = {
    mode: "v1.2",
    paused: false,
    seconds: 12,
    hops: 742,
    silent: 12,
    underruns: 8,
    fallback: 0,
    cpu: 3.1,
  };

  /* Simulated levels in dBFS. The output is derived from the microphone. */
  const levels = [
    { key: "mic", base: -40, span: 5 },
    { key: "ref", base: -28, span: 4 },
    { key: "out" },
    { key: "ref2", base: -33, span: 4 },
  ].map((item) => {
    const card = panel.querySelector(`[data-demo-level="${item.key}"]`);
    const meter = card ? card.querySelector(".demo-meter") : null;
    const peakEl = card ? card.querySelector(".demo-peak") : null;
    const bars = [];
    if (meter) {
      for (let i = 0; i < 18; i += 1) {
        bars.push(meter.appendChild(document.createElement("i")));
      }
    }
    return {
      ...item,
      card,
      bars,
      peakEl,
      dbEl: card ? card.querySelector(".demo-db") : null,
      peak: peakEl ? parseFloat(peakEl.textContent) : NaN,
    };
  });

  const formatTime = (seconds) =>
    `${String(Math.floor(seconds / 60)).padStart(2, "0")}:${String(seconds % 60).padStart(2, "0")}`;

  const setDb = (el, value) => {
    if (!el) return;
    el.textContent = value;
    const unit = document.createElement("span");
    unit.textContent = "dB";
    el.appendChild(unit);
  };

  let toastTimer = 0;
  const toast = (message, warn = false) => {
    const el = $("demoToast");
    if (!el) return;
    el.textContent = message;
    el.classList.toggle("is-warn", warn);
    el.classList.add("is-visible");
    window.clearTimeout(toastTimer);
    toastTimer = window.setTimeout(() => el.classList.remove("is-visible"), warn ? 4200 : 2600);
  };

  const sampleCpu = () => {
    if (state.paused) return Math.random() * 0.2;
    const [low, high] = modes[state.mode].cpu;
    return low + Math.random() * (high - low);
  };

  const renderLevels = () => {
    const { paused } = state;
    let micDb = -40;

    levels.forEach((level) => {
      if (!level.card || level.card.hidden) return;

      let db;
      if (level.key === "out") {
        const drop = modes[state.mode].drop;
        db = micDb - drop - (drop ? Math.random() * 1.5 : 0);
      } else {
        db = level.base + Math.random() * level.span;
      }
      if (level.key === "mic") micDb = db;

      const gain = Math.min(1, Math.max(0.08, (db + 60) / 40));
      level.bars.forEach((bar, index) => {
        const wave = Math.sin(Date.now() / 260 + index * 0.7) * 0.5 + 0.5;
        const intensity = paused ? 0 : gain * (0.35 + wave * 0.5 + Math.random() * 0.15);
        bar.style.height = `${Math.round(10 + intensity * 86)}%`;
        bar.classList.toggle("on", intensity > 0.3);
      });

      if (paused) {
        setDb(level.dbEl, "-∞");
        if (level.peakEl) level.peakEl.textContent = "—";
        level.peak = NaN;
        return;
      }

      setDb(level.dbEl, db.toFixed(1));
      /* Peak hold: jump up at once, fall back slowly. */
      level.peak = Number.isFinite(level.peak) ? Math.max(db + 2.5, level.peak - 0.6) : db + 2.5;
      if (level.peakEl) level.peakEl.textContent = `${level.peak.toFixed(1)} dB`;
    });
  };

  const updateStats = () => {
    setText("demoUptime", formatTime(Math.round(state.hops * 0.016)));
    setText("demoHops", state.hops.toLocaleString("en-US"));
    setText("demoRate", state.paused ? "—" : "62.5");
    setText("demoSilent", state.silent.toLocaleString("en-US"));
    setText("demoUnderruns", String(state.underruns));
    setText("demoFallback", String(state.fallback));
    setText("demoCpu", `${state.cpu.toFixed(1)}%`);
    setText(
      "demoStatusText",
      `${state.paused ? "已暂停 · 静音" : "运行中"} · ${formatTime(state.seconds)}`
    );
  };

  /* Uptime keeps counting while paused; processing stats do not. */
  const tick = () => {
    state.seconds += 1;
    if (!state.paused) {
      state.hops += state.seconds % 2 ? 63 : 62;
      if (Math.random() > 0.82) state.silent += 1;
    }
    state.cpu = sampleCpu();
    updateStats();
    renderLevels();
  };

  let timer = window.setInterval(tick, 1000);
  document.addEventListener("visibilitychange", () => {
    if (document.hidden) {
      window.clearInterval(timer);
      timer = 0;
    } else if (!timer) {
      timer = window.setInterval(tick, 1000);
    }
  });

  const setPaused = (paused) => {
    state.paused = paused;
    panel.classList.toggle("is-paused", paused);
    const button = $("demoPause");
    if (button) {
      button.textContent = paused ? "恢复处理与输出" : "暂停并静音";
      button.setAttribute("aria-pressed", String(paused));
    }
    setText("demoRefPill", paused ? "静默" : "播放中");
    state.cpu = sampleCpu();
    updateStats();
    renderLevels();
    toast(paused ? "已暂停并静音，音频采集和推理已停止" : "已恢复处理与输出");
  };

  $("demoPause")?.addEventListener("click", () => setPaused(!state.paused));

  const modeButtons = Array.from(panel.querySelectorAll(".demo-mode"));
  modeButtons.forEach((button) => {
    button.addEventListener("click", () => {
      const mode = button.dataset.mode;
      if (!modes[mode]) return;
      state.mode = mode;
      panel.dataset.mode = mode;
      modeButtons.forEach((item) => {
        const active = item === button;
        item.classList.toggle("is-active", active);
        item.setAttribute("aria-pressed", String(active));
      });
      setText("demoModeBadge", modes[mode].label);
      state.cpu = sampleCpu();
      updateStats();
      renderLevels();
      toast(`已切换到${modes[mode].label}：${modes[mode].tip}`);
    });
  });

  $("demoRef2")?.addEventListener("change", (event) => {
    const enabled = event.target.checked;
    const card = $("demoRef2Card");
    if (card) card.hidden = !enabled;
    $("demoLevels")?.classList.toggle("is-four-up", enabled);
    renderLevels();
    toast(enabled ? "第二参考已启用：把第二只麦克风放在旁人那一侧" : "第二参考已关闭");
  });

  $("demoAutostart")?.addEventListener("change", (event) => {
    toast(event.target.checked ? "已开启开机自启（原型，不改系统设置）" : "已关闭开机自启（原型，不改系统设置）");
  });

  $("demoObs")?.addEventListener("click", () => {
    toast("桌面版会打开「安装到 OBS」：先退出 OBS，再一键安装滤镜");
  });

  /* Wrong-device options carry data-warn: explain the mistake instead. */
  panel.querySelectorAll(".demo-device").forEach((select) => {
    select.addEventListener("change", () => {
      const option = select.selectedOptions[0];
      const warning = option ? option.dataset.warn : "";
      select.closest(".demo-level")?.classList.toggle("is-warn", Boolean(warning));
      toast(warning || `设备已切换：${select.value}`, Boolean(warning));
    });
  });

  panel.dataset.mode = state.mode;
  updateStats();
  renderLevels();
})();
