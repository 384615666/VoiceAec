(() => {
  "use strict";

  const nav = document.querySelector(".nav");
  const navToggle = document.querySelector(".nav-toggle");
  const navLinks = Array.from(document.querySelectorAll(".nav-link"));
  const progressBar = document.querySelector("#progressBar");
  const toTop = document.querySelector(".to-top");
  const lightbox = document.querySelector("#lightbox");
  const lightboxImage = document.querySelector("#lightboxImage");
  const lightboxCaption = document.querySelector("#lightboxCaption");
  const lightboxClose = document.querySelector(".lightbox-close");

  /* Mobile menu */
  const closeNav = () => {
    if (!nav) return;
    nav.classList.remove("is-open");
    if (navToggle) navToggle.setAttribute("aria-expanded", "false");
  };

  if (navToggle) {
    navToggle.addEventListener("click", () => {
      const open = nav.classList.toggle("is-open");
      navToggle.setAttribute("aria-expanded", String(open));
    });
  }

  navLinks.forEach((link) => {
    link.addEventListener("click", closeNav);
  });

  document.addEventListener("click", (event) => {
    if (!nav || !nav.classList.contains("is-open")) return;
    if (nav.contains(event.target)) return;
    closeNav();
  });

  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape") closeNav();
  });

  window.addEventListener("resize", () => {
    if (window.innerWidth > 900) closeNav();
  });

  /* Reading progress + back to top + scroll spy */
  let ticking = false;

  const updateScrollUI = () => {
    const scrollTop = window.scrollY || document.documentElement.scrollTop;
    const max = Math.max(
      1,
      document.documentElement.scrollHeight - window.innerHeight
    );
    if (progressBar) {
      progressBar.style.width = `${Math.min(100, (scrollTop / max) * 100)}%`;
    }
    if (toTop) {
      toTop.classList.toggle("is-visible", scrollTop > 700);
    }

    /* Scroll spy: the last section whose top passed the nav wins. */
    let current = "";
    const anchor = scrollTop + (parseFloat(getComputedStyle(document.documentElement).getPropertyValue("--nav-h")) || 66) + 40;
    document.querySelectorAll("main section[id]").forEach((section) => {
      if (section.offsetTop <= anchor) current = section.id;
    });
    navLinks.forEach((link) => {
      const href = link.getAttribute("href") || "";
      link.classList.toggle("is-active", href === `#${current}`);
    });

    ticking = false;
  };

  const requestScrollUpdate = () => {
    if (ticking) return;
    ticking = true;
    window.requestAnimationFrame(updateScrollUI);
  };

  window.addEventListener("scroll", requestScrollUpdate, { passive: true });
  window.addEventListener("resize", requestScrollUpdate);
  updateScrollUI();

  if (toTop) {
    toTop.addEventListener("click", () => {
      window.scrollTo({ top: 0, behavior: "smooth" });
    });
  }

  /* Lightbox */
  if (lightbox && lightboxImage && typeof lightbox.showModal === "function") {
    document.querySelectorAll("[data-zoom]").forEach((image) => {
      image.addEventListener("click", () => {
        const src = image.getAttribute("data-zoom") || image.currentSrc || image.src;
        const caption =
          image.getAttribute("data-caption") ||
          image.getAttribute("alt") ||
          "";
        lightboxImage.src = src;
        lightboxImage.alt = image.getAttribute("alt") || "";
        if (lightboxCaption) lightboxCaption.textContent = caption;
        lightbox.showModal();
      });
    });

    const closeLightbox = () => {
      if (lightbox.open) lightbox.close();
    };

    if (lightboxClose) lightboxClose.addEventListener("click", closeLightbox);
    lightbox.addEventListener("click", (event) => {
      if (event.target === lightbox) closeLightbox();
    });
    lightboxImage.addEventListener("click", (event) => event.stopPropagation());
  }

  /* Copy buttons */
  const copyText = async (text) => {
    try {
      await navigator.clipboard.writeText(text);
      return true;
    } catch {
      const area = document.createElement("textarea");
      area.value = text;
      area.setAttribute("readonly", "");
      area.style.position = "fixed";
      area.style.opacity = "0";
      document.body.appendChild(area);
      area.select();
      let ok = false;
      try {
        ok = document.execCommand("copy");
      } catch {
        ok = false;
      }
      area.remove();
      return ok;
    }
  };

  document.querySelectorAll("[data-copy]").forEach((button) => {
    button.addEventListener("click", async () => {
      const value = button.getAttribute("data-copy") || "";
      const original = button.textContent;
      const ok = await copyText(value);
      button.textContent = ok ? "已复制" : "复制失败";
      window.setTimeout(() => {
        button.textContent = original;
      }, 1600);
    });
  });
})();
