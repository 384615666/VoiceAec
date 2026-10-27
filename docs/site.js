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
