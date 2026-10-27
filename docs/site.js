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
     Interface tour tabs: click + arrow keys + Home / End.
     ---------------------------------------------------------------------- */
  const tablist = document.querySelector('[role="tablist"]');

  if (tablist) {
    const tabs = Array.from(tablist.querySelectorAll('[role="tab"]'));
    const panels = tabs
      .map((tab) => document.getElementById(tab.getAttribute("aria-controls")))
      .filter(Boolean);

    const activateTab = (tab, moveFocus) => {
      tabs.forEach((item) => {
        const selected = item === tab;
        item.setAttribute("aria-selected", String(selected));
        item.tabIndex = selected ? 0 : -1;
      });

      panels.forEach((panel) => {
        const selected = panel.id === tab.getAttribute("aria-controls");
        panel.hidden = !selected;
      });

      if (moveFocus) tab.focus();
    };

    tabs.forEach((tab) => {
      tab.addEventListener("click", () => activateTab(tab, false));
    });

    tablist.addEventListener("keydown", (event) => {
      const currentIndex = tabs.indexOf(document.activeElement);
      if (currentIndex < 0) return;

      let nextIndex = -1;

      switch (event.key) {
        case "ArrowRight":
        case "ArrowDown":
          nextIndex = (currentIndex + 1) % tabs.length;
          break;
        case "ArrowLeft":
        case "ArrowUp":
          nextIndex = (currentIndex - 1 + tabs.length) % tabs.length;
          break;
        case "Home":
          nextIndex = 0;
          break;
        case "End":
          nextIndex = tabs.length - 1;
          break;
        default:
          return;
      }

      event.preventDefault();
      activateTab(tabs[nextIndex], true);
    });
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

/* Interactive control-panel prototype. It simulates UI state only. */
(() => {
  "use strict";

  const panel = document.getElementById("demoPanel");
  if (!panel) return;

  const $ = (id) => document.getElementById(id);
  const modeLabels = {
    off: "关闭（直通）",
    "v1.2": "均衡模式",
    "v1.3": "强力模式",
    "v1.4": "纯回声消除",
  };

  const state = {
    paused: false,
    seconds: 12,
    hops: 742,
    silent: 12,
    underruns: 8,
    fallback: 0,
    cpu: 1.4,
  };

  const levels = [
    { key: "mic", meter: "demoMicMeter", db: "demoMicDb", base: -60, span: 7 },
    { key: "ref", meter: "demoRefMeter", db: "demoRefDb", base: -34, span: 6 },
    { key: "out", meter: "demoOutMeter", db: "demoOutDb", base: -31, span: 6 },
    { key: "ref2", meter: "demoRef2Meter", db: "demoRef2Db", base: -30, span: 6 },
  ].map((item) => {
    const meter = $(item.meter);
    const bars = [];
    if (meter) {
      for (let i = 0; i < 18; i += 1) {
        const bar = document.createElement("i");
        meter.appendChild(bar);
        bars.push(bar);
      }
    }
    return { ...item, bars, dbEl: $(item.db) };
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
  const toast = (message) => {
    const el = $("demoToast");
    if (!el) return;
    el.textContent = message;
    el.classList.add("is-visible");
    window.clearTimeout(toastTimer);
    toastTimer = window.setTimeout(() => el.classList.remove("is-visible"), 2200);
  };

  const renderLevels = () => {
    const paused = state.paused;
    levels.forEach((level) => {
      if (level.key === "ref2" && $("demoRef2Card")?.hidden) return;
      level.bars.forEach((bar, index) => {
        const wave = Math.sin((Date.now() / 260) + index * 0.7) * 0.5 + 0.5;
        const intensity = paused ? 0.04 : 0.22 + wave * 0.55 + Math.random() * 0.18;
        bar.style.height = `${Math.round(8 + intensity * 80)}%`;
        bar.classList.toggle("on", !paused && intensity > 0.3);
      });
      if (paused) {
        setDb(level.dbEl, "−∞");
      } else {
        setDb(level.dbEl, (level.base + Math.random() * level.span).toFixed(1));
      }
    });
  };

  const updateStats = () => {
    $("demoUptime").textContent = formatTime(state.seconds);
    $("demoHops").textContent = state.hops.toLocaleString("en-US");
    $("demoRate").textContent = state.paused ? "—" : "62.5";
    $("demoSilent").textContent = state.silent.toLocaleString("en-US");
    $("demoUnderruns").textContent = String(state.underruns);
    $("demoFallback").textContent = String(state.fallback);
    $("demoCpu").textContent = `${state.cpu.toFixed(1)}%`;
    $("demoStatusText").textContent = state.paused
      ? `已暂停 · 静音 · ${formatTime(state.seconds)}`
      : `运行中 · ${formatTime(state.seconds)}`;
  };

  const tick = () => {
    if (state.paused) return;
    state.seconds += 1;
    state.hops += 62 + (Math.random() > 0.5 ? 1 : 0);
    if (Math.random() > 0.82) state.silent += 1;
    state.cpu = 1.2 + Math.random() * 0.9;
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
    button.textContent = paused ? "恢复处理与输出" : "暂停并静音";
    button.setAttribute("aria-pressed", String(paused));
    updateStats();
    renderLevels();
    toast(paused ? "已暂停：原型不会处理音频" : "已恢复：继续模拟面板");
  };

  $("demoPause")?.addEventListener("click", () => setPaused(!state.paused));

  document.querySelectorAll(".demo-mode").forEach((button) => {
    button.addEventListener("click", () => {
      const mode = button.dataset.mode;
      if (!mode || !modeLabels[mode]) return;
      document.querySelectorAll(".demo-mode").forEach((item) => {
        const active = item === button;
        item.classList.toggle("is-active", active);
        item.setAttribute("aria-pressed", String(active));
      });
      $("demoModeBadge").textContent = modeLabels[mode];
      toast(`已切换到${modeLabels[mode]}（原型）`);
    });
  });

  $("demoRef2")?.addEventListener("change", (event) => {
    const enabled = event.target.checked;
    const card = $("demoRef2Card");
    const grid = $("demoLevels");
    if (card) card.hidden = !enabled;
    grid?.classList.toggle("is-four-up", enabled);
    renderLevels();
    toast(enabled ? "第二参考已开启（原型）" : "第二参考已关闭（原型）");
  });

  $("demoAutostart")?.addEventListener("change", (event) => {
    toast(event.target.checked ? "开机自启已开启（原型）" : "开机自启已关闭（原型）");
  });

  $("demoObs")?.addEventListener("click", () => {
    toast("OBS 插件安装只在 Windows 桌面版可用");
  });

  document.querySelectorAll(".demo-device").forEach((select) => {
    select.addEventListener("change", () => {
      toast(`已选择 ${select.value}（原型）`);
    });
  });

  updateStats();
  renderLevels();
})();
